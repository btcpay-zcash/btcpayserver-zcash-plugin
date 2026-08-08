using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Plugins.ZCash.Configuration;
using BTCPayServer.Plugins.ZCash.RPC;
using NBitcoin;

namespace BTCPayServer.Plugins.ZCash.Services
{
    public class ZcashRPCProvider
    {
        private readonly ZcashLikeConfiguration _ZcashLikeConfiguration;
        private readonly EventAggregator _eventAggregator;
        public ImmutableDictionary<string, JsonRpcClient> DaemonRpcClients;
        public ImmutableDictionary<string, JsonRpcClient> WalletRpcClients;
        public ImmutableDictionary<string, IZcashWalletBackend> WalletBackends;

        private readonly ConcurrentDictionary<string, ZcashLikeSummary> _summaries =
            new ConcurrentDictionary<string, ZcashLikeSummary>();

        public ConcurrentDictionary<string, ZcashLikeSummary> Summaries => _summaries;

        public ZcashRPCProvider(ZcashLikeConfiguration ZcashLikeConfiguration, EventAggregator eventAggregator, IHttpClientFactory httpClientFactory)
        {
            _ZcashLikeConfiguration = ZcashLikeConfiguration;
            _eventAggregator = eventAggregator;
            DaemonRpcClients =
                _ZcashLikeConfiguration.ZcashLikeConfigurationItems
                    .Where(pair => pair.Value.WalletBackendType == WalletBackendType.ZcashWalletd && pair.Value.DaemonRpcUri is not null)
                    .ToImmutableDictionary(pair => pair.Key,
                        pair => new JsonRpcClient(pair.Value.DaemonRpcUri, "", "", httpClientFactory.CreateClient()));
            WalletRpcClients =
                _ZcashLikeConfiguration.ZcashLikeConfigurationItems
                    .Where(pair => pair.Value.WalletBackendType == WalletBackendType.ZcashWalletd && pair.Value.InternalWalletRpcUri is not null)
                    .ToImmutableDictionary(pair => pair.Key,
                        pair => new JsonRpcClient(pair.Value.InternalWalletRpcUri, "", "", httpClientFactory.CreateClient()));
            WalletBackends =
                _ZcashLikeConfiguration.ZcashLikeConfigurationItems.ToImmutableDictionary(pair => pair.Key, pair =>
                {
                    IZcashWalletBackend backend = pair.Value.WalletBackendType switch
                    {
                        WalletBackendType.ZkoolGraphQl => new ZkoolGraphQlBackend(pair.Key,
                            new ZkoolGraphQlClient(pair.Value.GraphQlEndpointUri, httpClientFactory.CreateClient())),
                        _ => new ZcashWalletdBackend(pair.Key,
                            new JsonRpcClient(pair.Value.DaemonRpcUri, "", "", httpClientFactory.CreateClient()),
                            new JsonRpcClient(pair.Value.InternalWalletRpcUri, "", "", httpClientFactory.CreateClient()))
                    };
                    return backend;
                });
        }

        public bool IsConfigured(string cryptoCode) => WalletBackends.ContainsKey(cryptoCode.ToUpperInvariant());

        public bool IsAvailable(string cryptoCode)
        {
            cryptoCode = cryptoCode.ToUpperInvariant();
            return _summaries.ContainsKey(cryptoCode) && IsAvailable(_summaries[cryptoCode]);
        }

        private bool IsAvailable(ZcashLikeSummary summary)
        {
            return summary.Synced &&
                   summary.WalletAvailable;
        }

        public async Task<ZcashLikeSummary> UpdateSummary(string cryptoCode)
        {
            if (!WalletBackends.TryGetValue(cryptoCode.ToUpperInvariant(), out var walletBackend))
            {
                return null;
            }

            var summary = new ZcashLikeSummary();
            try
            {
                var walletStatus = await walletBackend.GetSyncStatusAsync();

                summary.TargetHeight = walletStatus.TargetHeight;
                summary.CurrentHeight = walletStatus.CurrentHeight;
                summary.WalletHeight = walletStatus.WalletHeight;
                summary.Synced = walletStatus.Synced;
                summary.UpdatedAt = DateTime.Now;
                summary.DaemonAvailable = walletStatus.DaemonAvailable;
                summary.WalletAvailable = walletStatus.WalletAvailable;
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                summary.DaemonAvailable = false;
                summary.WalletAvailable = false;
                summary.UpdatedAt = DateTime.Now;
            }

            var changed = !_summaries.ContainsKey(cryptoCode) || IsAvailable(cryptoCode) != IsAvailable(summary);

            _summaries.AddOrReplace(cryptoCode, summary);
            if (changed)
            {
                _eventAggregator.Publish(new ZcashDaemonStateChange() { Summary = summary, CryptoCode = cryptoCode });
            }

            return summary;
        }


        public class ZcashDaemonStateChange
        {
            public string CryptoCode { get; set; }
            public ZcashLikeSummary Summary { get; set; }
        }

        public class ZcashLikeSummary
        {
            public bool Synced { get; set; }
            public long CurrentHeight { get; set; }
            public long WalletHeight { get; set; }
            public long TargetHeight { get; set; }
            public DateTime UpdatedAt { get; set; }
            public bool DaemonAvailable { get; set; }
            public bool WalletAvailable { get; set; }

            public override String ToString() { return String.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3} {4} {5}", Synced, CurrentHeight, TargetHeight, WalletHeight, DaemonAvailable, WalletAvailable); }
        }
    }
}
