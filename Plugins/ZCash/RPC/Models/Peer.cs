using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ZCash.RPC
{
    public partial class Peer
    {
        [JsonProperty("info")] public Info Info { get; set; }
    }
}
