using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ZCash.RPC
{
    public class GetFeeEstimateResponse
    {
        [JsonProperty("fee")] public long Fee { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("untrusted")] public bool Untrusted { get; set; }
    }
}
