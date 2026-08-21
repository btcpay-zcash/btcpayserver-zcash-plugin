using System;

namespace BTCPayServer.Plugins.ZCash.Data.Models;

public class ZcashReceiver
{
    public int Id { get; set; }
    public int AccountIndex { get; set; }
    public int AddressIndex { get; set; }
    public string UnifiedAddress { get; set; }
    public string? TransparentAddress { get; set; }
    public string? SaplingAddress { get; set; }
    public string? OrchardAddress { get; set; }
    public string? InvoiceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
