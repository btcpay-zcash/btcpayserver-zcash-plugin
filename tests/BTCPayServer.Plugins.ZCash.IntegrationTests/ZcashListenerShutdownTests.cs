using System.Net.Sockets;
using BBTCPayServer.Plugins.ZCash.RPC;
using BTCPayServer.Plugins.ZCash.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.ZCash.IntegrationTests;

public class ZcashListenerShutdownTests : ZcashIntegrationTestBase
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task WalletFailureDuringShutdownDoesNotCrashHost(bool honorsCancellation, bool apiFailure)
    {
        using var transport = new DisconnectingWallet(honorsCancellation, apiFailure);
        using var client = new ZkoolGraphQlClient(new Uri("http://wallet.invalid/graphql"), new HttpClient(transport));
        Provider.WalletBackends = Provider.WalletBackends.SetItem("ZEC",
            new ZkoolGraphQlBackend("ZEC", client, null!, null!, NullLogger<ZkoolGraphQlBackend>.Instance));
        Provider.Summaries["ZEC"] = new ZcashRPCProvider.ZcashLikeSummary { Synced = true, WalletAvailable = true };
        var events = new EventAggregator(new BTCPayServer.Logging.Logs());
        var listener = new ZcashListener(null!, events, Provider, null!, null!,
            NullLogger<ZcashListener>.Instance, null!, null!, null!, null!);
        using var host = new HostBuilder().ConfigureServices(services =>
            services.AddSingleton<IHostedService>(listener)).Build();
        await host.StartAsync();
        try
        {
            events.Publish(new ZcashEvent { CryptoCode = "ZEC", TransactionHash = new string('a', 64), AccountIndex = 1 });
            await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Begin host shutdown with an in-flight wallet HTTP request, then lose the wallet.
            var stopping = host.StopAsync();
            Assert.True(listener.CancellationToken.IsCancellationRequested);
            Assert.True(transport.RequestToken.IsCancellationRequested);
            transport.Disconnect.SetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            transport.Disconnect.TrySetResult();
        }
    }

    private sealed class DisconnectingWallet(bool honorsCancellation, bool apiFailure) : HttpMessageHandler
    {
        public CancellationToken RequestToken { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disconnect { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestToken = cancellationToken;
            Started.SetResult();
            // Honor cancellation if the listener propagates it to the HTTP request.
            await Disconnect.Task.WaitAsync(honorsCancellation ? cancellationToken : CancellationToken.None);
            if (apiFailure)
                throw new ZkoolGraphQlClient.GraphQlApiException(["Wallet is shutting down"]);
            throw new HttpRequestException("No route to host (wallet:8000)",
                new SocketException((int)SocketError.HostUnreachable));
        }
    }
}
