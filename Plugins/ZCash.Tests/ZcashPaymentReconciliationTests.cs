using System;
using BTCPayServer.Plugins.ZCash.RPC;
using BTCPayServer.Plugins.ZCash.Services;
using Xunit;

namespace BTCPayServer.Plugins.ZCash.Tests
{
    public class ZcashPaymentReconciliationTests
    {
        [Fact]
        public void TransferMatchesByWalletLocation()
        {
            var index = ZcashPaymentReconciliation.IndexLocations(new[]
            {
                (new WalletLocation(7, 3), "invoice-1")
            });
            var aggregation = ZcashPaymentReconciliation.AggregateTransfers(7, new[]
            {
                Transfer(7, 3, "AABB", 100, 2, 50)
            });
            var transfer = Assert.Single(aggregation.Transfers);

            Assert.Equal("invoice-1", Assert.Single(index[transfer.Location]));
            Assert.Equal("aabb", transfer.TransactionId);
            Assert.False(index.ContainsKey(new WalletLocation(1, 3)));
            Assert.False(index.ContainsKey(new WalletLocation(7, 2)));
        }

        [Fact]
        public void DuplicateClaimsByOneInvoiceAreNotAmbiguous()
        {
            var location = new WalletLocation(0, 3);
            var index = ZcashPaymentReconciliation.IndexLocations(new[]
            {
                (location, "invoice-1"),
                (location, "invoice-1")
            });

            Assert.Equal("invoice-1", Assert.Single(index[location]));
        }

        [Fact]
        public void DifferentInvoiceClaimsRemainAmbiguous()
        {
            var location = new WalletLocation(0, 3);
            var index = ZcashPaymentReconciliation.IndexLocations(new[]
            {
                (location, "invoice-1"),
                (location, "invoice-2")
            });

            Assert.Equal(2, index[location].Length);
        }

        [Fact]
        public void MultipleNotesForOneTransactionAndLocationAreAggregated()
        {
            var result = ZcashPaymentReconciliation.AggregateTransfers(0, new[]
            {
                Transfer(0, 3, "AABB", 100, 2, 50),
                Transfer(0, 3, "aabb", 200, 3, 50),
                Transfer(0, 4, "aabb", 400, 3, 50)
            });

            Assert.Equal(2, result.Transfers.Count);
            var combined = Assert.Single(result.Transfers, transfer => transfer.Location == new WalletLocation(0, 3));
            Assert.Equal(300, combined.Amount);
            Assert.Equal("aabb", combined.TransactionId);
            Assert.Equal(2, combined.Confirmations);
            Assert.Equal(50, combined.Height);
            Assert.Equal(0, result.InvalidTransferCount);
            Assert.Equal(0, result.InvalidGroupCount);
        }

        [Fact]
        public void InvalidAccountAndInconsistentOrOverflowingGroupsAreRejected()
        {
            var result = ZcashPaymentReconciliation.AggregateTransfers(0, new[]
            {
                Transfer(1, 3, "wrong-account", 1, 2, 50),
                Transfer(0, 3, "height-mismatch", 1, 2, 50),
                Transfer(0, 3, "height-mismatch", 1, 2, 51),
                Transfer(0, 4, "overflow", long.MaxValue, 2, 50),
                Transfer(0, 4, "overflow", 1, 2, 50)
            });

            Assert.Empty(result.Transfers);
            Assert.Equal(1, result.InvalidTransferCount);
            Assert.Equal(2, result.InvalidGroupCount);
        }

        [Theory]
        [InlineData(true, true, 50, 51, true)]
        [InlineData(false, true, 50, 51, false)]
        [InlineData(true, false, 50, 51, false)]
        [InlineData(true, true, 51, 51, false)]
        public void WalletCheckpointNotificationRequiresContinuousAvailability(
            bool previousAvailable,
            bool currentAvailable,
            long previousHeight,
            long currentHeight,
            bool expected)
        {
            var previous = Summary(previousAvailable, previousHeight);
            var current = Summary(currentAvailable, currentHeight);

            Assert.Equal(expected, ZcashRPCProvider.WalletCheckpointAdvanced(previous, current));
        }

        private static GetTransfersResponse.GetTransfersResponseItem Transfer(
            long account,
            long address,
            string txid,
            long amount,
            long confirmations,
            long height)
        {
            return new GetTransfersResponse.GetTransfersResponseItem
            {
                SubaddrIndex = new SubaddrIndex { Major = account, Minor = address },
                Txid = txid,
                Amount = amount,
                Confirmations = confirmations,
                Height = height,
                Address = "receiver-only-unified-address"
            };
        }

        private static ZcashRPCProvider.ZcashLikeSummary Summary(bool available, long walletHeight)
        {
            return new ZcashRPCProvider.ZcashLikeSummary
            {
                Synced = available,
                WalletAvailable = available,
                WalletHeight = walletHeight
            };
        }
    }
}
