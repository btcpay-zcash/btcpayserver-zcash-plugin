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
using Newtonsoft.Json.Linq;
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
                    _ = UpdateAnyPendingZcashLikePayment(stateChanged.CryptoCode);
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
                addressIndexList.Add(expandedInvoice.PaymentMethodDetails.AddressIndex - 1);
                accountToAddressQuery[expandedInvoice.PaymentMethodDetails.AccountIndex] = addressIndexList;
            }

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
            
            // Process each account's transfers
            foreach (var kvp in tasks)
            {
                var response = await kvp.Value;
                var transfers = response.In;
                if (transfers == null || !transfers.Any())
                    continue;

                foreach (var transfer in transfers)
                {
                    // Console.WriteLine($"Processing transfer {transfer.Txid} -> {transfer.Address}, amount: {transfer.Amount}");

                    // Try to find existing invoice for this transfer
                    var invoice = await _invoiceRepository.GetInvoiceFromAddress(paymentId, transfer.Address);
                    Console.WriteLine($"paymentId: {paymentId}");

                    if (invoice == null)
                    {
                        Console.WriteLine($"No invoice found for {transfer.Address}, skipping");
                        continue; // skip this transfer
                    }
                    // else
                    // {
                    //     // Fall back to invoice by prompt/destination
                    //     var newMatch = expandedInvoices.SingleOrDefault(ei => ei.Prompt.Destination == transfer.Address);
                    //     if (newMatch.Invoice == null)
                    //     {
                    //         Console.WriteLine($"No matching invoice for {transfer.Address}, skipping");
                    //         continue;
                    //     }

                    //     invoice = newMatch.Invoice;
                    //     Console.WriteLine($"New invoice found: {invoice.Id}");
                    // }

                    // Handle payment data
                    await HandlePaymentData(
                        cryptoCode,
                        transfer.Address,
                        transfer.Amount,
                        transfer.SubaddrIndex.Major,
                        transfer.SubaddrIndex.Minor,
                        transfer.Txid,
                        transfer.Confirmations,
                        transfer.Height,
                        invoice,
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

        private async Task OnTransactionUpdated(string cryptoCode, string transactionHash)
        {
            var paymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(cryptoCode);
            var transfer = await _ZcashRpcProvider.WalletRpcClients[cryptoCode]
                .SendCommandAsync<GetTransferByTransactionIdRequest, GetTransferByTransactionIdResponse>(
                    "get_transfer_by_txid",
                    new GetTransferByTransactionIdRequest() { TransactionId = transactionHash, AccountIndex = 0 });

            var paymentsToUpdate = new List<(PaymentEntity Payment, InvoiceEntity invoice)>();

            //group all destinations of the tx together and loop through the sets
            foreach (var destination in transfer.Transfers.GroupBy(destination => destination.Address))
            {
                //find the invoice corresponding to this address, else skip
                var invoice = await _invoiceRepository.GetInvoiceFromAddress(paymentMethodId, destination.Key);
                if (invoice == null)
                    continue;

                var index = destination.First().SubaddrIndex;

                await HandlePaymentData(cryptoCode,
                    destination.Key,
                    destination.Sum(destination1 => destination1.Amount),
                    index.Major,
                    index.Minor,
                    transfer.Transfer.Txid,
                    transfer.Transfer.Confirmations,
                    transfer.Transfer.Height
                    , invoice, paymentsToUpdate);
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
            var paymentData = new Data.PaymentData()
            {
                Status = status,
                Amount = ZcashMoney.Convert(totalAmount),
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
                alreadyExistingPaymentThatMatches.Status = status;
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
