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

namespace BTCPayServer.Plugins.ZCash.Payments
{
    public class ZcashLikePaymentMethodHandler : IPaymentMethodHandler
    {
        private readonly ZcashLikeSpecificBtcPayNetwork _network;
        public ZcashLikeSpecificBtcPayNetwork Network => _network;
        public JsonSerializer Serializer { get; }
        private readonly ZcashRPCProvider _ZcashRpcProvider;
        public PaymentMethodId PaymentMethodId { get; }
        public ZcashLikePaymentMethodHandler(BTCPayNetworkBase network, ZcashRPCProvider ZcashRpcProvider)
        {
            _network = (ZcashLikeSpecificBtcPayNetwork)network;
            PaymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId(_network.CryptoCode);
            Serializer = BlobSerializer.CreateSerializer().Serializer;
            _ZcashRpcProvider = ZcashRpcProvider;
        }
        bool IsReady() => _ZcashRpcProvider.IsConfigured(_network.CryptoCode) && _ZcashRpcProvider.IsAvailable(_network.CryptoCode);

        public Task BeforeFetchingRates(PaymentMethodContext context)
        {
            context.Prompt.Currency = _network.CryptoCode;
            context.Prompt.Divisibility = _network.Divisibility;
            var config = ParsePaymentMethodConfig(context.PaymentMethodConfig);
            if (context.Prompt.Activated && IsReady() && config.AccountIndex is { } accountIndex)
            {
                var walletBackend = _ZcashRpcProvider.WalletBackends[_network.CryptoCode];
                
                try
                {
                    context.State = new Prepare()
                    {
                        GetFeeRate = walletBackend.GetFeeEstimateAsync(accountIndex),
                        ReserveAddress = s => walletBackend.CreateAddressAsync(accountIndex, $"btcpay invoice #{s}"),
                        AccountIndex = accountIndex
                    };
                }
                catch (Exception ex)
                {
                    context.Logs.Write($"Error in BeforeFetchingRates: {ex.Message}", InvoiceEventData.EventSeverity.Error);
                }
            }
            return Task.CompletedTask;
        }
        public async Task ConfigurePrompt(PaymentMethodContext context)
        {
            if (!_ZcashRpcProvider.IsAvailable(_network.CryptoCode))
                throw new PaymentMethodUnavailableException($"Node or wallet not available");
            var invoice = context.InvoiceEntity;
            var ZcashPrepare = (Prepare)context.State;
            var feeRatePerKb = await ZcashPrepare.GetFeeRate;
            var address = await ZcashPrepare.ReserveAddress(invoice.Id);

            var feeRatePerByte = feeRatePerKb.FeePerKb / 1024;

            context.Prompt.Destination = address.Address;
            context.Prompt.PaymentMethodFee = ZcashMoney.Convert(feeRatePerByte * 100);
            context.Prompt.Details = JObject.FromObject(new ZcashPaymentPromptDetails()
            {
                AccountIndex = ZcashPrepare.AccountIndex,
                AddressIndex = address.AddressIndex,
                DepositAddress = address.Address,
                InvoiceSettledConfirmationThreshold = ParsePaymentMethodConfig(context.PaymentMethodConfig).InvoiceSettledConfirmationThreshold
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



        class Prepare
        {
            public Task<WalletFeeEstimate> GetFeeRate;
            public Func<string, Task<WalletAddress>> ReserveAddress;
            public long AccountIndex { get; internal set; }
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
