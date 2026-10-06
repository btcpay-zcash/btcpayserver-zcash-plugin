using System.Net.WebSockets;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace MockZkool;

// A deliberately small, in-memory protocol stub, not a Zcash node or GraphQL implementation.
public static class MockWalletServer
{
    public static WebApplication Create(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();
        var wallet = new MockWallet();
        var available = true;
        app.MapPost("/test/availability/{value:bool}", (bool value) => { available = value; return Results.Ok(); });
        var subscriptions = new ConcurrentDictionary<string, int>();
        app.MapGet("/test/subscriptions", () => subscriptions.Values.Order().ToArray());
        app.MapGet("/health", () => Results.Ok(new { mock = true }));
        app.MapPost("/graphql", async (HttpContext context) =>
        {
            if (!available) return Results.StatusCode(503);
            var body = await JsonNode.ParseAsync(context.Request.Body) ?? new JsonObject();
            try { return Results.Json(new { data = wallet.Query(body) }); }
            catch (ArgumentException e) { return Results.Json(new { errors = new[] { new { message = e.Message } } }); }
        });
        app.MapPost("/rpc", async (HttpContext context) =>
        {
            var body = await JsonNode.ParseAsync(context.Request.Body) ?? new JsonObject();
            if (body["method"]?.GetValue<string>() != "generate")
                return Results.Json(new { id = body["id"]?.DeepClone(), error = new { code = -32601, message = "Unsupported mock RPC method" } });
            try
            {
                var hashes = wallet.Mine(body["params"]?[0]?.GetValue<int>() ?? 0);
                return Results.Json(new { id = body["id"]?.DeepClone(), result = hashes });
            }
            catch (ArgumentException e) { return Results.Json(new { id = body["id"]?.DeepClone(), error = new { code = -32602, message = e.Message } }); }
        });
        // Subscriptions remain idle; the plugin's existing poller discovers mock transactions/blocks.
        app.UseWebSockets();
        app.Map("/subscriptions", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync("graphql-transport-ws");
            var buffer = new byte[16384];
            var owned = new List<string>();
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var message = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (message.MessageType == WebSocketMessageType.Close) break;
                    var payload = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, message.Count));
                    var type = payload?["type"]?.GetValue<string>();
                    if (type == "subscribe")
                    {
                        var subscriptionId = Guid.NewGuid().ToString();
                        owned.Add(subscriptionId);
                        subscriptions[subscriptionId] = payload!["payload"]!["variables"]!["idAccount"]!.GetValue<int>();
                    }
                    if (type is "connection_init" or "ping")
                        await socket.SendAsync(Encoding.UTF8.GetBytes(type == "ping" ? "{\"type\":\"pong\"}" : "{\"type\":\"connection_ack\"}"), WebSocketMessageType.Text, true, context.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally { foreach (var id in owned) subscriptions.TryRemove(id, out _); }
        });
        return app;
    }
}

internal sealed class MockWallet
{
    private readonly object gate = new();
    private long height = 200;
    private int nextAccount = 2;
    private int nextAddress = 0;
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private readonly List<JsonObject> accounts = [new() { ["id"] = 1, ["name"] = "cashcow", ["aindex"] = 0, ["height"] = 200, ["balance"] = 1000m }];
    private readonly Dictionary<string, (int Account, int Index)> addresses = new();
    private readonly List<(int Account, JsonObject Tx)> transactions = new();

    public string[] Mine(int count)
    {
        lock (gate)
        {
            if (count < 1 || count > 1000) throw new ArgumentException("Block count must be between 1 and 1000");
            var first = ++height;
            foreach (var (_, tx) in transactions.Where(t => t.Tx["height"]!.GetValue<long>() == 0)) tx["height"] = first;
            height += count - 1;
            return Enumerable.Range(0, count).Select(i => (first + i).ToString("x64")).ToArray();
        }
    }

