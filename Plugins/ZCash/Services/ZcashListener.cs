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
using BTCPayServer.Plugins.ZCash.Utils;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitpayClient;
using NBXplorer;
using Newtonsoft.Json;
using static BTCPayServer.Client.Models.InvoicePaymentMethodDataModel;
using BTCPayServer.Services;
using BTCPayServer.Plugins.ZCash.RPC;

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

        public ZcashListener(InvoiceRepository invoiceRepository,
            EventAggregator eventAggregator,
            ZcashRPCProvider ZcashRpcProvider,
            ZcashLikeConfiguration ZcashLikeConfiguration,
            BTCPayNetworkProvider networkProvider,
            ILogger<ZcashListener> logger,
            PaymentService paymentService,
            InvoiceActivator invoiceActivator,
            PaymentMethodHandlerDictionary handlers) : base(eventAggregator, logger)
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
                    await OnNewBlock(zcashEvent.CryptoCode);
                }
                if (!string.IsNullOrEmpty(zcashEvent.TransactionHash))
                {
                    await OnTransactionUpdated(zcashEvent.CryptoCode, zcashEvent.TransactionHash);
                }
            }
        }

        private async Task ReceivedPayment(InvoiceEntity invoice, PaymentEntity payment)
        {
            _logger.LogInformation(
                $"Invoice {invoice.Id} received payment {payment.Value} {payment.Currency} {payment.Id}");


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

        private async Task UpdatePaymentStates(string cryptoCode, InvoiceEntity[] invoices)
        {
            if (!invoices.Any())
                return;

            var ZcashWalletRpcClient = _ZcashRpcProvider.WalletRpcClients[cryptoCode];
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

            var invoicesById = expandedInvoices.ToDictionary(
                expandedInvoice => expandedInvoice.Invoice.Id,
                expandedInvoice => expandedInvoice.Invoice);
            var locationClaims = new List<(WalletLocation Location, string InvoiceId)>();
            foreach (var expandedInvoice in expandedInvoices)
            {
                locationClaims.Add((
                    new WalletLocation(
                        expandedInvoice.PaymentMethodDetails.AccountIndex,
                        expandedInvoice.PaymentMethodDetails.AddressIndex),
                    expandedInvoice.Invoice.Id));

                locationClaims.AddRange(expandedInvoice.ExistingPayments.Select(existingPayment =>
                    (
                        new WalletLocation(
                            existingPayment.PaymentData.SubaccountIndex,
                            existingPayment.PaymentData.SubaddressIndex),
                        expandedInvoice.Invoice.Id)));
            }

            var invoicesByLocation = ZcashPaymentReconciliation.IndexLocations(locationClaims);

            // Build account->subaddress query
            var accountToAddressQuery = invoicesByLocation.Keys
                .GroupBy(location => location.AccountIndex)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(location => location.AddressIndex).ToList());

            // Send RPC commands
            var tasks = accountToAddressQuery.ToDictionary(
                kvp => kvp.Key,
                kvp => ZcashWalletRpcClient.SendCommandAsync<GetTransfersRequest, GetTransfersResponse>(
                    "get_transfers",
                    new GetTransfersRequest
                    {
                        AccountIndex = kvp.Key,
                        In = true,
                        SubaddrIndices = kvp.Value.Distinct().ToList()
                    }
                )
            );

            await Task.WhenAll(tasks.Values);

            var updatedPaymentEntities = new List<(PaymentEntity Payment, InvoiceEntity Invoice)>();
            var ambiguousTransferCount = 0;
            var invalidTransferCount = 0;
            var invalidGroupCount = 0;
            
            // Process each account's transfers
            foreach (var kvp in tasks)
            {
                var response = await kvp.Value;
                var transfers = response.In;
                if (transfers == null || !transfers.Any())
                    continue;

                var aggregation = ZcashPaymentReconciliation.AggregateTransfers(kvp.Key, transfers);
                invalidTransferCount += aggregation.InvalidTransferCount;
                invalidGroupCount += aggregation.InvalidGroupCount;

                foreach (var transfer in aggregation.Transfers)
                {
                    if (!invoicesByLocation.TryGetValue(transfer.Location, out var invoiceIds) || invoiceIds.Length != 1)
                    {
                        if (invoiceIds?.Length > 1)
                            ambiguousTransferCount++;
                        continue;
                    }

                    var invoice = invoicesById[invoiceIds[0]];

                    await HandlePaymentData(
                        cryptoCode,
                        transfer.Amount,
                        transfer.Location.AccountIndex,
                        transfer.Location.AddressIndex,
                        transfer.TransactionId,
                        transfer.Confirmations,
                        transfer.Height,
                        invoice,
                        updatedPaymentEntities
                    );
                }
            }

            if (invalidTransferCount > 0 || invalidGroupCount > 0 || ambiguousTransferCount > 0)
            {
                _logger.LogWarning(
                    "{CryptoCode} reconciliation skipped invalid transfers/groups or ambiguous wallet locations: {InvalidTransferCount}/{InvalidGroupCount}/{AmbiguousTransferCount}",
                    cryptoCode,
                    invalidTransferCount,
                    invalidGroupCount,
                    ambiguousTransferCount);
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

        private Task OnTransactionUpdated(string cryptoCode, string _transactionHash)
        {
            return UpdateAnyPendingZcashLikePayment(cryptoCode);
        }

        private async Task HandlePaymentData(string cryptoCode, long totalAmount, long subaccountIndex,
            long subaddressIndex,
            string txId, long confirmations, long blockHeight, InvoiceEntity invoice,
            List<(PaymentEntity Payment, InvoiceEntity invoice)> paymentsToUpdate)
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
            var status = GetStatus(details, invoice.SpeedPolicy) ? PaymentStatus.Settled : PaymentStatus.Processing;
            var amount = ZcashMoney.Convert(totalAmount);
            var paymentData = new Data.PaymentData()
            {
                Status = status,
                Amount = amount,
                Created = DateTimeOffset.UtcNow,
                Id = $"{txId}#{subaccountIndex}#{subaddressIndex}",
                Currency = network.CryptoCode
            }.Set(invoice, handler, details);


            var alreadyExistingPaymentThatMatches = GetAllZcashLikePayments(invoice, cryptoCode)
                .SingleOrDefault(c => c.Id == paymentData.Id && c.PaymentMethodId == pmi);

            //if it doesnt, add it and assign a new Zcashlike address to the system if a balance is still due
            if (alreadyExistingPaymentThatMatches == null)
            {
                var payment = await _paymentService.AddPayment(paymentData, [txId]);
                if (payment != null)
                    await ReceivedPayment(invoice, payment);
            }
            else
            {
                //else update it with the new data
                alreadyExistingPaymentThatMatches.Value = amount;
                alreadyExistingPaymentThatMatches.Status = status;
                alreadyExistingPaymentThatMatches.SetDetails(handler, details);
                alreadyExistingPaymentThatMatches.UpdateAmounts();
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
