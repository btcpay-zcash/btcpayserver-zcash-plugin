using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBTCPayServer.Plugins.ZCash.RPC;
using BTCPayServer.Plugins.ZCash.Configuration;

namespace BTCPayServer.Plugins.ZCash.Services
{
    public interface IZcashWalletBackend
    {
        string CryptoCode { get; }
        WalletBackend BackendType { get; }
        bool UsesWalletFile { get; }

        Task<WalletSyncStatus> GetSyncStatusAsync(CancellationToken cancellationToken = default);
        Task<long> SynchronizeAsync(IReadOnlyList<long> accountIndexes, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<WalletAccount>> GetAccountsAsync(CancellationToken cancellationToken = default);
        Task<WalletAccountCreationResult> CreateAccountAsync(WalletAccountCreationRequest request, CancellationToken cancellationToken = default);
        Task<WalletAddress> CreateAddressAsync(long accountIndex, string label, CancellationToken cancellationToken = default);
        Task<WalletBalance> GetBalanceAsync(long accountIndex, CancellationToken cancellationToken = default);
        Task<WalletFeeEstimate> GetFeeEstimateAsync(long accountIndex, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<WalletTransfer>> GetTransfersAsync(long accountIndex, IReadOnlyList<long> addressIndices, CancellationToken cancellationToken = default);
        Task<WalletTransaction> GetTransactionAsync(long accountIndex, string transactionId, CancellationToken cancellationToken = default);
        Task<WalletPreparedPayment> PreparePaymentAsync(long accountIndex, WalletPaymentRequest request, CancellationToken cancellationToken = default);
        Task<WalletSentPayment> SendPaymentAsync(long accountIndex, WalletPaymentRequest request, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<ZcashEvent>> PollEventsAsync(CancellationToken cancellationToken = default);
    }

    public class WalletSyncStatus
    {
        public bool Synced { get; set; }
        public long CurrentHeight { get; set; }
        public long WalletHeight { get; set; }
        public long TargetHeight { get; set; }
        public bool DaemonAvailable { get; set; }
        public bool WalletAvailable { get; set; }
    }

    public class WalletAccount
    {
        public long AccountIndex { get; set; }
        public string Label { get; set; }
        public string Address { get; set; }
        public decimal Balance { get; set; }
        public decimal UnlockedBalance { get; set; }
        public long Height { get; set; }
    }

    public class WalletAccountCreationRequest
    {
        public string Label { get; set; }
        public string Key { get; set; }
        public string Passphrase { get; set; }
        public long? BirthHeight { get; set; }
        public long? AccountIndex { get; set; }
        public bool UseInternalAddresses { get; set; }
    }

    public class WalletAccountCreationResult
    {
        public long AccountIndex { get; set; }
        public string Address { get; set; }
    }

    public class WalletAddress
    {
        public string Address { get; set; }
        public long AddressIndex { get; set; }
        public string UnifiedAddress { get; set; }
        public string TransparentAddress { get; set; }
        public string SaplingAddress { get; set; }
        public long? DiversifierIndex { get; set; }
        public string OrchardAddress { get; set; }
    }

    public class WalletBalance
    {
        public long Height { get; set; }
        public long Total { get; set; }
        public long Transparent { get; set; }
        public long Sapling { get; set; }
        public long Orchard { get; set; }
    }

    public class WalletFeeEstimate
    {
        public long FeePerKb { get; set; }
    }

    public class WalletTransfer
    {
        public string Address { get; set; }
        public long Amount { get; set; }
        public long Confirmations { get; set; }
        public long Height { get; set; }
        public long AccountIndex { get; set; }
        public long AddressIndex { get; set; }
        public string TransactionId { get; set; }
    }

    public class WalletTransaction
    {
        public string TransactionId { get; set; }
        public long Confirmations { get; set; }
        public long Height { get; set; }
        public IReadOnlyList<WalletTransfer> Transfers { get; set; }
    }

    public class WalletPreparedPayment
    {
        public string Payload { get; set; }
        public long Fee { get; set; }
    }

    public class WalletSentPayment
    {
        public string TransactionId { get; set; }
    }

    public class WalletPaymentRequest
    {
        public IReadOnlyList<WalletPaymentRecipient> Recipients { get; set; }
        public int? SourcePools { get; set; }
        public bool? RecipientPaysFee { get; set; }
        public int? Confirmations { get; set; }
    }

    public class WalletPaymentRecipient
    {
        public string Address { get; set; }
        public long Amount { get; set; }
        public string Memo { get; set; }
        public string AssetDescriptor { get; set; }
    }
}