    public JsonObject Query(JsonNode request)
    {
        lock (gate)
        {
            var q = request["query"]?.GetValue<string>() ?? "";
            var v = request["variables"] ?? new JsonObject();
            JsonObject Result(string key, JsonNode? value) => new() { [key] = value };
            var id = v["idAccount"]?.GetValue<int>() ?? v["id"]?.GetValue<int>() ?? 1;
            if (q.Contains("currentHeight")) return Result("currentHeight", JsonValue.Create(height));
            if (q.Contains("synchronize")) return Result("synchronize", JsonValue.Create(height));
            if (q.Contains("createAccount"))
            {
                var account = nextAccount++;
                accounts.Add(new() { ["id"] = account, ["name"] = v["newAccount"]?["name"]?.DeepClone(), ["ufvk"] = v["newAccount"]?["key"]?.DeepClone(), ["aindex"] = account - 1, ["height"] = height, ["balance"] = 0m });
                return Result("createAccount", JsonValue.Create(account));
            }
            if (q.Contains("newAddresses"))
            {
                if (!accounts.Any(a => a["id"]!.GetValue<int>() == id)) throw new ArgumentException("Unknown account");
                var index = ++nextAddress;
                var address = $"uregtest-mock-{sessionId}-{id}-{index}";
                addresses.Add(address, (id, index));
                return Result("newAddresses", new JsonObject { ["ua"] = address, ["diversifierIndex"] = index });
            }
            if (q.Contains("accounts"))
            {
                var filter = v["accountFilter"];
                var selected = accounts.Where(a => (filter?["id"] == null || a["id"]!.GetValue<int>() == filter["id"]!.GetValue<int>()) && (filter?["name"] == null || a["name"]!.GetValue<string>() == filter["name"]!.GetValue<string>()));
                return Result("accounts", new JsonArray(selected.Select(a => { var copy = a.DeepClone(); copy["height"] = height; return copy; }).ToArray()));
            }
            if (q.Contains("pay("))
            {
                var txid = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                foreach (var recipient in v["payment"]?["recipients"]?.AsArray() ?? throw new ArgumentException("Recipients required"))
                {
                    var address = recipient!["address"]!.GetValue<string>();
                    if (!addresses.TryGetValue(address, out var receiver)) throw new ArgumentException("Unknown mock destination");
                    var amount = decimal.Parse(recipient["amount"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture);
                    if (amount <= 0) throw new ArgumentException("Amount must be positive");
                    var tx = transactions.FirstOrDefault(t => t.Account == receiver.Account && t.Tx["txid"]!.GetValue<string>() == txid).Tx;
                    if (tx == null)
                    {
                        tx = new JsonObject { ["txid"] = txid, ["height"] = 0L, ["notes"] = new JsonArray() };
                        transactions.Add((receiver.Account, tx));
                    }
                    tx["notes"]!.AsArray().Add(new JsonObject { ["value"] = amount, ["address"] = address, ["diversifierIndex"] = receiver.Index });
                }
                return Result("pay", JsonValue.Create(txid));
            }
            var txs = transactions.Where(t => t.Account == id).Select(t => t.Tx).ToList();
            if (q.Contains("transactionById")) return Result("transactionById", txs.FirstOrDefault(t => t["txid"]!.GetValue<string>() == v["txid"]?.GetValue<string>())?.DeepClone());
            if (q.Contains("transactionsByAccount") && q.Contains("unconfirmedByAccount"))
                return new JsonObject
                {
                    ["transactionsByAccount"] = new JsonArray(txs.Where(t => t["height"]!.GetValue<long>() > 0).Select(t => t.DeepClone()).ToArray()),
                    ["unconfirmedByAccount"] = new JsonArray(txs.Where(t => t["height"]!.GetValue<long>() == 0).Select(t => t.DeepClone()).ToArray())
                };
            if (q.Contains("unconfirmedByAccount")) return Result("unconfirmedByAccount", new JsonArray(txs.Where(t => t["height"]!.GetValue<long>() == 0).Select(t => t.DeepClone()).ToArray()));
            if (q.Contains("transactionsByAccount"))
                return Result("transactionsByAccount", new JsonArray(txs.Where(t => t["height"]!.GetValue<long>() >= (v["minHeight"]?.GetValue<long>() ?? 0)).Select(t => t.DeepClone()).ToArray()));
            if (q.Contains("mockSpendNotes"))
            {
                foreach (var tx in txs) tx["spent"] = true;
                return Result("mockSpendNotes", JsonValue.Create(true));
            }
            if (q.Contains("notesByAccount"))
            {
                var indices = v["diversifierIndices"]?.AsArray().Select(n => long.Parse(n!.ToString())).ToHashSet();
                return Result("notesByAccount", new JsonArray(txs.Where(t => t["height"]!.GetValue<long>() > 0 && t["spent"]?.GetValue<bool>() != true).SelectMany(t => t["notes"]!.AsArray().Select(n => { var note = n!.DeepClone(); note["tx"] = new JsonObject { ["txid"] = t["txid"]!.DeepClone(), ["height"] = t["height"]!.DeepClone() }; return note; })).Where(n => indices == null || indices.Contains(n["diversifierIndex"]!.GetValue<int>())).ToArray()));
            }
            if (q.Contains("balanceByAccount")) return Result("balanceByAccount", new JsonObject { ["height"] = height, ["total"] = txs.Sum(t => t["notes"]!.AsArray().Sum(n => n!["value"]!.GetValue<decimal>())), ["transparent"] = 0m, ["sapling"] = 0m, ["orchard"] = 0m });
            throw new ArgumentException("Unsupported mock GraphQL operation");
        }
    }
}
