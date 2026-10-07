using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBTCPayServer.Plugins.ZCash.RPC;
using BTCPayServer.Plugins.ZCash.Configuration;
using BTCPayServer.Plugins.ZCash.Data;
using BTCPayServer.Plugins.ZCash.Data.Models;
using BTCPayServer.Plugins.ZCash.RPC;
using BTCPayServer.Services;
using GraphQL;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ZCash.Services
{
    public class ZkoolGraphQlBackend : IZcashWalletBackend
    {
        private readonly ZkoolGraphQlClient _graphQlClient;
        private readonly BTCPayServerEnvironment environment;
        private readonly SemaphoreSlim _pollingLock = new SemaphoreSlim(1, 1);
        private long? _lastKnownHeight;
        private readonly ZcashPluginDbContextFactory _dbContextFactory;
        // private readonly ILogger<ZkoolGraphQlBackend> _logger;
        
        public ZkoolGraphQlBackend(string cryptoCode,
            ZkoolGraphQlClient graphQlClient,
            ZcashPluginDbContextFactory dbContextFactory,
            BTCPayServerEnvironment environment
            // ILogger<ZkoolGraphQlBackend> logger
        )
        {
            CryptoCode = cryptoCode;
            _graphQlClient = graphQlClient;
            _dbContextFactory = dbContextFactory;
            this.environment = environment;
            // _logger = logger;
        }

        public string CryptoCode { get; }
        public WalletBackend BackendType => WalletBackend.ZkoolGraphQL;
        public bool UsesWalletFile => false;

        public async Task<WalletSyncStatus> GetSyncStatusAsync(CancellationToken cancellationToken = default)
        {
          Console.WriteLine($"Start GraphQL GetSyncStatusAsync");
            var result = new WalletSyncStatus();
            try
            {
                var currentHeight = await GetCurrentHeightAsync(cancellationToken);
                result.CurrentHeight = currentHeight;
                result.TargetHeight = currentHeight;
                result.WalletHeight = currentHeight;
                result.Synced = currentHeight > 0;
                result.DaemonAvailable = true;
                result.WalletAvailable = true;
                Console.WriteLine($"GetCurrentHeightAsync SUCCESS {result.CurrentHeight}");

                await GetAccountsAsync(cancellationToken);
            }
            catch(Exception e)
            {
              Console.WriteLine($"GetSyncStatusAsync THREW: ${e}");
                result.WalletAvailable = false;
                result.DaemonAvailable = result.CurrentHeight > 0;
                result.Synced = false;
            }

            return result;
        }
        
        public async Task<long> SynchronizeAsync(IReadOnlyList<long> accountIds, CancellationToken cancellationToken)
        {
            var data = await _graphQlClient.SendAsync(@"
mutation($idAccounts: [Int!]!) {
  synchronize(idAccounts: $idAccounts)
}", new { idAccounts = accountIds.Select(id => (int)id).ToArray() }, cancellationToken);

            return data["synchronize"]?.Value<long>() ?? 0;
        }

        public async Task<IReadOnlyList<WalletAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
        {
            var data = await _graphQlClient.SendAsync(@"
query {
  accounts {
    id
    name
    aindex
    height
    balance
  }
}", cancellationToken: cancellationToken);

            return data["accounts"]?.Select(account => new WalletAccount
            {
                AccountIndex = account["id"]?.Value<long>() ?? 0,
                Label = account["name"]?.Value<string>(),
                Height = account["height"]?.Value<long>() ?? 0,
                Balance = ParseDecimal(account["balance"]),
                UnlockedBalance = ParseDecimal(account["balance"])
            }).ToList() ?? new List<WalletAccount>();
        }

        public async Task<WalletAccountCreationResult> CreateAccountAsync(WalletAccountCreationRequest request, CancellationToken cancellationToken = default)
        {
            // Temporarily disabled: deployed zkool builds may not expose Account.ufvk.
            // Store-level viewing-key hash checks still prevent sharing keys across stores.
            /*
            var existing = await _graphQlClient.SendAsync("query { accounts { id name ufvk } }", cancellationToken: cancellationToken);
            var matching = existing["accounts"]?.FirstOrDefault(a => a["ufvk"]?.Value<string>() == request.Key?.Trim());
            if (matching != null)
            {
                if (matching["name"]?.Value<string>() != request.Label)
                    throw new InvalidOperationException("This viewing key is already imported by another account. Use a different viewing key.");
                // Recover an import whose store save failed without creating another wallet account.
                return new WalletAccountCreationResult { AccountIndex = matching["id"]!.Value<long>() };
            }
            */

            var data = await _graphQlClient.SendAsync(@"
mutation($newAccount: NewAccount!) {
  createAccount(newAccount: $newAccount)
}", new
            {
                newAccount = new
                {
                    name = request.Label,
                    key = request.Key,
                    passphrase = request.Passphrase,
                    aindex = 0,
                    birth = request.BirthHeight,
                    useInternal = request.UseInternalAddresses
                }
            }, cancellationToken);

            var createdId = data["createAccount"]!.Value<long>();
            // if (createdId == null)
            // {
            //     throw new InvalidOperationException($"Unable to create a new account with key {request.Key}");
            // }
            var account = await GetAccountAsync(createdId, cancellationToken);
            var address = await CreateOrGetAddressAsync(createdId, cancellationToken);
            return new WalletAccountCreationResult
            {
                AccountIndex = account.AccountIndex,
                Address = address?.Address
            };
        }

        public async Task<WalletAddress> CreateAddressAsync(long accountIndex, string label, CancellationToken cancellationToken = default)
        {
            var accountId = await ResolveGraphQlAccountIdAsync(accountIndex, cancellationToken);
            return await CreateOrGetAddressAsync(accountId, cancellationToken);
        }

        public async Task<WalletBalance> GetBalanceAsync(long accountIndex, CancellationToken cancellationToken = default)
        {
            var accountId = await ResolveGraphQlAccountIdAsync(accountIndex, cancellationToken);
            var data = await _graphQlClient.SendAsync(@"
query($idAccount: Int!) {
  balanceByAccount(idAccount: $idAccount) {
    height
    total
    transparent
    sapling
    orchard
  }
}", new { idAccount = accountId }, cancellationToken);

            var balance = data["balanceByAccount"];
            return new WalletBalance
            {
                Height = balance?["height"]?.Value<long>() ?? 0,
                Total = ToAtomicUnits(balance?["total"]),
                Transparent = ToAtomicUnits(balance?["transparent"]),
                Sapling = ToAtomicUnits(balance?["sapling"]),
                Orchard = ToAtomicUnits(balance?["orchard"])
            };
        }

        // Roughly estimate at 2 transparent in/out + 2 shielded in/out.
        // We cannot implement ZIP-321 here because we don't have the transaction.
        private const long LogicalActionFee = 5000L;

        public Task<WalletFeeEstimate> GetFeeEstimateAsync(
            long accountIndex,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WalletFeeEstimate
            {
                FeePerKb = 4 * LogicalActionFee
            });
        }

        public async Task<IReadOnlyList<WalletTransfer>> GetTransfersAsync(
            long accountIndex,
            IReadOnlyList<long> addressIndices,
            CancellationToken cancellationToken = default)
        {
            var accountId = await ResolveGraphQlAccountIdAsync(accountIndex, cancellationToken);
            var currentHeight = await GetCurrentHeightAsync(cancellationToken);

            // notesByAccount contains only unspent notes. Invoice recovery needs receipts
            // even after the merchant spends them, as well as transactions still in the mempool.
            var data = await _graphQlClient.SendAsync(@"
query($idAccount: Int!) {
  transactionsByAccount(idAccount: $idAccount) {
    txid height notes { value address diversifierIndex }
  }
  unconfirmedByAccount(idAccount: $idAccount) {
    txid notes { value address diversifierIndex }
  }
}", new { idAccount = accountId }, cancellationToken);
            var confirmed = data["transactionsByAccount"]?.SelectMany(tx => FlattenTransactionNotes(accountIndex, currentHeight, tx))
                ?? Enumerable.Empty<WalletTransfer>();
            var unconfirmed = data["unconfirmedByAccount"]?.SelectMany(tx => FlattenUnconfirmedNotes(accountIndex, tx))
                ?? Enumerable.Empty<WalletTransfer>();
            return confirmed.Concat(unconfirmed)
                .Where(t => addressIndices == null || addressIndices.Contains(t.AddressIndex))
                .GroupBy(t => (t.TransactionId, t.AccountIndex, t.AddressIndex, t.Address))
                .Select(g => g.OrderByDescending(t => t.Height).First()).ToList();
        }

        public async Task<WalletTransaction> GetTransactionAsync(long accountIndex, string transactionId, CancellationToken cancellationToken = default)
        {
            var accountId = await ResolveGraphQlAccountIdAsync(accountIndex, cancellationToken);
            var currentHeight = await GetCurrentHeightAsync(cancellationToken);

            try
            {
                var data = await _graphQlClient.SendAsync(@"
query($idAccount: Int!, $txid: String!) {
  transactionById(idAccount: $idAccount, txid: $txid) {
    txid
    height
    notes {
      value
      address
      diversifierIndex
    }
  }
}", new { idAccount = accountId, txid = transactionId }, cancellationToken);

                var transaction = data["transactionById"];
                if (transaction is JObject)
                {
                    var height = transaction["height"]?.Value<long>() ?? 0;
                    return new WalletTransaction
                    {
                        TransactionId = transaction["txid"]?.Value<string>() ?? transactionId,
                        Height = height,
                        Confirmations = GetConfirmations(currentHeight, height),
                        Transfers = FlattenTransactionNotes(accountIndex, currentHeight, transaction)
                    };
                }
            }
            catch (ZkoolGraphQlClient.GraphQlApiException)
            {
            }

            var unconfirmed = await _graphQlClient.SendAsync(@"
query($idAccount: Int!) {
  unconfirmedByAccount(idAccount: $idAccount) {
    txid
    notes {
      value
      address
      diversifierIndex
    }
  }
}", new { idAccount = accountId }, cancellationToken);

            var tx = unconfirmed["unconfirmedByAccount"]?.FirstOrDefault(token =>
                string.Equals(token["txid"]?.Value<string>(), transactionId, StringComparison.OrdinalIgnoreCase));
            if (tx == null)
            {
                return new WalletTransaction
                {
                    TransactionId = transactionId,
                    Transfers = Array.Empty<WalletTransfer>()
                };
            }

            return new WalletTransaction
            {
                TransactionId = tx["txid"]?.Value<string>() ?? transactionId,
                Height = 0,
                Confirmations = 0,
                Transfers = FlattenUnconfirmedNotes(accountIndex, tx)
            };
        }
        
        public async Task<IReadOnlyList<WalletTransfer>> GetTransfersByHeightAsync(
            long accountIndex, long minHeight, IReadOnlyList<long> diversifierIndices,
            CancellationToken cancellationToken = default)
        {
            var accountId = await ResolveGraphQlAccountIdAsync(accountIndex, cancellationToken);
            var currentHeight = await GetCurrentHeightAsync(cancellationToken);

            var data = await _graphQlClient.SendAsync(@"
query($idAccount: Int!, $minHeight: Int) {
  transactionsByAccount(idAccount: $idAccount, height: $minHeight) {
    txid
    height
    notes { value address diversifierIndex }
  }
}", new { idAccount = accountId, minHeight }, cancellationToken);

            return data["transactionsByAccount"]?.SelectMany(tx => FlattenTransactionNotes(accountIndex, currentHeight, tx))
                .Where(t => diversifierIndices == null || diversifierIndices.Contains(t.AddressIndex)).ToList()
                ?? new List<WalletTransfer>();
        }

        public async Task<WalletPreparedPayment> PreparePaymentAsync(long accountIndex, WalletPaymentRequest request, CancellationToken cancellationToken = default)
        {
            var accountId = await ResolveGraphQlAccountIdAsync(accountIndex, cancellationToken);
            var prepared = await _graphQlClient.SendAsync(@"
query($idAccount: Int!, $payment: Payment!) {
  prepareSend(idAccount: $idAccount, payment: $payment)
}", new { idAccount = accountId, payment = MapPayment(request) }, cancellationToken);

            var payload = prepared["prepareSend"]?.Value<string>();
            var decoded = await _graphQlClient.SendAsync(@"
query($pczt: String!) {
  decodePczt(pczt: $pczt) {
    fee
  }
}", new { pczt = payload }, cancellationToken);

            return new WalletPreparedPayment
            {
                Payload = payload,
                Fee = ToAtomicUnits(decoded["decodePczt"]?["fee"])
            };
        }

        public async Task<WalletSentPayment> SendPaymentAsync(long accountIndex, WalletPaymentRequest request, CancellationToken cancellationToken = default)
        {
            var accountId = await ResolveGraphQlAccountIdAsync(accountIndex, cancellationToken);
            var data = await _graphQlClient.SendAsync(@"
mutation($idAccount: Int!, $payment: Payment!) {
  pay(idAccount: $idAccount, payment: $payment)
}", new { idAccount = accountId, payment = MapPayment(request) }, cancellationToken);

            return new WalletSentPayment
            {
                TransactionId = ZkoolGraphQlClient.RequireTransactionId(data["pay"])
            };
        }

        public async Task<IReadOnlyList<ZcashEvent>> PollEventsAsync(CancellationToken cancellationToken = default)
        {
            await _pollingLock.WaitAsync(cancellationToken);
            try
            {
                var events = new List<ZcashEvent>();
                var currentHeight = await GetCurrentHeightAsync(cancellationToken);
                var accounts = await GetAccountsAsync(cancellationToken);

                // Reconcile every poll, including startup and unchanged/reorg heights.
                // Listener writes are idempotent, so a dropped event or failed handler is retried.
                events.Add(new ZcashEvent
                {
                    CryptoCode = CryptoCode,
                    Reconcile = true,
                    BlockHash = _lastKnownHeight.HasValue && _lastKnownHeight != currentHeight
                        ? currentHeight.ToString(CultureInfo.InvariantCulture) : null
                });
                _lastKnownHeight = currentHeight;

                foreach (var account in accounts)
                {
                    var accountId = await ResolveGraphQlAccountIdAsync(account.AccountIndex, cancellationToken);
                    var data = await _graphQlClient.SendAsync(@"
query($idAccount: Int!) {
  unconfirmedByAccount(idAccount: $idAccount) {
    txid
  }
}", new { idAccount = accountId }, cancellationToken);

                    var seen = data["unconfirmedByAccount"]?
                                   .Select(token => token["txid"]?.Value<string>())
                                   .Where(txid => !string.IsNullOrEmpty(txid))
                                   .ToHashSet(StringComparer.OrdinalIgnoreCase)
                               ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    // Notify again until confirmed: publishing once is not an acknowledgement
                    // that the listener persisted the receipt.
                    foreach (var txid in seen)
                    {
                        events.Add(new ZcashEvent
                        {
                            CryptoCode = CryptoCode,
                            AccountIndex = account.AccountIndex,
                            TransactionHash = txid
                        });
                    }
                }

                return events;
            }
            finally
            {
                _pollingLock.Release();
            }
        }

        private async Task<long> GetCurrentHeightAsync(CancellationToken cancellationToken)
        {
            var data = await _graphQlClient.SendAsync("query { currentHeight }", cancellationToken: cancellationToken);
            return data["currentHeight"]?.Value<long>() ?? 0;
        }

        private async Task<long> ResolveGraphQlAccountIdAsync(long accountIndex, CancellationToken cancellationToken)
        {
            var data = await _graphQlClient.SendAsync(@"
query {
  accounts {
    id
    aindex
  }
}", cancellationToken: cancellationToken);

            var account = data["accounts"]?.FirstOrDefault(token => token["id"]?.Value<long>() == accountIndex);
            if (account == null)
            {
                throw new InvalidOperationException($"Could not resolve GraphQL account id for account index {accountIndex}.");
            }

            return account["id"]?.Value<long>() ?? accountIndex;
        }

        private async Task<WalletAccount> GetAccountAsync(long accountId, CancellationToken cancellationToken)
        {
            var data = await _graphQlClient.SendAsync(@"
query($accountFilter: AccountFilter) {
  accounts(accountFilter: $accountFilter) {
    id
    name
    height
    balance
  }
}", new { accountFilter = new { id = accountId } }, cancellationToken);

            var account = data["accounts"]?.FirstOrDefault();
            if (account == null || (account["id"]?.Value<long>() ?? 0) == 0)
            {
                return null;
            }

            return new WalletAccount
            {
                AccountIndex = account["id"]!.Value<long>(),
                Label = account["name"]?.Value<string>(),
                Height = account["height"]?.Value<long>() ?? 0,
                Balance = ParseDecimal(account["balance"]),
                UnlockedBalance = ParseDecimal(account["balance"])
            };
        }

        private async Task<WalletAddress> CreateOrGetAddressAsync(long accountId, CancellationToken cancellationToken)
        {
            var data = await _graphQlClient.SendAsync(@"
mutation($idAccount: Int!) {
  newAddresses(idAccount: $idAccount) {
    ua
    transparent
    sapling
    orchard
    diversifierIndex
  }
}", new { idAccount = accountId }, cancellationToken);
            
            var addresses = MapAddress(data["newAddresses"]);
            
            await using var ctx = _dbContextFactory.CreateContext();
            ctx.Receivers.Add(new ZcashReceiver
            {
                AccountIndex = (int)accountId,
                AddressIndex = checked((int)addresses.AddressIndex),
                UnifiedAddress = addresses.UnifiedAddress,
                TransparentAddress = addresses.TransparentAddress,
                SaplingAddress = addresses.SaplingAddress,
                OrchardAddress = addresses.OrchardAddress,
                // InvoiceId = invoiceId,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await ctx.SaveChangesAsync();

            return addresses;
        }

        private static WalletAddress MapAddress(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            return new WalletAddress
            {
                UnifiedAddress = token["ua"]?.Value<string>(),
                TransparentAddress = token["transparent"]?.Value<string>(),
                SaplingAddress = token["sapling"]?.Value<string>(),
                OrchardAddress = token["orchard"]?.Value<string>(),
                AddressIndex = ToLong(token["diversifierIndex"]),
                Address = SelectPreferredAddress(token)
            };
        }

        private static string SelectPreferredAddress(JToken token)
        {
            return token?["ua"]?.Value<string>()
                   ?? token?["orchard"]?.Value<string>()
                   ?? token?["sapling"]?.Value<string>()
                   ?? token?["transparent"]?.Value<string>();
        }
        
        private static List<WalletTransfer> FlattenTransactionNotes(long accountIndex, long currentHeight, JToken transaction)
        {
            return GroupTransfers(accountIndex,
                transaction?["notes"]?.Select(note => new GraphQlTransferNote
                {
                    Address = note["address"]?.Value<string>(),
                    Amount = ToAtomicUnits(note["value"]),
                    AddressIndex = ToLong(note["diversifierIndex"])
                }) ?? Enumerable.Empty<GraphQlTransferNote>(),
                transaction?["txid"]?.Value<string>(),
                transaction?["height"]?.Value<long>() ?? 0,
                currentHeight);
        }

        private static List<WalletTransfer> FlattenUnconfirmedNotes(long accountIndex, JToken transaction)
        {
            return GroupTransfers(accountIndex,
                transaction?["notes"]?.Select(note => new GraphQlTransferNote
                {
                    Address = note["address"]?.Value<string>(),
                    Amount = ToAtomicUnits(note["value"]),
                    AddressIndex = ToLong(note["diversifierIndex"])
                }) ?? Enumerable.Empty<GraphQlTransferNote>(),
                transaction?["txid"]?.Value<string>(),
                0,
                0);
        }

        private static List<WalletTransfer> GroupTransfers(long accountIndex, IEnumerable<GraphQlTransferNote> notes, string transactionId, long height, long currentHeight)
        {
            var confirmations = GetConfirmations(currentHeight, height);
            return notes
                .Where(note => !string.IsNullOrEmpty(note.Address))
                .GroupBy(note => new { note.Address, note.AddressIndex })
                .Select(group => new WalletTransfer
                {
                    Address = group.Key.Address,
                    AddressIndex = group.Key.AddressIndex,
                    AccountIndex = accountIndex,
                    Amount = group.Sum(note => note.Amount),
                    TransactionId = transactionId,
                    Height = height,
                    Confirmations = confirmations
                })
                .ToList();
        }

        private static long GetConfirmations(long currentHeight, long height)
        {
            return height <= 0 || currentHeight <= 0 ? 0 : Math.Max(currentHeight - height + 1, 0);
        }

        private static decimal ParseDecimal(JToken token)
        {
            if (token == null)
            {
                return 0m;
            }

            return decimal.Parse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static long ToAtomicUnits(JToken token)
        {
            return token == null ? 0 : checked((long) decimal.Round(ParseDecimal(token) * 100_000_000m, MidpointRounding.AwayFromZero));
        }

        private static long ToLong(JToken token)
        {
            return token == null
                ? 0
                : checked((long) decimal.Round(ParseDecimal(token), MidpointRounding.AwayFromZero));
        }

        private static object MapPayment(WalletPaymentRequest request)
        {
            return new
            {
                recipients = request.Recipients?.Select(recipient => new
                {
                    address = recipient.Address,
                    amount = ToGraphQlAmount(recipient.Amount),
                    memo = recipient.Memo,
                    assetDesc = recipient.AssetDescriptor
                }).ToList(),
                srcPools = request.SourcePools,
                recipientPaysFee = request.RecipientPaysFee,
                confirmations = request.Confirmations
            };
        }

        private static string ToGraphQlAmount(long amount)
        {
            return (amount / 100_000_000m).ToString("0.########", CultureInfo.InvariantCulture);
        }

        private class GraphQlTransferNote
        {
            public string Address { get; set; }
            public long Amount { get; set; }
            public long AddressIndex { get; set; }
        }
        
        

        /// <summary>
        /// Starts a long-running GraphQL subscription loop for all accounts, publishing
        /// <see cref="ZcashEvent"/>s to <paramref name="eventAggregator"/> as BLOCK/TX events arrive.
        /// Reconnects with backoff on websocket errors. Completes when <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        public async Task StartSubscriptionLoopAsync(EventAggregator eventAggregator, ILogger logger, CancellationToken cancellationToken)
        {
            using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var subscriptions = new Dictionary<long, (CancellationTokenSource Cts, Task Task)>();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var accounts = await GetAccountsAsync(cancellationToken);
                        var ids = accounts.Select(a => a.AccountIndex).ToHashSet();
                        foreach (var id in subscriptions.Keys.Where(id => !ids.Contains(id)).ToList())
                        {
                            var old = subscriptions[id];
                            old.Cts.Cancel();
                            await old.Task;
                            old.Cts.Dispose();
                            subscriptions.Remove(id);
                        }
                        foreach (var id in ids.Where(id => !subscriptions.ContainsKey(id)))
                        {
                            var cts = CancellationTokenSource.CreateLinkedTokenSource(loopCts.Token);
                            subscriptions.Add(id, (cts, RunAccountSubscriptionAsync(id, id, eventAggregator, logger, cts.Token)));
                        }
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        logger?.LogWarning(ex, "[{CryptoCode}] Failed to refresh GraphQL event subscriptions", CryptoCode);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            finally
            {
                loopCts.Cancel();
                await Task.WhenAll(subscriptions.Values.Select(s => s.Task));
                foreach (var subscription in subscriptions.Values) subscription.Cts.Dispose();
            }
        }

        private async Task RunAccountSubscriptionAsync(long accountId, long accountIndex, EventAggregator eventAggregator, ILogger logger, CancellationToken cancellationToken)
        {
            int errorCount = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var request = new GraphQLRequest
                {
                    Query = @"subscription($idAccount: Int!) { events(idAccount: $idAccount) { type height txid } }",
                    Variables = new { idAccount = (int)accountId }
                };

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var subscriptionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                void WebSocketExceptionHandler(Exception ex)
                {
                    logger?.LogWarning(ex, "[{CryptoCode}] GraphQL subscription transport error for account {AccountIndex}", CryptoCode, accountIndex);
                    Interlocked.Increment(ref errorCount);
                    tcs.TrySetResult();
                    subscriptionCts.Cancel();
                }

                IDisposable subscription = null;
                try
                {
                    subscription = _graphQlClient.CreateSubscriptionStream(request, WebSocketExceptionHandler)
                        .Subscribe(
                            response =>
                            {
                                if (response.Errors?.Any() == true)
                                {
                                    Interlocked.Increment(ref errorCount);
                                    tcs.TrySetResult();
                                    return;
                                }
                                Interlocked.Exchange(ref errorCount, 0);
                                var evt = MapSubscriptionEvent(response, accountIndex);
                                if (evt != null)
                                {
                                    if (evt.TransactionHash != null)
                                        logger?.LogInformation("[{CryptoCode}] GraphQL subscription received transaction {TransactionId} for account {AccountIndex} at {ReceivedAt:O}",
                                            CryptoCode, evt.TransactionHash, accountIndex, DateTimeOffset.UtcNow);
                                    eventAggregator.Publish(evt);
                                }
                            },
                            _ =>
                            {
                                Interlocked.Increment(ref errorCount);
                                tcs.TrySetResult();
                            },
                            () => tcs.TrySetResult()
                        );
                    logger?.LogInformation("[{CryptoCode}] GraphQL subscription requested for account {AccountIndex} at {RequestedAt:O}",
                        CryptoCode, accountIndex, DateTimeOffset.UtcNow);

                    using var reg = subscriptionCts.Token.Register(() => tcs.TrySetResult());
                    await tcs.Task;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    logger?.LogWarning(ex, "[{CryptoCode}] GraphQL subscription error for account {AccountIndex}", CryptoCode, accountIndex);
                }
                finally
                {
                    subscription?.Dispose();
                }

                if (cancellationToken.IsCancellationRequested) return;

                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Max(1, Volatile.Read(ref errorCount)) * 5));
                try { await Task.Delay(delay, cancellationToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        private ZcashEvent MapSubscriptionEvent(GraphQLResponse<JObject> response, long accountIndex)
        {
            var evt = response.Data?["events"];
            if (evt == null) return null;

            var type = evt["type"]?.Value<string>();
            var height = evt["height"]?.Value<long>() ?? 0;
            var txid = evt["txid"]?.Value<string>();



            return type switch
            {
                "BLOCK" => new ZcashEvent { CryptoCode = CryptoCode, BlockHash = height.ToString(CultureInfo.InvariantCulture) },
                "TX" when !string.IsNullOrEmpty(txid) => new ZcashEvent { CryptoCode = CryptoCode, AccountIndex = accountIndex, TransactionHash = txid, FromSubscription = true },
                _ => null
            };
        }
    }
}
