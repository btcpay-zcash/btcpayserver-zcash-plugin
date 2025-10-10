using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ZCash.RPC
{
    public partial class GetAccountsRequest
    {
        [JsonProperty("tag")] public string Tag { get; set; }
    }
}
