using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ZCash.RPC
{
    public partial class SubaddrIndex
    {
        [JsonProperty("major")] public long Major { get; set; }
        [JsonProperty("minor")] public long Minor { get; set; }
    }
}
