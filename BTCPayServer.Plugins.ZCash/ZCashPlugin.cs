using BTCPayServer.Services;
using System.Globalization;
using System.Linq;
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
using NBXplorer;
using BTCPayServer.Plugins.ZCash.Services;

namespace BTCPayServer.Plugins.Altcoins;

public class ZCashPlugin : BaseBTCPayServerPlugin
{

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.1.0" }
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
                    "ZEC_BTC = kraken(ZEC_BTC)",
                    "ZEC_USD = kraken(ZEC_USD)"
                },
            CryptoImagePath = "zcash.png",
            UriScheme = "zcash"
        };
        var blockExplorerLink = chainName == ChainName.Mainnet
                    ? "https://www.exploreZcash.com/transaction/{0}"
                    : "https://testnet.xmrchain.net/tx/{0}";
        var pmi = PaymentTypes.CHAIN.GetPaymentMethodId("ZEC");
        services.AddDefaultPrettyName(pmi, network.DisplayName);
        services.AddBTCPayNetwork(network)
                .AddTransactionLinkProvider(pmi, new SimpleTransactionLinkProvider(blockExplorerLink));


        services.AddSingleton(provider =>
            ConfigureZcashLikeConfiguration(provider));
        services.AddSingleton<ZcashRPCProvider>();
        services.AddHostedService<ZcashLikeSummaryUpdaterHostedService>();
        services.AddHostedService<ZcashListener>();


        services.AddSingleton<IPaymentMethodHandler>(provider =>
        (IPaymentMethodHandler)ActivatorUtilities.CreateInstance(provider, typeof(ZcashLikePaymentMethodHandler), new object[] { network }));
        services.AddSingleton<IPaymentLinkExtension>(provider =>
(IPaymentLinkExtension)ActivatorUtilities.CreateInstance(provider, typeof(ZcashPaymentLinkExtension), new object[] { network, pmi }));
        services.AddSingleton<ICheckoutModelExtension>(provider =>
(ICheckoutModelExtension)ActivatorUtilities.CreateInstance(provider, typeof(ZcashCheckoutModelExtension), new object[] { network, pmi }));

        services.AddUIExtension("store-nav", "/Views/ZCash/StoreNavZcashExtension.cshtml");
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
            var daemonUri =
                configuration.GetOrDefault<Uri?>($"{ZcashLikeSpecificBtcPayNetwork.CryptoCode}_daemon_uri",
                    null);
            var walletDaemonUri =
                configuration.GetOrDefault<Uri?>(
                    $"{ZcashLikeSpecificBtcPayNetwork.CryptoCode}_wallet_daemon_uri", null);
            var walletDaemonWalletDirectory =
                configuration.GetOrDefault<string?>(
                    $"{ZcashLikeSpecificBtcPayNetwork.CryptoCode}_wallet_daemon_walletdir", null);
            if (daemonUri == null || walletDaemonUri == null || walletDaemonWalletDirectory == null)
            {
                throw new ConfigException($"{ZcashLikeSpecificBtcPayNetwork.CryptoCode} is misconfigured");
            }

            result.ZcashLikeConfigurationItems.Add(ZcashLikeSpecificBtcPayNetwork.CryptoCode, new ZcashLikeConfigurationItem()
            {
                DaemonRpcUri = daemonUri,
                InternalWalletRpcUri = walletDaemonUri,
                WalletDirectory = walletDaemonWalletDirectory
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

