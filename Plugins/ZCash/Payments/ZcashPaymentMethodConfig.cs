using BTCPayServer.Payments;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ZCash.Payments
{
    public class ZcashPaymentMethodConfig
    {
        public string? ViewingKey { get; set; }
        public long? BirthHeight { get; set; }
        public bool WalletConfigured => !string.IsNullOrEmpty(ViewingKey);
        public long? AccountIndex { get; set; }
        public long? InvoiceSettledConfirmationThreshold { get; set; }
    }
}
