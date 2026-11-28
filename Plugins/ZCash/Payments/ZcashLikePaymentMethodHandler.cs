using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Dom;
using BTCPayServer.Common;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Models;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Altcoins;
using BTCPayServer.Rating;
using BTCPayServer.Plugins.ZCash.RPC;
using BTCPayServer.Plugins.ZCash;
using BTCPayServer.Plugins.ZCash.Utils;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Rates;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BTCPayServer.Plugins.ZCash.Services;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.ZCash.Payments
{
    public class ZcashLikePaymentMethodHandler : IPaymentMethodHandler
    {
        private readonly ZcashLikeSpecificBtcPayNetwork _network;
        public ZcashLikeSpecificBtcPayNetwork Network => _network;
        public JsonSerializer Serializer { get; }
        private readonly ZcashRPCProvider _ZcashRpcProvider;
        private readonly ILogger<ZcashLikePaymentMethodHandler> _logger;
        public PaymentMethodId PaymentMethodId { get; }
        public ZcashLikePaymentMethodHandler(BTCPayNetworkBase network, ZcashRPCProvider ZcashRpcProvider,
            ILogger<ZcashLikePaymentMethodHandler> logger)
        {
            _network = (ZcashLikeSpecificBtcPayNetwork)network;
            PaymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(_network.CryptoCode);
            Serializer = BlobSerializer.CreateSerializer().Serializer;
            _ZcashRpcProvider = ZcashRpcProvider;
            _logger = logger;
        }
        public Task BeforeFetchingRates(PaymentMethodContext context)
        {
            context.Prompt.Currency = _network.CryptoCode;
            context.Prompt.Divisibility = _network.Divisibility;
            return Task.CompletedTask;
        }

        public Task ValidatePaymentMethodConfig(PaymentMethodConfigValidationContext context)
        {
            // Account assignment is established by wallet import. Generic API writes may
            // update the settlement threshold but cannot create or replace the binding.
            var previous = context.PreviousConfig as JObject ?? new JObject();
            var updated = (JObject)previous.DeepClone();
            if (context.Config is not JObject requested)
            {
                context.ModelState.AddModelError("config", "Expected a Zcash settings object.");
                return Task.CompletedTask;
            }
            foreach (var field in requested.Properties())
            {
                if (string.Equals(field.Name, nameof(ZcashPaymentMethodConfig.InvoiceSettledConfirmationThreshold), StringComparison.OrdinalIgnoreCase))
                {
                    if (field.Value.Type != JTokenType.Null &&
                        (field.Value.Type != JTokenType.Integer || !int.TryParse(field.Value.ToString(), out var value) || value < 0 || value > 100))
                        context.ModelState.AddModelError("config", "Required confirmations must be between 0 and 100.");
                    else
                    {
                        var old = updated.Property(field.Name, StringComparison.OrdinalIgnoreCase);
                        updated[old?.Name ?? field.Name] = field.Value.DeepClone();
                    }
                }
                else if (!JToken.DeepEquals(previous.GetValue(field.Name, StringComparison.OrdinalIgnoreCase), field.Value))
                    context.ModelState.AddModelError("config", "Configure the receiving account through the store wallet settings.");
            }
            context.Config = updated;
            return Task.CompletedTask;
        }

        public async Task ConfigurePrompt(PaymentMethodContext context)
        {
            try
            {
                await ConfigurePromptCore(context);
            }
            catch (Exception ex) when (ex is not PaymentMethodUnavailableException)
            {
                // BTCPay records this only in the invoice's own event log and creates the
                // invoice without this payment method, so make it visible in the server log.
                _logger.LogError(ex, "{PaymentMethodId}: could not create the payment prompt for invoice {InvoiceId} of store {StoreId}; the invoice will not offer this payment method",
                    PaymentMethodId, context.InvoiceEntity?.Id, context.Store?.Id);
                throw;
            }
        }

        private async Task ConfigurePromptCore(PaymentMethodContext context)
        {
            if (!_ZcashRpcProvider.IsAvailable(_network.CryptoCode))
                throw new PaymentMethodUnavailableException($"Node or wallet not available");
            var config = ParsePaymentMethodConfig(context.PaymentMethodConfig);
            if (config.AccountIndex is not { } accountId ||
                _ZcashRpcProvider.WalletBackends[_network.CryptoCode] is not ZkoolGraphQlBackend backend ||
                !await backend.IsStoreAccountAsync(context.Store.Id, accountId, config.GetKeyHash()))
                throw new PaymentMethodUnavailableException("This store has no verified Zcash receiving account.");
            var feeRatePerKb = await backend.GetFeeEstimateAsync(accountId);
            var address = await backend.CreateAddressAsync(accountId, $"btcpay invoice #{context.InvoiceEntity.Id}");

            var feeRatePerByte = feeRatePerKb.FeePerKb / 1024;

            context.Prompt.Destination = address.Address;
            context.Prompt.PaymentMethodFee = ZcashMoney.Convert(feeRatePerByte * 100);
            context.Prompt.Details = JObject.FromObject(new ZcashPaymentPromptDetails()
            {
                AccountIndex = accountId,
                AddressIndex = address.AddressIndex,
                DepositAddress = address.Address,
                InvoiceSettledConfirmationThreshold = config.InvoiceSettledConfirmationThreshold
            }, Serializer);
            context.TrackedDestinations.Add(address.Address);
        }

        public ZcashPaymentPromptDetails ParsePaymentPromptDetails(Newtonsoft.Json.Linq.JToken details)
        {
            return details.ToObject<ZcashPaymentPromptDetails>(Serializer);
        }
        object IPaymentMethodHandler.ParsePaymentPromptDetails(Newtonsoft.Json.Linq.JToken details)
        {
            return ParsePaymentPromptDetails(details);
        }
        object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken config)
        {
            return ParsePaymentMethodConfig(config);
        }
        public ZcashPaymentMethodConfig ParsePaymentMethodConfig(JToken config)
        {
            return config.ToObject<ZcashPaymentMethodConfig>(Serializer) ?? throw new FormatException($"Invalid {nameof(ZcashPaymentMethodConfig)}");
        }

        public ZcashLikePaymentData ParsePaymentDetails(JToken details)
        {
            return details.ToObject<ZcashLikePaymentData>(Serializer) ?? throw new FormatException($"Invalid {nameof(ZcashLikePaymentData)}");
        }
        object IPaymentMethodHandler.ParsePaymentDetails(JToken details)
        {
            return ParsePaymentDetails(details);
        }
    }
}
