using BTCPayServer;
using BTCPayServer.Configuration;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ZCash;
using BTCPayServer.Plugins.ZCash.Configuration;
using BTCPayServer.Plugins.ZCash.Services;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MockZkool;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.ZCash.IntegrationTests;

// Inspired by the Monero integration-test branch's shared fixture. Uses isolated
// HTTP endpoints instead of global environment variables and Docker dependencies.
public abstract class ZcashIntegrationTestBase : IAsyncLifetime
{
    private WebApplication app = null!;
    private ServiceProvider services = null!;
    protected ZcashRPCProvider Provider { get; private set; } = null!;
    protected ZcashCheckoutCheatModeExtension CheatMode { get; private set; } = null!;
    protected ZkoolGraphQlClient GraphQl { get; private set; } = null!;
    protected Uri MockEndpoint { get; private set; } = null!;
    protected PaymentMethodId PaymentMethodId { get; } = PaymentTypes.CHAIN.GetPaymentMethodId("ZEC");

    public async Task InitializeAsync()
    {
        app = MockWalletServer.Create([]);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        var endpoint = new Uri(app.Urls.Single());
        MockEndpoint = endpoint;
        var configuration = new ZcashLikeConfiguration();
        configuration.ZcashLikeConfigurationItems.Add("ZEC", new ZcashLikeConfigurationItem
        {
            WalletBackend = WalletBackend.ZkoolGraphQL,
            GraphQlEndpointUri = new Uri(endpoint, "graphql"),
            CashCowEndpointUri = new Uri(endpoint, "graphql"),
            CashcowDaemonRpcUri = new Uri(endpoint, "rpc")
        });
        services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var environment = new BTCPayServerEnvironment(app.Environment,
            new BTCPayNetworkProvider([], new NBXplorer.NBXplorerNetworkProvider(ChainName.Regtest), new BTCPayServer.Logging.Logs()), null!, new BTCPayServerOptions { CheatMode = true });
        Provider = new ZcashRPCProvider(configuration, new EventAggregator(new BTCPayServer.Logging.Logs()),
            services.GetRequiredService<IHttpClientFactory>(), null!, environment, NullLogger<ZcashRPCProvider>.Instance);
        GraphQl = Provider.CashCowWalletGraphQlClients["ZEC"];
        CheatMode = new ZcashCheckoutCheatModeExtension(Provider,
            new ZcashLikeSpecificBtcPayNetwork { CryptoCode = "ZEC" }, PaymentMethodId);
    }

    public async Task DisposeAsync()
    {
        GraphQl.Dispose();
        services.Dispose();
        await app.DisposeAsync();
    }
}
