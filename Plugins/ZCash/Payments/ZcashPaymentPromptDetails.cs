using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.ZCash.Payments
{
    /// <summary>
    /// Snapshot taken at invoice creation. Immutable after that.
    /// Holds exactly what address/account was assigned to this invoice.
    /// </summary>
    public class ZcashPaymentPromptDetails
    {
        public long AccountIndex { get; set; }
        public long AddressIndex { get; set; }
        public string DepositAddress { get; set; }
        public long? InvoiceSettledConfirmationThreshold { get; set; }
    }
}
