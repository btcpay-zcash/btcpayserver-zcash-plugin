using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ZCash.RPC
{
    public partial class MakeUriResponse
    {
        [JsonProperty("uri")] public string Uri { get; set; }
    }
}
