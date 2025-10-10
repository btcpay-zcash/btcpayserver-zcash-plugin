using BTCPayServer.Client.Models;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Altcoins;
using BTCPayServer.Plugins.ZCash.Utils;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.ZCash.Payments
{
    public class ZcashLikePaymentData
    {
        public long SubaddressIndex { get; set; }
        public long SubaccountIndex { get; set; }
        public long BlockHeight { get; set; }
        public long ConfirmationCount { get; set; }
        public string TransactionId { get; set; }
    }
}
