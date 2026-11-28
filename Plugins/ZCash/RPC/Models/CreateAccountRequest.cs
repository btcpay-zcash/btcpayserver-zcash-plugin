using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ZCash.RPC
{
    public partial class CreateAccountRequest
    {
        [JsonProperty("label")] public string Label { get; set; }
        [JsonProperty("key")] public string Key { get; set; }
        [JsonProperty("height")] public long? Height { get; set; }
    }
}
