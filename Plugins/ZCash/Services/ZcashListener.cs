using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Altcoins;
using BTCPayServer.Plugins.ZCash.Configuration;
using BTCPayServer.Plugins.ZCash.Payments;
using BBTCPayServer.Plugins.ZCash.RPC;
using BTCPayServer.Plugins.ZCash.Data;
using BTCPayServer.Plugins.ZCash.Data.Models;
using BTCPayServer.Plugins.ZCash.Utils;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitpayClient;
using NBXplorer;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static BTCPayServer.Client.Models.InvoicePaymentMethodDataModel;
using BTCPayServer.Services;
using BTCPayServer.Plugins.ZCash.RPC;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.ZCash.Services
{
    public class ZcashListener : EventHostedServiceBase
    {
        private readonly InvoiceRepository _invoiceRepository;
        private readonly EventAggregator _eventAggregator;
        private readonly ZcashRPCProvider _ZcashRpcProvider;
        private readonly ZcashLikeConfiguration _ZcashLikeConfiguration;
        private readonly BTCPayNetworkProvider _networkProvider;
        private readonly ILogger<ZcashListener> _logger;
        private readonly PaymentService _paymentService;
        private readonly InvoiceActivator _invoiceActivator;
        private readonly PaymentMethodHandlerDictionary _handlers;
        private readonly IDbContextFactory<ZcashPluginDbContext> _dbContextFactory;

        public ZcashListener(InvoiceRepository invoiceRepository,
            EventAggregator eventAggregator,
            ZcashRPCProvider ZcashRpcProvider,
            ZcashLikeConfiguration ZcashLikeConfiguration,
            BTCPayNetworkProvider networkProvider,
            ILogger<ZcashListener> logger,
            PaymentService paymentService,
            InvoiceActivator invoiceActivator,
            PaymentMethodHandlerDictionary handlers,
            IDbContextFactory<ZcashPluginDbContext> dbContextFactory) : base(eventAggregator, logger)
        {
            _invoiceRepository = invoiceRepository;
            _eventAggregator = eventAggregator;
            _ZcashRpcProvider = ZcashRpcProvider;
            _ZcashLikeConfiguration = ZcashLikeConfiguration;
            _networkProvider = networkProvider;
            _logger = logger;
            _paymentService = paymentService;
            _invoiceActivator = invoiceActivator;
            _handlers = handlers;
            _dbContextFactory = dbContextFactory;
        }

        protected override void SubscribeToEvents()
        {
            base.SubscribeToEvents();
            Subscribe<ZcashEvent>();
            Subscribe<ZcashRPCProvider.ZcashDaemonStateChange>();
        }

        protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
        {
            if (evt is ZcashRPCProvider.ZcashDaemonStateChange stateChanged)
            {
                if (_ZcashRpcProvider.IsAvailable(stateChanged.CryptoCode))
                {
                    _logger.LogInformation($"{stateChanged.CryptoCode} just became available");
                    await RecoverPayments(stateChanged.CryptoCode, cancellationToken);
                    await UpdateAnyPendingZcashLikePayment(stateChanged.CryptoCode);
                }
                else
                {
                    _logger.LogInformation($"{stateChanged.CryptoCode} just became unavailable");
                }
            }
            else if (evt is ZcashEvent zcashEvent)
            {
                if (!_ZcashRpcProvider.IsAvailable(zcashEvent.CryptoCode))
                    return;

                if (!string.IsNullOrEmpty(zcashEvent.BlockHash))
                {
                    await RecoverPayments(zcashEvent.CryptoCode, cancellationToken);
                    await OnNewBlock(zcashEvent.CryptoCode);
                }
                else if (zcashEvent.Reconcile)
                {
                    await RecoverPayments(zcashEvent.CryptoCode, cancellationToken);
                    await UpdateAnyPendingZcashLikePayment(zcashEvent.CryptoCode);
                }
                if (!string.IsNullOrEmpty(zcashEvent.TransactionHash) && zcashEvent.AccountIndex is not null)
                {
                    await OnTransactionUpdated(zcashEvent.CryptoCode, zcashEvent.TransactionHash, (long) zcashEvent.AccountIndex,
                        zcashEvent.FromSubscription ? "subscription" : "poll");
                }
            }
        }

        private async Task ReceivedPayment(InvoiceEntity invoice, PaymentEntity payment, string detectionSource)
        {
            _logger.LogInformation(
                "Invoice {InvoiceId} received payment {Value} {Currency} {PaymentId} via {DetectionSource} at {ReceivedAt:O}",
                invoice.Id, payment.Value, payment.Currency, payment.Id, detectionSource, DateTimeOffset.UtcNow);


            var prompt = invoice.GetPaymentPrompt(payment.PaymentMethodId);

            if (prompt != null &&
                prompt.Activated &&
                prompt.Destination == payment.Destination &&
                prompt.Calculate().Due > 0.0m)
            {
                await _invoiceActivator.ActivateInvoicePaymentMethod(invoice.Id, payment.PaymentMethodId, true);
                invoice = await _invoiceRepository.GetInvoice(invoice.Id);
            }

            _eventAggregator.Publish(
                new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
        }

        private async Task<IReadOnlyList<WalletTransfer>> SafeGetTransfersAsync(
            IZcashWalletBackend walletBackend,
            long accountIndex,
            List<long> subaddrIndices)
        {
            try
            {
                return await walletBackend.GetTransfersAsync(accountIndex, subaddrIndices.Distinct().ToList());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"get_transfers failed for account {accountIndex}: {ex.Message}");
                return null;
            }
        }

        private async Task UpdatePaymentStates(string cryptoCode, InvoiceEntity[] invoices)
        {
            if (!invoices.Any())
                return;

            var walletBackend = _ZcashRpcProvider.WalletBackends[cryptoCode];
            var network = _networkProvider.GetNetwork(cryptoCode);

            var paymentId = PaymentTypes.CHAIN.GetPaymentMethodId(network.CryptoCode);
            var handler = (ZcashLikePaymentMethodHandler)_handlers[paymentId];

            // Prepare expanded invoices
            var expandedInvoices = invoices.Select(entity => (
                Invoice: entity,
                ExistingPayments: GetAllZcashLikePayments(entity, cryptoCode),
                Prompt: entity.GetPaymentPrompt(paymentId),
                PaymentMethodDetails: handler.ParsePaymentPromptDetails(entity.GetPaymentPrompt(paymentId).Details)
            ))
            .Select(tuple => (
                tuple.Invoice,
                tuple.PaymentMethodDetails,
                tuple.Prompt,
                ExistingPayments: tuple.ExistingPayments.Select(ep => (
                    Payment: ep,
                    PaymentData: handler.ParsePaymentDetails(ep.Details),
                    tuple.Invoice,
                    tuple.Prompt
                ))
            ))
            .ToList();

            var existingPaymentData = expandedInvoices.SelectMany(tuple => tuple.ExistingPayments).ToList();

            // Build account->subaddress query
            var accountToAddressQuery = new Dictionary<long, List<long>>();
            foreach (var expandedInvoice in expandedInvoices)
            {
                var addressIndexList = accountToAddressQuery.GetValueOrDefault(
                    expandedInvoice.PaymentMethodDetails.AccountIndex,
                    new List<long>()
                );

                addressIndexList.AddRange(expandedInvoice.ExistingPayments.Select(ep => ep.PaymentData.SubaddressIndex));
                addressIndexList.Add(expandedInvoice.PaymentMethodDetails.AddressIndex);
                var historicalAddresses = expandedInvoice.Invoice.Addresses
                    .Where(a => a.PaymentMethodId == paymentId).Select(a => a.Address).ToList();
                await using var receiverContext = await _dbContextFactory.CreateDbContextAsync();
                addressIndexList.AddRange(await receiverContext.Receivers
                    .Where(r => r.AccountIndex == expandedInvoice.PaymentMethodDetails.AccountIndex && historicalAddresses.Contains(r.UnifiedAddress))
                    .Select(r => (long)r.AddressIndex).ToListAsync());
                accountToAddressQuery[expandedInvoice.PaymentMethodDetails.AccountIndex] = addressIndexList;
            }

            Console.WriteLine($"Send RPC commands");
            
            if (_ZcashRpcProvider.WalletBackends.TryGetValue(cryptoCode, out var backend))
            {
                await backend.SynchronizeAsync(accountToAddressQuery.Keys.ToList());
            }

            // Send RPC commands
            var tasks = accountToAddressQuery.ToDictionary(
                kvp => kvp.Key,
                kvp => SafeGetTransfersAsync(walletBackend, kvp.Key, kvp.Value)
            );

            await Task.WhenAll(tasks.Values);
            Console.WriteLine($"Send RPC commands awaited. tasks.Count: {tasks.Count}");

            var updatedPaymentEntities = new List<(PaymentEntity Payment, InvoiceEntity Invoice)>();
            
            // Process each account's transfers
            foreach (var kvp in tasks)
            {
                var response = await kvp.Value;
                if (response == null)
                    continue;
                Console.WriteLine($"Account {kvp.Key}: got {response?.Count ?? -1} incoming transfers");
                var transfers = response;
                if (transfers == null || !transfers.Any())
                    continue;
                
                // var groupedTransfers = transfers
                    // .GroupBy(t => (t.TransactionId, t.Address));

                var transfersWithInvoice = new List<(WalletTransfer Transfer, InvoiceEntity Invoice, string UnifiedAddress)>();

                foreach (var transfer in transfers)
                {
                    await using var ctx = await _dbContextFactory.CreateDbContextAsync();
                    var ua = await ctx.Receivers.FirstOrDefaultAsync(r =>
                        r.AccountIndex == transfer.AccountIndex &&
                        (r.UnifiedAddress == transfer.Address ||
                        r.SaplingAddress == transfer.Address ||
                        r.OrchardAddress == transfer.Address ||
                        r.TransparentAddress == transfer.Address));
                    if (ua == null) continue;

                    var invoice = await _invoiceRepository.GetInvoiceFromAddress(paymentId, ua.UnifiedAddress);
                    if (invoice == null) continue;

                    transfersWithInvoice.Add((transfer, invoice, ua.UnifiedAddress));
                }

                var groupedByTxAndInvoice = transfersWithInvoice
                    .GroupBy(t => (t.Transfer.TransactionId, t.Invoice.Id, t.Transfer.AccountIndex, t.Transfer.AddressIndex));

                foreach (var group in groupedByTxAndInvoice)
                {
                    var first = group.First();
                    var totalAmount = group.Sum(t => t.Transfer.Amount);

                    await HandlePaymentData(
                        cryptoCode,
                        first.UnifiedAddress,
                        totalAmount,
                        first.Transfer.AccountIndex,
                        first.Transfer.AddressIndex,
                        first.Transfer.TransactionId,
                        first.Transfer.Confirmations,
                        first.Transfer.Height,
                        first.Invoice,
                        updatedPaymentEntities
                    );
                }
            }

            // Update all payments at once
            if (updatedPaymentEntities.Any())
            {
                await _paymentService.UpdatePayments(updatedPaymentEntities.Select(tuple => tuple.Payment).ToList());

                foreach (var group in updatedPaymentEntities.GroupBy(tuple => tuple.Invoice))
                {
                    _eventAggregator.Publish(new Events.InvoiceNeedUpdateEvent(group.Key.Id));
                }
            }
        }

        private async Task OnNewBlock(string cryptoCode)
        {
            await UpdateAnyPendingZcashLikePayment(cryptoCode);
            _eventAggregator.Publish(new NewBlockEvent() { PaymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode) });
        }
        

        private async Task OnTransactionUpdated(string cryptoCode, string transactionHash, long accountIndex, string detectionSource)
        {
            var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode);
            var transfer = await _ZcashRpcProvider.WalletBackends[cryptoCode]
                .GetTransactionAsync(accountIndex, transactionHash);

            var paymentsToUpdate = new List<(PaymentEntity Payment, InvoiceEntity invoice)>();

            // All receiver forms for a diversifier share one payment identity.
            foreach (var destination in transfer.Transfers.GroupBy(t => (t.AccountIndex, t.AddressIndex)))
            {
                await using var ctx = await _dbContextFactory.CreateDbContextAsync();
                var receiver = await ctx.Receivers.FirstOrDefaultAsync(r =>
                    r.AccountIndex == destination.Key.AccountIndex && r.AddressIndex == destination.Key.AddressIndex);
                if (receiver == null) continue;
                var invoice = await _invoiceRepository.GetInvoiceFromAddress(paymentMethodId, receiver.UnifiedAddress);
                if (invoice == null) continue;
                await HandlePaymentData(cryptoCode, receiver.UnifiedAddress,
                    destination.Sum(t => t.Amount), destination.Key.AccountIndex, destination.Key.AddressIndex,
                    transfer.TransactionId, transfer.Confirmations, transfer.Height, invoice, paymentsToUpdate, detectionSource);
            }

            if (paymentsToUpdate.Any())
            {
                await _paymentService.UpdatePayments(paymentsToUpdate.Select(tuple => tuple.Payment).ToList());
                foreach (var valueTuples in paymentsToUpdate.GroupBy(entity => entity.invoice))
                {
                    if (valueTuples.Any())
                    {
                        _eventAggregator.Publish(new Events.InvoiceNeedUpdateEvent(valueTuples.Key.Id));
                    }
                }
            }
        }

        private async Task HandlePaymentData(string cryptoCode, string address, long totalAmount, long subaccountIndex,
            long subaddressIndex,
            string txId, long confirmations, long blockHeight, InvoiceEntity invoice,
            List<(PaymentEntity Payment, InvoiceEntity invoice)> paymentsToUpdate, string detectionSource = "reconciliation")
        {
            var network = _networkProvider.GetNetwork(cryptoCode);
            var pmi = PaymentTypes.CHAIN.GetPaymentMethodId(network.CryptoCode);
            var handler = (ZcashLikePaymentMethodHandler)_handlers[pmi];
            var promptDetails = handler.ParsePaymentPromptDetails(invoice.GetPaymentPrompt(pmi).Details);

            var details = new ZcashLikePaymentData()
            {
                SubaccountIndex = subaccountIndex,
                SubaddressIndex = subaddressIndex,
                TransactionId = txId,
                ConfirmationCount = confirmations,
                BlockHeight = blockHeight,
                InvoiceSettledConfirmationThreshold = promptDetails.InvoiceSettledConfirmationThreshold
            };

            var paymentId = $"{txId}#{subaccountIndex}#{subaddressIndex}";

            // Look up existing payment FIRST
            var allPayments = GetAllZcashLikePayments(invoice, cryptoCode);
            var alreadyExistingPaymentThatMatches = allPayments
                .FirstOrDefault(c => c.Id == paymentId && c.PaymentMethodId == pmi);

            // Now compute status, taking into account any existing Settled state
            var isSettled = GetStatus(details, invoice.SpeedPolicy);
            var newStatus = isSettled ? PaymentStatus.Settled : PaymentStatus.Processing;
            var status = alreadyExistingPaymentThatMatches?.Status == PaymentStatus.Settled
                ? PaymentStatus.Settled
                : newStatus;

            var paymentData = new PaymentData()
            {
                Status = status,
                Amount = ZcashMoney.Convert(totalAmount),
                Created = DateTimeOffset.UtcNow,
                Id = paymentId,
                Currency = network.CryptoCode
            }.Set(invoice, handler, details);

            var paymentBlob = paymentData.GetBlob();
            paymentBlob.Destination = address;
            paymentData.SetBlob(pmi, paymentBlob);

            if (alreadyExistingPaymentThatMatches == null)
            {
                var payment = await _paymentService.AddPayment(paymentData, [txId]);
                if (payment != null)
                {
                    await ReceivedPayment(await _invoiceRepository.GetInvoice(invoice.Id), payment, detectionSource);
                }
                else
                {
                    // AddPayment also returns null for database write failures. Never checkpoint
                    // a receipt unless its payment was actually persisted (possibly by another listener).
                    var persisted = await _invoiceRepository.GetInvoice(invoice.Id);
                    if (persisted == null || !GetAllZcashLikePayments(persisted, cryptoCode).Any(p => p.Id == paymentId))
                        throw new InvalidOperationException($"Could not persist Zcash payment {paymentId}.");
                }
            }
            else
            {
                alreadyExistingPaymentThatMatches.Status = status;
                alreadyExistingPaymentThatMatches.Value = ZcashMoney.Convert(totalAmount);
                alreadyExistingPaymentThatMatches.UpdateAmounts();
                alreadyExistingPaymentThatMatches.Details = JToken.FromObject(details, handler.Serializer);
                paymentsToUpdate.Add((alreadyExistingPaymentThatMatches, invoice));
            }
        }

        private bool GetStatus(ZcashLikePaymentData details, SpeedPolicy speedPolicy)
        {
            // Console.WriteLine($"Confirmations: {details.ConfirmationCount}, Required: {ConfirmationsRequired(details, speedPolicy)}");
            return details.ConfirmationCount >= ConfirmationsRequired(details, speedPolicy);
        }

        public static long ConfirmationsRequired(ZcashLikePaymentData details, SpeedPolicy speedPolicy)
            => (details, speedPolicy) switch
            {
                ({ InvoiceSettledConfirmationThreshold: long v }, _) => v,
                (_, SpeedPolicy.HighSpeed) => 0,
                (_, SpeedPolicy.MediumSpeed) => 1,
                (_, SpeedPolicy.LowMediumSpeed) => 2,
                (_, SpeedPolicy.LowSpeed) => 6,
                _ => 6,
            };

        private async Task RecoverPayments(string cryptoCode, CancellationToken cancellationToken)
        {
            if (_ZcashRpcProvider.WalletBackends[cryptoCode] is not ZkoolGraphQlBackend backend)
                return;

            await using var accountContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var accounts = await accountContext.Receivers.Select(r => (long)r.AccountIndex)
                .Distinct().ToListAsync(cancellationToken);
            foreach (var account in accounts)
            {
                try
                {
                    await RecoverAccountPayments(cryptoCode, backend, account, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // Leave this cursor unchanged and continue recovering independent accounts.
                    _logger.LogError(ex, "[{CryptoCode}] Payment recovery failed for account {Account}", cryptoCode, account);
                }
            }
        }

        private async Task RecoverAccountPayments(string cryptoCode, ZkoolGraphQlBackend backend,
            long account, CancellationToken cancellationToken)
        {
            await using var strategyContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode);
                await using var ctx = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
                await using var transaction = await ctx.Database.BeginTransactionAsync(cancellationToken);
                // Serialize recovery for an account across listener/server instances. Payment writes
                // use separate contexts; a crash before the cursor commit safely replays them.
                await ctx.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({802469321}, {checked((int)account)})", cancellationToken);
                var cursor = await ctx.RecoveryCursors.SingleOrDefaultAsync(c =>
                    c.CryptoCode == cryptoCode && c.AccountIndex == account, cancellationToken);
                var height = await backend.SynchronizeAsync(new[] { account }, cancellationToken);
                // Replay a small overlap, including the saved height, for same-block late discovery
                // and shallow reorganizations. Existing payment IDs make this idempotent.
                var fromHeight = Math.Max(0, Math.Min(cursor?.Height ?? 0, height) - 100);
                var receivers = await ctx.Receivers.Where(r => r.AccountIndex == account)
                    .ToListAsync(cancellationToken);
                var transfers = await backend.GetTransfersByHeightAsync(account, fromHeight,
                    receivers.Select(r => (long)r.AddressIndex).Distinct().ToArray(), cancellationToken);
                var updates = new List<(PaymentEntity Payment, InvoiceEntity Invoice)>();
                var recoveredInvoices = new HashSet<string>();
                // Filter each note's receiver before grouping: an internal/change note may share
                // a diversifier index with an invoice, but must neither be credited nor hide its receipt.
                var matched = transfers.Where(t => t.Height > 0 && t.Height <= height)
                    .Select(t => (Transfer: t, Receiver: receivers.FirstOrDefault(r =>
                        r.AddressIndex == t.AddressIndex && (t.Address == r.UnifiedAddress ||
                        t.Address == r.SaplingAddress || t.Address == r.OrchardAddress || t.Address == r.TransparentAddress))))
                    .Where(t => t.Receiver != null);
                foreach (var group in matched.GroupBy(t =>
                    (t.Transfer.TransactionId, t.Transfer.AccountIndex, t.Transfer.AddressIndex)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var receiver = group.First().Receiver;
                    var invoice = await _invoiceRepository.GetInvoiceFromAddress(paymentMethodId, receiver.UnifiedAddress);
                    if (invoice == null) continue; // A reserved address may never have become an invoice.
                    var first = group.First().Transfer;
                    await HandlePaymentData(cryptoCode, receiver.UnifiedAddress, group.Sum(t => t.Transfer.Amount),
                        account, first.AddressIndex, first.TransactionId, first.Confirmations, first.Height, invoice, updates);
                    recoveredInvoices.Add(invoice.Id);
                }
                await _paymentService.UpdatePayments(updates.Select(u => u.Payment).ToList());
                foreach (var invoiceId in recoveredInvoices)
                    _eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoiceId));
                if (cursor == null)
                {
                    cursor = new ZcashRecoveryCursor { CryptoCode = cryptoCode, AccountIndex = account };
                    ctx.RecoveryCursors.Add(cursor);
                }
                cursor.Height = height;
                await ctx.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            });
        }

        private async Task UpdateAnyPendingZcashLikePayment(string cryptoCode)
        {
            var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode);
            var invoices = await _invoiceRepository.GetMonitoredInvoices(paymentMethodId);
            if (!invoices.Any())
                return;
            invoices = invoices.Where(entity => entity.GetPaymentPrompt(paymentMethodId).Activated).ToArray();
            await UpdatePaymentStates(cryptoCode, invoices);
        }

        private IEnumerable<PaymentEntity> GetAllZcashLikePayments(InvoiceEntity invoice, string cryptoCode)
        {
            return invoice.GetPayments(false)
                .Where(p => p.PaymentMethodId == PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode));
        }
    }
}
