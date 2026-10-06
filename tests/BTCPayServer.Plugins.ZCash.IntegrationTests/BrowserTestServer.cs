using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using MockZkool;
using Npgsql;

namespace BTCPayServer.Plugins.ZCash.IntegrationTests;

internal sealed class BrowserTestServer : IAsyncDisposable
{
    private WebApplication? mock;
    private Process? process;
    private ProcessStartInfo? startInfo;
    public string DatabaseConnectionString { get; private set; } = "";
    private string adminConnection = "";
    private string database = "";
    private readonly object logLock = new();
    public string Url { get; private set; } = "";
    public bool UsesRegtest { get; private set; }
    public string CashcowUrl { get; private set; } = "";
    public string Artifacts { get; private set; } = "";
    private string logPath => Path.Combine(Artifacts, "server.log");

    public static async Task<BrowserTestServer> StartAsync(bool forceMock = false)
    {
        var result = new BrowserTestServer();
        try { await result.StartCoreAsync(forceMock); return result; }
        catch { await result.DisposeAsync(); throw; }
    }

    private async Task StartCoreAsync(bool forceMock)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "btcpay-zcash-plugin.sln"))) root = root.Parent;
        if (root == null) throw new InvalidOperationException("Cannot locate plugin repository");
        var id = Guid.NewGuid().ToString("N");
        Artifacts = Path.Combine(root.FullName, "TestResults", "playwright", id);
        Directory.CreateDirectory(Artifacts);
        database = "zec_ui_" + id;
        var cs = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("BTCPAY_TEST_POSTGRES")
            ?? "Host=127.0.0.1;Port=15432;Username=btcpay;Database=postgres");
        cs.Database = "postgres";
        adminConnection = cs.ConnectionString;
        await using (var connection = new NpgsqlConnection(adminConnection))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {database}", connection);
            await command.ExecuteNonQueryAsync();
        }
        var regtest = forceMock ? null : await RegtestEndpoints.DetectAsync();
        UsesRegtest = regtest != null;
        string graphqlUrl, cashcowUrl, rpcUrl;
        if (regtest != null)
        {
            graphqlUrl = regtest.GraphQl;
            cashcowUrl = regtest.CashCow;
            rpcUrl = regtest.Rpc;
        }
        else
        {
            mock = MockWalletServer.Create([]);
            mock.Urls.Add("http://127.0.0.1:0");
            await mock.StartAsync();
            graphqlUrl = mock.Urls.Single() + "/graphql";
            cashcowUrl = graphqlUrl;
            rpcUrl = mock.Urls.Single() + "/rpc";
        }
        CashcowUrl = cashcowUrl;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Url = $"http://127.0.0.1:{port}";
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.Combine(root.FullName, "btcpayserver", "BTCPayServer"),
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add(Path.Combine(start.WorkingDirectory, "bin", "Debug", "net10.0", "BTCPayServer.dll"));
        start.ArgumentList.Add("--disable-registration"); start.ArgumentList.Add("false");
        // Prevent inherited local-launch settings from leaking into this isolated instance.
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("BTCPAY_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        cs.Database = database;
        DatabaseConnectionString = cs.ConnectionString;
        void Env(string key, string value) => start.Environment[key] = value;
        Env("ASPNETCORE_ENVIRONMENT", "Development");
        Env("BTCPAY_NETWORK", "regtest"); Env("BTCPAY_CHAINS", "zec"); Env("BTCPAY_CHEATMODE", "true");
        Env("BTCPAY_BIND", "127.0.0.1"); Env("BTCPAY_PORT", port.ToString());
        Env("BTCPAY_POSTGRES", cs.ConnectionString);
        Env("BTCPAY_DATADIR", Path.Combine(Artifacts, "data"));
        Env("BTCPAY_PLUGINDIR", Path.Combine(Artifacts, "plugins"));
        Env("BTCPAY_DEBUG_PLUGINS", Path.Combine(root.FullName, "Plugins", "ZCash", "bin", "Debug", "net10.0", "BTCPayServer.Plugins.ZCash.dll"));
        Env("BTCPAY_ZEC_WALLET_BACKEND_TYPE", "ZkoolGraphQL");
        Env("BTCPAY_ZEC_WALLET_GRAPHQL_URI", graphqlUrl);
        Env("BTCPAY_ZEC_WALLET_CASHCOW_URI", cashcowUrl);
        Env("BTCPAY_ZEC_CASHCOW_DAEMON_URI", rpcUrl);
        if (UsesRegtest) Env("BTCPAY_ZEC_CASHCOW_MINER_SEED", "burger voice warrior danger satoshi you solid atom elite alcohol category layer able debate culture talk tissue language hip surge fiction paddle stove voyage");
        startInfo = start;
        StartProcess();
        await WaitUntilReadyAsync();
    }

    private void StartProcess()
    {
        process = new Process { StartInfo = startInfo! };
        void Log(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (logLock) File.AppendAllText(logPath, e.Data + Environment.NewLine); }
        process.OutputDataReceived += Log; process.ErrorDataReceived += Log;
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
    }

    private async Task WaitUntilReadyAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow.AddSeconds(UsesRegtest ? 240 : 90);
        while (DateTime.UtcNow < deadline)
        {
            if (process!.HasExited) throw new InvalidOperationException("BTCPay exited during startup:\n" + ReadLog());
            try
            {
                if ((await client.GetAsync(Url)).IsSuccessStatusCode
                    && (!UsesRegtest || ReadLog().Contains("ZEC just became available"))) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(250);
        }
        throw new TimeoutException("BTCPay startup timed out:\n" + ReadLog());
    }

    public async Task StopDetectionAsync()
    {
        if (process != null)
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            process.Dispose();
            process = null;
        }
    }

    public async Task RestartDetectionAsync()
    {
        await StopDetectionAsync();
        StartProcess();
        await WaitUntilReadyAsync();
    }

    public async Task SetMockWalletAvailableAsync(bool value)
    {
        if (mock == null) throw new InvalidOperationException("Requires the isolated mock wallet");
        using var http = new HttpClient();
        using var response = await http.PostAsync(mock.Urls.Single() + "/test/availability/" + value.ToString().ToLowerInvariant(), null);
        response.EnsureSuccessStatusCode();
    }

    public string ReadLog() { lock (logLock) return File.Exists(logPath) ? File.ReadAllText(logPath) : ""; }

    public async ValueTask DisposeAsync()
    {
        if (process != null)
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            process.Dispose();
        }
        if (mock != null) await mock.DisposeAsync();
        if (!string.IsNullOrEmpty(adminConnection) && !string.IsNullOrEmpty(database))
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(adminConnection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
