using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Plugins.ZCash.RPC;

namespace BTCPayServer.Plugins.ZCash.Services
{
    internal readonly record struct WalletLocation(long AccountIndex, long AddressIndex);

    internal readonly record struct AggregatedWalletTransfer(
        WalletLocation Location,
        string TransactionId,
        long Amount,
        long Confirmations,
        long Height);

    internal sealed record WalletTransferAggregationResult(
        IReadOnlyList<AggregatedWalletTransfer> Transfers,
        int InvalidTransferCount,
        int InvalidGroupCount);

    internal static class ZcashPaymentReconciliation
    {
        internal static IReadOnlyDictionary<WalletLocation, string[]> IndexLocations(
            IEnumerable<(WalletLocation Location, string InvoiceId)> claims)
        {
            ArgumentNullException.ThrowIfNull(claims);

            return claims
                .GroupBy(claim => claim.Location)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(claim => claim.InvoiceId)
                        .Distinct(StringComparer.Ordinal)
                        .Take(2)
                        .ToArray());
        }

        internal static WalletTransferAggregationResult AggregateTransfers(
            long requestedAccountIndex,
            IEnumerable<GetTransfersResponse.GetTransfersResponseItem> transfers)
        {
            ArgumentNullException.ThrowIfNull(transfers);

            var validTransfers = new List<(GetTransfersResponse.GetTransfersResponseItem Transfer, string TransactionId)>();
            var invalidTransferCount = 0;

            foreach (var transfer in transfers)
            {
                if (transfer?.SubaddrIndex is null ||
                    transfer.SubaddrIndex.Major != requestedAccountIndex ||
                    transfer.SubaddrIndex.Major < 0 ||
                    transfer.SubaddrIndex.Minor < 0 ||
                    string.IsNullOrWhiteSpace(transfer.Txid) ||
                    transfer.Amount <= 0 ||
                    transfer.Confirmations < 0 ||
                    transfer.Height < 0)
                {
                    invalidTransferCount++;
                    continue;
                }

                validTransfers.Add((transfer, transfer.Txid.ToLowerInvariant()));
            }

            var aggregatedTransfers = new List<AggregatedWalletTransfer>();
            var invalidGroupCount = 0;

            foreach (var group in validTransfers.GroupBy(transfer => (
                         Location: new WalletLocation(
                             transfer.Transfer.SubaddrIndex.Major,
                             transfer.Transfer.SubaddrIndex.Minor),
                         transfer.TransactionId)))
            {
                var heights = group.Select(transfer => transfer.Transfer.Height).Distinct().Take(2).ToArray();
                if (heights.Length != 1)
                {
                    invalidGroupCount++;
                    continue;
                }

                long amount;
                try
                {
                    amount = group.Sum(transfer => transfer.Transfer.Amount);
                }
                catch (OverflowException)
                {
                    invalidGroupCount++;
                    continue;
                }

                aggregatedTransfers.Add(new AggregatedWalletTransfer(
                    group.Key.Location,
                    group.Key.TransactionId,
                    amount,
                    group.Min(transfer => transfer.Transfer.Confirmations),
                    heights[0]));
            }

            return new WalletTransferAggregationResult(
                aggregatedTransfers,
                invalidTransferCount,
                invalidGroupCount);
        }
    }
}
