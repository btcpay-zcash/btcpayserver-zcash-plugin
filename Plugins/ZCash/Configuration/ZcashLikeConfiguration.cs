using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.ZCash.Configuration
{
    public class ZcashLikeConfiguration
    {
        public Dictionary<string, ZcashLikeConfigurationItem> ZcashLikeConfigurationItems { get; set; } =
            new Dictionary<string, ZcashLikeConfigurationItem>();
    }

    public enum WalletBackend { Walletd, ZkoolGraphQL }

    public class ZcashLikeConfigurationItem
    {
        public Uri DaemonRpcUri { get; set; }
        public Uri CashcowDaemonRpcUri { get; set; }
        public string CashcowMinerSeed { get; set; }
        public Uri InternalWalletRpcUri { get; set; }
        public Uri GraphQlEndpointUri { get; set; }
        public Uri CashCowEndpointUri { get; set; }
        public string WalletDirectory { get; set; }
        public WalletBackend WalletBackend { get; set; } = WalletBackend.Walletd;
        public string ConfigFile { get; set; }
    }
}
