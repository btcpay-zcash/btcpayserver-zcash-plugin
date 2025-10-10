using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ZCash.RPC
{
    public class GetFeeEstimateRequest
    {
        [JsonProperty("grace_blocks")] public int? GraceBlocks { get; set; }
    }
}
