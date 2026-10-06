using System.Text;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ZCash.IntegrationTests;

internal sealed record RegtestEndpoints(string GraphQl, string CashCow, string Rpc)
{
    public static async Task<RegtestEndpoints?> DetectAsync()
    {
        var mode = Environment.GetEnvironmentVariable("BTCPAY_TEST_WALLET") ?? "auto";
        if (mode == "mock") return null;
        if (mode != "auto" && mode != "regtest") throw new InvalidOperationException("BTCPAY_TEST_WALLET must be auto, mock, or regtest");
        var endpoint = Environment.GetEnvironmentVariable("BTCPAY_TEST_GRAPHQL") ?? "http://127.0.0.1:18081/graphql";
        var cashcow = Environment.GetEnvironmentVariable("BTCPAY_TEST_CASHCOW_GRAPHQL") ?? "http://127.0.0.1:18082/graphql";
        var rpc = Environment.GetEnvironmentVariable("BTCPAY_TEST_ZEBRA_RPC") ?? "http://127.0.0.1:18232";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        // Only connection failure means unavailable. A reachable but broken wallet must fail.
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(endpoint, new StringContent("{\"query\":\"query { currentHeight }\"}", Encoding.UTF8, "application/json"));
        }
        catch (Exception e) when ((e is HttpRequestException || e is TaskCanceledException) && mode == "auto") { return null; }
        using (response)
        {
            response.EnsureSuccessStatusCode();
            var result = JObject.Parse(await response.Content.ReadAsStringAsync());
            if (result["errors"] != null || result["data"]?["currentHeight"]?.Value<long>() < 300 || result["data"]?["currentHeight"] == null)
                throw new InvalidOperationException("Regtest wallet is not ready. Wait for zcash-regtest/scripts/run-local.py to report readiness.");
        }
        using var rpcResponse = await client.PostAsync(rpc, new StringContent("{\"jsonrpc\":\"1.0\",\"id\":\"test\",\"method\":\"getblockchaininfo\",\"params\":[]}", Encoding.UTF8, "application/json"));
        rpcResponse.EnsureSuccessStatusCode();
        var info = JObject.Parse(await rpcResponse.Content.ReadAsStringAsync());
        var chain = info["result"]?["chain"]?.Value<string>();
        // Zebra labels regtest as "test"; verify the generated regtest activation profile.
        var zebraRegtest = chain == "test"
            && info["result"]?["upgrades"]?["c2d6d0b4"]?["activationheight"]?.Value<long>() == 1
            && info["result"]?["upgrades"]?["37a5165b"]?["activationheight"]?.Value<long>() == 250;
        if (info["error"]?.Type is not (null or JTokenType.Null) || (chain != "regtest" && !zebraRegtest))
            throw new InvalidOperationException("The test daemon must be Zebra regtest");
        using var cashcowResponse = await client.PostAsync(cashcow, new StringContent("{\"query\":\"query { currentHeight }\"}", Encoding.UTF8, "application/json"));
        cashcowResponse.EnsureSuccessStatusCode();
        var cashcowInfo = JObject.Parse(await cashcowResponse.Content.ReadAsStringAsync());
        if (cashcowInfo["errors"] != null || (cashcowInfo["data"]?["currentHeight"]?.Value<long>() ?? 0) < 300)
            throw new InvalidOperationException("Cashcow GraphQL server is not ready");
        return new(endpoint, cashcow, rpc);
    }
}
