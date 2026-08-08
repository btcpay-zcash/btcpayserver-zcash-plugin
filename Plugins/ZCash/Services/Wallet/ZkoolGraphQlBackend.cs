using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBTCPayServer.Plugins.ZCash.RPC;
using BTCPayServer.Plugins.ZCash.Configuration;
using GraphQL;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ZCash.Services
{
    public class ZkoolGraphQlBackend : IZcashWalletBackend
    {
        private readonly ZkoolGraphQlClient _graphQlClient;
        private readonly SemaphoreSlim _pollingLock = new SemaphoreSlim(1, 1);
        private long? _lastKnownHeight;
        private Dictionary<long, HashSet<string>> _knownUnconfirmedByAccount = new Dictionary<long, HashSet<string>>();

        public ZkoolGraphQlBackend(string cryptoCode, ZkoolGraphQlClient graphQlClient)
        {
            CryptoCode = cryptoCode;
            _graphQlClient = graphQlClient;
        }

        public string CryptoCode { get; }
        public WalletBackendType BackendType => WalletBackendType.ZkoolGraphQl;
        public bool UsesWalletFile => false;

        public async Task<WalletSyncStatus> GetSyncStatusAsync(CancellationToken cancellationToken = default)
        {
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

                await GetAccountsAsync(cancellationToken);
            }
            catch
            {
                result.WalletAvailable = false;
                result.DaemonAvailable = result.CurrentHeight > 0;
                result.Synced = false;
            }

            return result;
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
                AccountIndex = account["aindex"]?.Value<long>() ?? 0,
                Label = account["name"]?.Value<string>(),
                Height = account["height"]?.Value<long>() ?? 0,
                Balance = ParseDecimal(account["balance"]),
                UnlockedBalance = ParseDecimal(account["balance"])
            }).ToList() ?? new List<WalletAccount>();
        }

        public async Task<WalletAccountCreationResult> CreateAccountAsync(WalletAccountCreationRequest request, CancellationToken cancellationToken = default)
        {
            var accounts = await GetAccountsAsync(cancellationToken);
            var requestedAccountIndex = request.AccountIndex ?? (accounts.Any() ? accounts.Max(account => account.AccountIndex) + 1 : 0);

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
                    aindex = requestedAccountIndex,
                    birth = request.BirthHeight,
                    useInternal = request.UseInternalAddresses
                }
            }, cancellationToken);

            var createdId = data["createAccount"]?.Value<long>() ?? requestedAccountIndex;
            var account = await GetAccountAsync(createdId, cancellationToken);
            var address = await CreateOrGetAddressAsync(createdId, cancellationToken);
            return new WalletAccountCreationResult
            {
                AccountIndex = account?.AccountIndex ?? requestedAccountIndex,
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

        public Task<WalletFeeEstimate> GetFeeEstimateAsync(long accountIndex, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WalletFeeEstimate());
        }

        public async Task<IReadOnlyList<WalletTransfer>> GetTransfersAsync(long accountIndex, IReadOnlyList<long> addressIndices, CancellationToken cancellationToken = default)
        {
            var accountId = await ResolveGraphQlAccountIdAsync(accountIndex, cancellationToken);
            var currentHeight = await GetCurrentHeightAsync(cancellationToken);
            var data = await _graphQlClient.SendAsync(@"
query($idAccount: Int!) {
  transactionsByAccount(idAccount: $idAccount) {
    txid
    height
    notes {
      value
      address
      diversifierIndex
    }
  }
}", new { idAccount = accountId }, cancellationToken);

            return FlattenNotesAsTransfers(accountIndex, currentHeight, data["transactionsByAccount"], addressIndices);
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
                if (transaction != null)
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
                TransactionId = data["pay"]?.Value<string>()
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

                if (_lastKnownHeight is null)
                {
                    _lastKnownHeight = currentHeight;
                }
                else if (currentHeight > _lastKnownHeight.Value)
                {
                    // Polling unconfirmed transactions plus synthetic block events keeps the listener backend-agnostic
                    // without introducing a websocket subscription dependency for the first GraphQL implementation.
                    events.Add(new ZcashEvent
                    {
                        CryptoCode = CryptoCode,
                        BlockHash = currentHeight.ToString(CultureInfo.InvariantCulture)
                    });
                    _lastKnownHeight = currentHeight;
                }

                var nextKnown = new Dictionary<long, HashSet<string>>();
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

                    nextKnown[account.AccountIndex] = seen;
                    if (!_knownUnconfirmedByAccount.TryGetValue(account.AccountIndex, out var previous))
                    {
                        continue;
                    }

                    foreach (var txid in seen.Where(txid => !previous.Contains(txid)))
                    {
                        events.Add(new ZcashEvent
                        {
                            CryptoCode = CryptoCode,
                            AccountIndex = account.AccountIndex,
                            TransactionHash = txid
                        });
                    }
                }

                _knownUnconfirmedByAccount = nextKnown;
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

            var account = data["accounts"]?.FirstOrDefault(token => token["aindex"]?.Value<long>() == accountIndex);
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
    aindex
    height
    balance
  }
}", new { accountFilter = new { id = accountId } }, cancellationToken);

            var account = data["accounts"]?.FirstOrDefault();
            if (account == null)
            {
                return null;
            }

            return new WalletAccount
            {
                AccountIndex = account["aindex"]?.Value<long>() ?? 0,
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

            return MapAddress(data["newAddresses"]);
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
                // Payment prompts currently store the next address index using the walletd convention,
                // and the listener compensates with `AddressIndex - 1` when querying incoming transfers.
                AddressIndex = ToLong(token["diversifierIndex"]) + 1,
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

        private static IReadOnlyList<WalletTransfer> FlattenNotesAsTransfers(long accountIndex, long currentHeight, JToken transactions, IReadOnlyList<long> addressIndices)
        {
            var result = new List<WalletTransfer>();
            foreach (var transaction in transactions?.Children() ?? Enumerable.Empty<JToken>())
            {
                var flattened = FlattenTransactionNotes(accountIndex, currentHeight, transaction);
                if (addressIndices?.Count > 0)
                {
                    flattened = flattened.Where(transfer => addressIndices.Contains(transfer.AddressIndex)).ToList();
                }

                result.AddRange(flattened);
            }

            return result;
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
            try
            {
                var accounts = await GetAccountsAsync(cancellationToken);
                var tasks = accounts.Select(async account =>
                {
                    var accountId = await ResolveGraphQlAccountIdAsync(account.AccountIndex, cancellationToken);
                    await RunAccountSubscriptionAsync(accountId, account.AccountIndex, eventAggregator, logger, cancellationToken);
                }).ToList();
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger?.LogError(ex, "[{CryptoCode}] Failed to start GraphQL event subscriptions", CryptoCode);
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

                var tcs = new TaskCompletionSource();
                using var subscriptionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                void WebSocketExceptionHandler(Exception ex)
                {
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
                                var evt = MapSubscriptionEvent(response, accountIndex);
                                if (evt != null)
                                    eventAggregator.Publish(evt);
                            },
                            _ =>
                            {
                                Interlocked.Increment(ref errorCount);
                                tcs.TrySetResult();
                            },
                            () => tcs.TrySetResult()
                        );

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

        private ZcashEvent MapSubscriptionEvent(GraphQL.GraphQLResponse<JObject> response, long accountIndex)
        {
            var evt = response.Data?["events"];
            if (evt == null) return null;

            var type = evt["type"]?.Value<string>();
            var height = evt["height"]?.Value<long>() ?? 0;
            var txid = evt["txid"]?.Value<string>();

            return type switch
            {
                "BLOCK" => new ZcashEvent { CryptoCode = CryptoCode, BlockHash = height.ToString(CultureInfo.InvariantCulture) },
                "TX" when !string.IsNullOrEmpty(txid) => new ZcashEvent { CryptoCode = CryptoCode, AccountIndex = accountIndex, TransactionHash = txid },
                _ => null
            };
        }
    }
}
