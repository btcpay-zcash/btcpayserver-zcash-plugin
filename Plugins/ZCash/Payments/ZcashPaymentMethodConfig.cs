using BTCPayServer.Payments;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ZCash.Payments
{
    public class ZcashPaymentMethodConfig
    {
        public string? ViewingKey { get; set; }
        public string? ViewingKeyHash { get; set; }
        public long? BirthHeight { get; set; }
        public bool WalletConfigured => AccountIndex is not null;
        public long? AccountIndex { get; set; }
        public long? InvoiceSettledConfirmationThreshold { get; set; }
    }
}
