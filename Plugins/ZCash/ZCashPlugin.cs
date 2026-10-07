using BTCPayServer.Services;
using System.Globalization;
using System.Linq;
using System.IO;
using NBitcoin;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Plugins.ZCash.Payments;
using BTCPayServer.Plugins.ZCash;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.ZCash.Configuration;
using System;
using Microsoft.Extensions.Configuration;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.ZCash.Data;
using NBXplorer;
using BTCPayServer.Plugins.ZCash.Services;

namespace BTCPayServer.Plugins.Altcoins;

public class ZCashPlugin : BaseBTCPayServerPlugin
{

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    };

    // Change this if you want another zcash coin
    public override void Execute(IServiceCollection services)
	{
        var pluginServices = (PluginServiceCollection)services;
        var prov = pluginServices.BootstrapServices.GetRequiredService<NBXplorerNetworkProvider>();
        var chainName = prov.NetworkType;
        var network = new ZcashLikeSpecificBtcPayNetwork()
        {
            CryptoCode = "ZEC",
            DisplayName = "Zcash",
            Divisibility = 8,
            DefaultRateRules = new[]
            {
                    "ZEC_X = ZEC_BTC * BTC_X",
                    "ZEC_BTC = binance(ZEC_BTC)",
                    "ZEC_USD = kraken(ZEC_USD)",
                    "ZEC_EUR = kraken(ZEC_EUR)"
                },
            CryptoImagePath = "zcash.png",
            UriScheme = "zcash"
        };
        var blockExplorerLink = chainName == ChainName.Mainnet
                    ? "https://mainnet.zcashexplorer.app/transactions/{0}"
                    : "https://testnet.zcashexplorer.app/transactions/{0}";
        var pmi = PaymentTypes.CHAIN.GetPaymentMethodId("ZEC");
        services.AddDefaultPrettyName(pmi, network.DisplayName);
        services.AddBTCPayNetwork(network)
                .AddTransactionLinkProvider(pmi, new SimpleTransactionLinkProvider(blockExplorerLink));


        services.AddSingleton(provider =>
            ConfigureZcashLikeConfiguration(provider));
        services.AddSingleton<ZcashRPCProvider>();
        services.AddHostedService<ZcashLikeSummaryUpdaterHostedService>();
        services.AddHostedService<ZcashWalletEventHostedService>();
        services.AddHostedService<ZcashListener>();
        
        services.AddSingleton<ZcashPluginDbContextFactory>();
        services.AddDbContextFactory<ZcashPluginDbContext>((provider, optionsBuilder) =>
        {
            var factory = provider.GetRequiredService<ZcashPluginDbContextFactory>();
            factory.ConfigureBuilder(optionsBuilder);
        });
        services.AddHostedService<ZcashMigrationRunner>();
        services.AddSingleton(provider =>
            (ICheckoutCheatModeExtension)ActivatorUtilities.CreateInstance(provider, typeof(ZcashCheckoutCheatModeExtension), [network, pmi]));


        services.AddSingleton<IPaymentMethodHandler>(provider =>
        (IPaymentMethodHandler)ActivatorUtilities.CreateInstance(provider, typeof(ZcashLikePaymentMethodHandler), new object[] { network }));
        services.AddSingleton<IPaymentLinkExtension>(provider =>
(IPaymentLinkExtension)ActivatorUtilities.CreateInstance(provider, typeof(ZcashPaymentLinkExtension), new object[] { network, pmi }));
        services.AddSingleton<ICheckoutModelExtension>(provider =>
(ICheckoutModelExtension)ActivatorUtilities.CreateInstance(provider, typeof(ZcashCheckoutModelExtension), new object[] { network, pmi }));

        // services.AddUIExtension("store-nav", "/Views/ZCash/StoreNavZcashExtension.cshtml");
        services.AddUIExtension("store-wallets-nav", "/Views/ZCash/StoreWalletsNavZcashExtension.cshtml");
        services.AddUIExtension("store-invoices-payments", "/Views/ZCash/ViewZcashLikePaymentData.cshtml");
        services.AddSingleton<ISyncSummaryProvider, ZcashSyncSummaryProvider>();

    }
    static ZcashLikeConfiguration ConfigureZcashLikeConfiguration(IServiceProvider serviceProvider)
    {
        var configuration = serviceProvider.GetService<IConfiguration>();
        var btcPayNetworkProvider = serviceProvider.GetRequiredService<BTCPayNetworkProvider>();
        var result = new ZcashLikeConfiguration();

        var supportedNetworks = btcPayNetworkProvider.GetAll()
            .OfType<ZcashLikeSpecificBtcPayNetwork>();

        foreach (var ZcashLikeSpecificBtcPayNetwork in supportedNetworks)
        {
            var walletBackendTypeStr =
                configuration.GetOrDefault<string>(
                    $"{ZcashLikeSpecificBtcPayNetwork.CryptoCode}_wallet_backend_type", null);

            var walletBackendType =
                !string.IsNullOrEmpty(walletBackendTypeStr) &&
                Enum.TryParse<WalletBackend>(walletBackendTypeStr, true, out var parsed)
                    ? parsed
                    : WalletBackend.Walletd;
            var daemonUri =
                configuration.GetOrDefault<Uri?>($"{ZcashLikeSpecificBtcPayNetwork.CryptoCode}_daemon_uri",
                    null);
            var cashcowDaemonUri =
                configuration.GetOrDefault<Uri?>($"{ZcashLikeSpecificBtcPayNetwork.CryptoCode}_cashcow_daemon_uri",
                    null);
            var cashcowMinerSeed =
                configuration.GetOrDefault<string?>($"{ZcashLikeSpecificBtcPayNetwork.CryptoCode}_cashcow_miner_seed",
                    null);
            var walletDaemonUri =
                configuration.GetOrDefault<Uri?>(
                    $"{ZcashLikeSpecificBtcPayNetwork.CryptoCode}_wallet_daemon_uri", null);
            var graphQlEndpointUri =
                configuration.GetOrDefault<Uri?>(
                    $"{ZcashLikeSpecificBtcPayNetwork.CryptoCode}_wallet_graphql_uri", null);
            var cashcowEndpointUri =
                configuration.GetOrDefault<Uri?>(
                    $"{ZcashLikeSpecificBtcPayNetwork.CryptoCode}_wallet_cashcow_uri", null);
            var walletDaemonWalletDirectory =
                configuration.GetOrDefault<string?>(
                    $"{ZcashLikeSpecificBtcPayNetwork.CryptoCode}_wallet_daemon_walletdir", null);
            var walletDaemonConfigFile =
                configuration.GetOrDefault<string?>(
                    $"{ZcashLikeSpecificBtcPayNetwork.CryptoCode}_wallet_daemon_config_path",
                    string.IsNullOrEmpty(walletDaemonWalletDirectory) ? null : Path.Combine(walletDaemonWalletDirectory, "config.json"));
            if (walletBackendType == WalletBackend.Walletd &&
                (daemonUri == null || walletDaemonUri == null || walletDaemonWalletDirectory == null))
            {
                throw new ConfigException($"{ZcashLikeSpecificBtcPayNetwork.CryptoCode} is misconfigured");
            }
            if (walletBackendType == WalletBackend.ZkoolGraphQL && graphQlEndpointUri == null)
            {
                throw new ConfigException($"{ZcashLikeSpecificBtcPayNetwork.CryptoCode} GraphQL wallet is misconfigured");
            }
            // Temp patch
            if (!string.IsNullOrEmpty(walletDaemonWalletDirectory) && System.IO.File.Exists(Path.Combine(walletDaemonWalletDirectory, "zec-wallet2.db")) && walletDaemonConfigFile == Path.Combine(walletDaemonWalletDirectory, "config.json")) {
                walletDaemonConfigFile = Path.Combine(walletDaemonWalletDirectory, "config2.json");
            }
            // Temp patch
            if (walletDaemonConfigFile == "/data/config2.json" && !string.IsNullOrEmpty(walletDaemonWalletDirectory)) {
                walletDaemonConfigFile = Path.Combine(walletDaemonWalletDirectory, "config2.json");
            }

            result.ZcashLikeConfigurationItems.Add(ZcashLikeSpecificBtcPayNetwork.CryptoCode, new ZcashLikeConfigurationItem()
            {
                DaemonRpcUri = daemonUri,
                CashcowDaemonRpcUri = cashcowDaemonUri,
                CashcowMinerSeed = cashcowMinerSeed,
                InternalWalletRpcUri = walletDaemonUri,
                GraphQlEndpointUri = graphQlEndpointUri,
                CashCowEndpointUri = cashcowEndpointUri,
                WalletDirectory = walletDaemonWalletDirectory,
                ConfigFile = walletDaemonConfigFile,
                WalletBackend = walletBackendType
            });
        }
        return result;
    }
    class SimpleTransactionLinkProvider : DefaultTransactionLinkProvider
    {
        public SimpleTransactionLinkProvider(string blockExplorerLink) : base(blockExplorerLink)
        {
        }

        public override string? GetTransactionLink(string paymentId)
        {
            if (string.IsNullOrEmpty(BlockExplorerLink))
                return null;
            return string.Format(CultureInfo.InvariantCulture, BlockExplorerLink, paymentId);
        }
    }
}
