using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using BBTCPayServer.Plugins.ZCash.RPC;
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

        private readonly ConcurrentDictionary<string, ZcashLikeSummary> _summaries =
            new ConcurrentDictionary<string, ZcashLikeSummary>();

        public ConcurrentDictionary<string, ZcashLikeSummary> Summaries => _summaries;

        public ZcashRPCProvider(ZcashLikeConfiguration ZcashLikeConfiguration, EventAggregator eventAggregator, IHttpClientFactory httpClientFactory)
        {
            _ZcashLikeConfiguration = ZcashLikeConfiguration;
            _eventAggregator = eventAggregator;
            DaemonRpcClients =
                _ZcashLikeConfiguration.ZcashLikeConfigurationItems.ToImmutableDictionary(pair => pair.Key,
                    pair => new JsonRpcClient(pair.Value.DaemonRpcUri, "", "", httpClientFactory.CreateClient()));
            WalletRpcClients =
                _ZcashLikeConfiguration.ZcashLikeConfigurationItems.ToImmutableDictionary(pair => pair.Key,
                    pair => new JsonRpcClient(pair.Value.InternalWalletRpcUri, "", "", httpClientFactory.CreateClient()));
        }

        public bool IsConfigured(string cryptoCode) => WalletRpcClients.ContainsKey(cryptoCode) && DaemonRpcClients.ContainsKey(cryptoCode);

        public bool IsAvailable(string cryptoCode)
        {
            cryptoCode = cryptoCode.ToUpperInvariant();
            return _summaries.ContainsKey(cryptoCode) && IsAvailable(_summaries[cryptoCode]);
        }

        private static bool IsAvailable(ZcashLikeSummary summary)
        {
            return summary.Synced &&
                   summary.WalletAvailable;
        }

        internal static bool WalletCheckpointAdvanced(ZcashLikeSummary previousSummary, ZcashLikeSummary summary)
        {
            return IsAvailable(previousSummary) &&
                   IsAvailable(summary) &&
                   summary.WalletHeight > previousSummary.WalletHeight;
        }

        public async Task<ZcashLikeSummary> UpdateSummary(string cryptoCode)
        {
            var normalizedCryptoCode = cryptoCode.ToUpperInvariant();
            if (!DaemonRpcClients.TryGetValue(cryptoCode.ToUpperInvariant(), out var daemonRpcClient) ||
                !WalletRpcClients.TryGetValue(cryptoCode.ToUpperInvariant(), out var walletRpcClient))
            {
                return null;
            }

            var summary = new ZcashLikeSummary();
            try
            {
                var daemonResult =
                    await daemonRpcClient.SendCommandAsync<JsonRpcClient.NoRequestModel, SyncInfoResponse>("sync_info",
                        JsonRpcClient.NoRequestModel.Instance);

                summary.TargetHeight = daemonResult.TargetHeight.GetValueOrDefault(0);
                summary.CurrentHeight = daemonResult.Height;
                summary.TargetHeight = summary.TargetHeight == 0 ? summary.CurrentHeight : summary.TargetHeight;
                summary.Synced = daemonResult.Height >= summary.TargetHeight && summary.CurrentHeight > 0;
                summary.UpdatedAt = DateTime.Now;
                summary.DaemonAvailable = true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                summary.DaemonAvailable = false;
            }

            try
            {
                var walletResult =
                    await walletRpcClient.SendCommandAsync<JsonRpcClient.NoRequestModel, GetHeightResponse>(
                        "get_height", JsonRpcClient.NoRequestModel.Instance);

                summary.WalletHeight = walletResult.Height;
                summary.WalletAvailable = true;
            }
            catch
            {
                summary.WalletAvailable = false;
            }

            var hadPreviousSummary = _summaries.TryGetValue(normalizedCryptoCode, out var previousSummary);
            var changed = !hadPreviousSummary || IsAvailable(previousSummary) != IsAvailable(summary);

            _summaries.AddOrReplace(normalizedCryptoCode, summary);
            if (changed)
            {
                _eventAggregator.Publish(new ZcashDaemonStateChange() { Summary = summary, CryptoCode = normalizedCryptoCode });
            }
            if (hadPreviousSummary && WalletCheckpointAdvanced(previousSummary, summary))
            {
                _eventAggregator.Publish(new ZcashEvent()
                {
                    BlockHash = summary.WalletHeight.ToString(CultureInfo.InvariantCulture),
                    CryptoCode = normalizedCryptoCode
                });
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
