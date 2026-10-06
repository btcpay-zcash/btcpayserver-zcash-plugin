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
        public ImmutableDictionary<string, JsonRpcClient> CashcowDaemonRpcClients;
        public ImmutableDictionary<string, JsonRpcClient> WalletRpcClients;
        public ImmutableDictionary<string, ZkoolGraphQlClient> CashCowWalletGraphQlClients;
        private const int MaturityThreshold = 100;
        private readonly string CashcowMinerSeed;
        public ImmutableDictionary<string, IZcashWalletBackend> WalletBackends;
        private const int CashCowAccountId = 1;
        

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
            CashcowDaemonRpcClients =
                _ZcashLikeConfiguration.ZcashLikeConfigurationItems
                    .Where(pair => pair.Value.CashcowDaemonRpcUri is not null)
                    .ToImmutableDictionary(
                        pair => pair.Key,
                        pair => new JsonRpcClient(pair.Value.CashcowDaemonRpcUri, "", "", httpClientFactory.CreateClient()));
            CashcowMinerSeed =
                _ZcashLikeConfiguration.ZcashLikeConfigurationItems
                    .Select(pair => pair.Value.CashcowMinerSeed)
                    .FirstOrDefault(seed => seed is not null)
                    ?.ToString();
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
            CashCowWalletGraphQlClients =
                _ZcashLikeConfiguration.ZcashLikeConfigurationItems
                    .Where(pair => pair.Value.CashCowEndpointUri is not null)
                    .ToImmutableDictionary(pair => pair.Key,
                        pair => new ZkoolGraphQlClient(pair.Value.CashCowEndpointUri, httpClientFactory.CreateClient()));
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

            if (!WalletBackends.TryGetValue(cryptoCode.ToUpperInvariant(), out var walletBackend))
            {
                Console.WriteLine($"No backend found for {cryptoCode}. Available: {string.Join(",", WalletBackends.Keys)}");
                return null;
            }
            CashcowDaemonRpcClients.TryGetValue(cryptoCode.ToUpperInvariant(), out var cashcowDaemonRpcClient);

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
                await MakeCashCowFat(cashCow, cashcowDaemonRpcClient);
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
        
        private async Task<long> CreateTestWalletAsync(ZkoolGraphQlClient graphQlClient, string label, string key, string passphrase, long birthHeight, bool useInternalAddresses)
        {
            var data = await graphQlClient.SendAsync(@"
mutation($newAccount: NewAccount!) {
  createAccount(newAccount: $newAccount)
}", new
            {
                newAccount = new
                {
                    name = label,
                    key = key,
                    passphrase = passphrase,
                    aindex = 0,
                    birth = birthHeight,
                    useInternal = useInternalAddresses
                }
            });


            return data["createAccount"]!.Value<long>();
        }
        
        private async Task MakeCashCowFat(ZkoolGraphQlClient cashcow, JsonRpcClient daemon)
        {
            var existing = await cashcow.SendAsync(@"
                query($accountFilter: AccountFilter) {
                accounts(accountFilter: $accountFilter) {
                    id
                    balance
                }
                }", new { accountFilter = new { id = CashCowAccountId } });

            if (existing["accounts"]?.Any() != true)
            {
                _logger.LogInformation("Creating cashcow wallet...");
                await CreateTestWalletAsync(cashcow, "cashcow", "", "", 1, false);
            }

            var data = await cashcow.SendAsync(@"
                query($accountFilter: AccountFilter) {
                accounts(accountFilter: $accountFilter) {
                    id
                    balance
                }
                }", new { accountFilter = new { id = CashCowAccountId } });

            var account = data["accounts"]?.FirstOrDefault();
            if (account == null)
            {
                _logger.LogWarning("Cashcow account not found");
                return;
            }

            var balance = ParseDecimal(account["balance"]);
            if (balance != 0)
            {
                return;
            }

            // 1. Mine coinbase to the zebra-configured miner address.
            _logger.LogInformation("Mining blocks for the cashcow...");
            // await daemon.SendCommandAsync<GenerateBlocksNoAddress, JsonRpcClient.NoRequestModel>(
            //     "generate",
            //     new GenerateBlocksNoAddress { AmountOfBlocks = MaturityThreshold + 10 });
            await daemon.SendRpc10CommandAsync<int[], string[]>("generate", new[] { MaturityThreshold + 10 });

            // 2. Wait until lightwalletd ingested the blocks.
            for (int i = 0; i < 120; i++)
            {
                var height = await cashcow.SendAsync("query { currentHeight }");
                if ((height["currentHeight"]?.Value<long>() ?? 0) >= MaturityThreshold + 10)
                {
                    break;
                }
                await Task.Delay(2000);
            }

            // 3. Get a destination address on the cashcow account.
            var addrData = await cashcow.SendAsync(@"
                mutation($idAccount: Int!) {
                newAddresses(idAccount: $idAccount) {
                    ua
                    transparent
                    sapling
                    orchard
                }
                }", new { idAccount = CashCowAccountId });
            var destination = addrData["newAddresses"]?["ua"]?.Value<string>()
                            ?? addrData["newAddresses"]?["transparent"]?.Value<string>();
            if (string.IsNullOrEmpty(destination))
            {
                _logger.LogWarning("Could not obtain a cashcow address");
                return;
            }

            // 4. Find or create the miner account over the coinbase outputs.
            var minerExisting = await cashcow.SendAsync(@"
                query($accountFilter: AccountFilter) {
                accounts(accountFilter: $accountFilter) {
                    id
                }
                }", new { accountFilter = new { name = "cashcow-miner" } });

            long minerId;
            var minerAccount = minerExisting["accounts"]?.FirstOrDefault();
            if (minerAccount != null)
            {
                minerId = minerAccount["id"]!.Value<long>();
            }
            else
            {
                minerId = await CreateTestWalletAsync(cashcow, "cashcow-miner", CashcowMinerSeed, "", 1, false);
            }

            await cashcow.SendAsync(@"
                mutation($idAccounts: [Int!]!) {
                synchronize(idAccounts: $idAccounts)
                }", new { idAccounts = new[] { (int)minerId } });

            // 5. Shield matured notes from the miner to the cashcow address.
            var matureHeight = (data["currentHeight"]?.Value<long>() ?? MaturityThreshold + 10) - MaturityThreshold;
            var notesData = await cashcow.SendAsync(@"
                query($idAccount: Int!) {
                notesByAccount(idAccount: $idAccount) { id height value }
                }", new { idAccount = minerId });

            var notes = notesData["notesByAccount"]?
                .Where(n => (n["height"]?.Value<long>() ?? 0) < matureHeight)
                .Take(10)
                .ToList();
            if (notes == null || notes.Count == 0)
            {
                _logger.LogWarning("No mature notes found for the cashcow miner");
                return;
            }

            var total = notes.Sum(n => ParseDecimal(n["value"]))
                .ToString("0.########", CultureInfo.InvariantCulture);
            await cashcow.SendAsync(@"
                mutation($idAccount: Int!, $payment: Payment!) {
                pay(idAccount: $idAccount, payment: $payment)
                }", new
                {
                    idAccount = minerId,
                    payment = new
                    {
                        recipients = new[] { new { address = destination, amount = total } },
                        recipientPaysFee = true,
                        confirmations = MaturityThreshold
                    }
                });

            // 6. Confirm the shielding tx, then sync the cashcow account.
            var h = await cashcow.SendAsync("query { currentHeight }");
            // await daemon.SendCommandAsync<GenerateBlocksNoAddress, JsonRpcClient.NoRequestModel>(
            //     "generate", new GenerateBlocksNoAddress { AmountOfBlocks = 10 });
            await daemon.SendRpc10CommandAsync<int[], string[]>("generate", new[] { 10 });
            await cashcow.SendAsync(@"
                mutation($idAccounts: [Int!]!) {
                synchronize(idAccounts: $idAccounts)
                }", new { idAccounts = new[] { CashCowAccountId } });

            _logger.LogInformation("Mining succeed! Cashcow funded");
        }
    }
}
