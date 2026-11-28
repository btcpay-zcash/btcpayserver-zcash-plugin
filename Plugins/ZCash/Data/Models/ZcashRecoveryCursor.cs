namespace BTCPayServer.Plugins.ZCash.Data.Models;

public class ZcashRecoveryCursor
{
    public string CryptoCode { get; set; }
    public long AccountIndex { get; set; }
    public long Height { get; set; }
}
