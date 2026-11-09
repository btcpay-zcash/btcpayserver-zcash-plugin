using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBTCPayServer.Plugins.ZCash.RPC;
using BTCPayServer.Plugins.ZCash.Configuration;
using BTCPayServer.Plugins.ZCash.RPC;

namespace BTCPayServer.Plugins.ZCash.Services
{
    public class ZcashWalletdBackend : IZcashWalletBackend
    {
        private readonly JsonRpcClient _daemonRpcClient;
        private readonly JsonRpcClient _walletRpcClient;

        public ZcashWalletdBackend(string cryptoCode, JsonRpcClient daemonRpcClient, JsonRpcClient walletRpcClient)
        {
            CryptoCode = cryptoCode;
            _daemonRpcClient = daemonRpcClient;
            _walletRpcClient = walletRpcClient;
        }

        public string CryptoCode { get; }
        public WalletBackend BackendType => WalletBackend.Walletd;
        public bool UsesWalletFile => true;

        public async Task<WalletSyncStatus> GetSyncStatusAsync(CancellationToken cancellationToken = default)
        {
            var result = new WalletSyncStatus();
            try
            {
              Console.WriteLine($"Start Walletd GetSyncStatusAsync");
                var daemonResult = await _daemonRpcClient.SendCommandAsync<JsonRpcClient.NoRequestModel, SyncInfoResponse>(
                    "sync_info",
                    JsonRpcClient.NoRequestModel.Instance,
                    cancellationToken);
                result.TargetHeight = daemonResult.TargetHeight.GetValueOrDefault(daemonResult.Height);
                result.CurrentHeight = daemonResult.Height;
                result.Synced = daemonResult.Height >= result.TargetHeight && result.CurrentHeight > 0;
                result.DaemonAvailable = true;
            }
            catch
            {
                result.DaemonAvailable = false;
            }

            try
            {
                var walletResult = await _walletRpcClient.SendCommandAsync<JsonRpcClient.NoRequestModel, GetHeightResponse>(
                    "get_height",
                    JsonRpcClient.NoRequestModel.Instance,
                    cancellationToken);
                result.WalletHeight = walletResult.Height;
                result.WalletAvailable = true;
            }
            catch
            {
                result.WalletAvailable = false;
            }

            return result;
        }

        public async Task<long> SynchronizeAsync(IReadOnlyList<long> accountIndexes,
            CancellationToken cancellationToken = default)
        {
            return 0;
        }

        public async Task<IReadOnlyList<WalletAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
        {
            var response = await _walletRpcClient.SendCommandAsync<GetAccountsRequest, GetAccountsResponse>(
                "get_accounts",
                new GetAccountsRequest(),
                cancellationToken);

            return response?.SubaddressAccounts?.Select(account => new WalletAccount
            {
                AccountIndex = account.AccountIndex,
                Label = account.Label,
                Address = account.BaseAddress,
                Balance = account.Balance,
                UnlockedBalance = account.UnlockedBalance
            }).ToList() ?? new List<WalletAccount>();
        }

        public async Task<WalletAccountCreationResult> CreateAccountAsync(WalletAccountCreationRequest request, CancellationToken cancellationToken = default)
        {
            var response = await _walletRpcClient.SendCommandAsync<CreateAccountRequest, CreateAccountResponse>(
                "create_account",
                new CreateAccountRequest
                {
                    Key = request.Key,
                    Height = request.BirthHeight,
                    Label = request.Label
                },
                cancellationToken);

            return new WalletAccountCreationResult
            {
                AccountIndex = response.AccountIndex,
                Address = response.Address
            };
        }

        public async Task<WalletAddress> CreateAddressAsync(long accountIndex, string label, CancellationToken cancellationToken = default)
        {
            var response = await _walletRpcClient.SendCommandAsync<CreateAddressRequest, CreateAddressResponse>(
                "create_address",
                new CreateAddressRequest
                {
                    AccountIndex = accountIndex,
                    Label = label
                },
                cancellationToken);

            return new WalletAddress
            {
                Address = response.Address,
                AddressIndex = response.AddressIndex,
                UnifiedAddress = response.Address
            };
        }

