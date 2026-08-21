using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Plugins.ZCash.Configuration;
using BTCPayServer.Plugins.ZCash.Data;
using BTCPayServer.Plugins.ZCash.RPC;
using BTCPayServer.Services;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ZCash.Services
{
    public class ZcashRPCProvider
    {
        private readonly ZcashLikeConfiguration _ZcashLikeConfiguration;
        private readonly ILogger<ZcashRPCProvider> _logger;
        private readonly EventAggregator _eventAggregator;
        private readonly ZcashPluginDbContextFactory _dbContextFactory;
        private readonly BTCPayServerEnvironment environment;
        public ImmutableDictionary<string, JsonRpcClient> DaemonRpcClients;
        public ImmutableDictionary<string, JsonRpcClient> WalletRpcClients;
        public ImmutableDictionary<string, ZkoolGraphQlClient> CashCowWalletGraphQlClients;
        public ImmutableDictionary<string, IZcashWalletBackend> WalletBackends;
        

        private readonly ConcurrentDictionary<string, ZcashLikeSummary> _summaries =
            new ConcurrentDictionary<string, ZcashLikeSummary>();

        public ConcurrentDictionary<string, ZcashLikeSummary> Summaries => _summaries;

        public ZcashRPCProvider(ZcashLikeConfiguration ZcashLikeConfiguration,
            EventAggregator eventAggregator,
            IHttpClientFactory httpClientFactory,
            ZcashPluginDbContextFactory dbContextFactory,
            BTCPayServerEnvironment environment,
            ILogger<ZcashRPCProvider> logger
        )
        {
            _ZcashLikeConfiguration = ZcashLikeConfiguration;
            _eventAggregator = eventAggregator;
            _dbContextFactory = dbContextFactory;
            this.environment = environment;
            _logger = logger;
            DaemonRpcClients =
                _ZcashLikeConfiguration.ZcashLikeConfigurationItems
                    .Where(pair => pair.Value.WalletBackend == WalletBackend.Walletd && pair.Value.DaemonRpcUri is not null)
                    .ToImmutableDictionary(pair => pair.Key,
                        pair => new JsonRpcClient(pair.Value.DaemonRpcUri, "", "", httpClientFactory.CreateClient()));
            WalletRpcClients =
                _ZcashLikeConfiguration.ZcashLikeConfigurationItems
                    .Where(pair => pair.Value.WalletBackend == WalletBackend.Walletd && pair.Value.InternalWalletRpcUri is not null)
                    .ToImmutableDictionary(pair => pair.Key,
                        pair => new JsonRpcClient(pair.Value.InternalWalletRpcUri, "", "", httpClientFactory.CreateClient()));
            WalletBackends =
                _ZcashLikeConfiguration.ZcashLikeConfigurationItems.ToImmutableDictionary(
                    pair => pair.Key,
                    pair => (IZcashWalletBackend)(pair.Value.WalletBackend switch
                    {
                        WalletBackend.ZkoolGraphQL => new ZkoolGraphQlBackend(pair.Key,
                            new ZkoolGraphQlClient(pair.Value.GraphQlEndpointUri, httpClientFactory.CreateClient()),
                            _dbContextFactory,
                            this.environment
                        ),
                        _ => new ZcashWalletdBackend(pair.Key,
                            new JsonRpcClient(pair.Value.DaemonRpcUri, "", "", httpClientFactory.CreateClient()),
                            new JsonRpcClient(pair.Value.InternalWalletRpcUri, "", "", httpClientFactory.CreateClient()))
                    }));
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
            Console.WriteLine($"UpdateSummary START for {cryptoCode}");

            if (!DaemonRpcClients.TryGetValue(cryptoCode.ToUpperInvariant(), out var daemonRpcClient) ||
                !WalletBackends.TryGetValue(cryptoCode.ToUpperInvariant(), out var walletBackend))
            {
                Console.WriteLine($"No backend found for {cryptoCode}.",
                    cryptoCode, string.Join(",", WalletBackends.Keys));
                return null;
            }

            var summary = new ZcashLikeSummary();
            try
            {
                var walletStatus = await walletBackend.GetSyncStatusAsync();
                Console.WriteLine($"SyncStatus OK for {cryptoCode}: {walletStatus}");
                summary.CurrentHeight = walletStatus.CurrentHeight;
                summary.WalletHeight = walletStatus.WalletHeight;
                summary.TargetHeight = walletStatus.TargetHeight;
                summary.Synced = walletStatus.Synced;
                summary.DaemonAvailable = walletStatus.DaemonAvailable;
                summary.WalletAvailable = walletStatus.WalletAvailable;
                summary.UpdatedAt = DateTime.UtcNow;
            }
            catch (Exception e)
            {
                Console.WriteLine($"GetSyncStatusAsync THREW for {cryptoCode}, ${e}");
                summary.DaemonAvailable = false;
                summary.WalletAvailable = false;
            }
            var changed = !_summaries.ContainsKey(cryptoCode) || IsAvailable(cryptoCode) != IsAvailable(summary);
            
            if (environment.CheatMode &&
                CashCowWalletGraphQlClients.TryGetValue(cryptoCode.ToUpperInvariant(), out var cashCow))
            {
                await MakeCashCowFat(cashCow, daemonRpcClient);
            }

            _summaries.AddOrReplace(cryptoCode, summary);
            if (changed)
            {
                _eventAggregator.Publish(new ZcashDaemonStateChange { Summary = summary, CryptoCode = cryptoCode });
            }

            Console.WriteLine($"UpdateSummary END for {cryptoCode}: {summary}");
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
        
        private static decimal ParseDecimal(JToken token)
        {
            if (token == null)
            {
                return 0m;
            }

            return decimal.Parse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
