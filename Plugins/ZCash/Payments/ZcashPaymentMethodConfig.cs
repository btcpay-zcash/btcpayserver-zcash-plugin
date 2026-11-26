using BTCPayServer.Payments;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ZCash.Payments
{
    public class ZcashPaymentMethodConfig
    {
        public string? ViewingKey { get; set; }
        public string? ViewingKeyHash { get; set; }
        public static string HashViewingKey(string key) => System.Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key.Trim().ToLowerInvariant())));

        public string GetKeyHash() => ViewingKeyHash ??
            (string.IsNullOrWhiteSpace(ViewingKey) ? null : HashViewingKey(ViewingKey));

        public long? BirthHeight { get; set; }
        public bool WalletConfigured => AccountIndex is not null;
        public long? AccountIndex { get; set; }
        public long? InvoiceSettledConfirmationThreshold { get; set; }
    }
}