        public async Task<WalletBalance> GetBalanceAsync(long accountIndex, CancellationToken cancellationToken = default)
        {
            var account = (await GetAccountsAsync(cancellationToken)).FirstOrDefault(a => a.AccountIndex == accountIndex);
            return new WalletBalance
            {
                Total = ToAtomicUnits(account?.Balance ?? 0m)
            };
        }

        public async Task<WalletFeeEstimate> GetFeeEstimateAsync(long accountIndex, CancellationToken cancellationToken = default)
        {
            var response = await _daemonRpcClient.SendCommandAsync<GetFeeEstimateRequest, GetFeeEstimateResponse>(
                "get_fee_estimate",
                new GetFeeEstimateRequest(),
                cancellationToken);

            return new WalletFeeEstimate
            {
                FeePerKb = response.Fee
            };
        }

        public async Task<IReadOnlyList<WalletTransfer>> GetTransfersAsync(long accountIndex, IReadOnlyList<long> addressIndices, CancellationToken cancellationToken = default)
        {
            var response = await _walletRpcClient.SendCommandAsync<GetTransfersRequest, GetTransfersResponse>(
                "get_transfers",
                new GetTransfersRequest
                {
                    AccountIndex = accountIndex,
                    In = true,
                    SubaddrIndices = addressIndices?.Distinct().ToList()
                },
                cancellationToken);

            return response?.In?.Select(MapTransfer).ToList() ?? new List<WalletTransfer>();
        }

        public async Task<WalletTransaction> GetTransactionAsync(long accountIndex, string transactionId, CancellationToken cancellationToken = default)
        {
            var response = await _walletRpcClient.SendCommandAsync<GetTransferByTransactionIdRequest, GetTransferByTransactionIdResponse>(
                "get_transfer_by_txid",
                new GetTransferByTransactionIdRequest
                {
                    TransactionId = transactionId,
                    AccountIndex = accountIndex
                },
                cancellationToken);

            return new WalletTransaction
            {
                TransactionId = response.Transfer.Txid,
                Confirmations = response.Transfer.Confirmations,
                Height = response.Transfer.Height,
                Transfers = response.Transfers?.Select(transfer => new WalletTransfer
                {
                    Address = transfer.Address,
                    Amount = transfer.Amount,
                    Confirmations = transfer.Confirmations,
                    Height = transfer.Height,
                    AccountIndex = transfer.SubaddrIndex.Major,
                    AddressIndex = transfer.SubaddrIndex.Minor,
                    TransactionId = transfer.Txid
                }).ToList() ?? new List<WalletTransfer>()
            };
        }

        public Task<WalletPreparedPayment> PreparePaymentAsync(long accountIndex, WalletPaymentRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Payment preparation is not implemented for the zcash-walletd backend.");
        }

        public Task<WalletSentPayment> SendPaymentAsync(long accountIndex, WalletPaymentRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Sending payments is not implemented for the zcash-walletd backend.");
        }

        public Task<IReadOnlyList<ZcashEvent>> PollEventsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ZcashEvent>>(Array.Empty<ZcashEvent>());
        }

        private static WalletTransfer MapTransfer(GetTransfersResponse.GetTransfersResponseItem transfer)
        {
            return new WalletTransfer
            {
                Address = transfer.Address,
                Amount = transfer.Amount,
                Confirmations = transfer.Confirmations,
                Height = transfer.Height,
                AccountIndex = transfer.SubaddrIndex.Major,
                AddressIndex = transfer.SubaddrIndex.Minor,
                TransactionId = transfer.Txid
            };
        }

        private static long ToAtomicUnits(decimal value)
        {
            return checked((long) decimal.Round(value * 100_000_000m, MidpointRounding.AwayFromZero));
        }
    }
}
