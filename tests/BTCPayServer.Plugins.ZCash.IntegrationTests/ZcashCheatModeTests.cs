using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.ZCash.IntegrationTests;

public class ZcashCheatModeTests : ZcashIntegrationTestBase
{
    [Fact]
    public async Task CheatPaymentIsDetectedAndConfirmedAfterMining()
    {
        Assert.True(CheatMode.Handle(PaymentMethodId));
        Assert.False(CheatMode.Handle(PaymentTypes.CHAIN.GetPaymentMethodId("BTC")));
        var summary = await Provider.UpdateSummary("ZEC");
        Assert.True(summary.Synced);
        Assert.True(Provider.IsAvailable("ZEC"));
        var backend = Provider.WalletBackends["ZEC"];
        await backend.PollEventsAsync();
        // Receive payment before the new store account's first poll (regression coverage).
        var created = await GraphQl.SendAsync("mutation($newAccount: NewAccount!) { createAccount(newAccount: $newAccount) }",
            new { newAccount = new { name = "new-store", key = "mock-viewing-key" } });
        var accountId = created["createAccount"]!.Value<long>();
        var result = await GraphQl.SendAsync("mutation($idAccount: Int!) { newAddresses(idAccount: $idAccount) { ua diversifierIndex } }", new { idAccount = (int)accountId });
        var address = result["newAddresses"]!["ua"]!.Value<string>()!;
        var index = result["newAddresses"]!["diversifierIndex"]!.Value<long>();
        var payment = await CheatMode.PayInvoice(new ICheckoutCheatModeExtension.PayInvoiceContext(
            null!, 0.01234567m, null!, new PaymentPrompt { Destination = address }, null!));
        Assert.Equal(64, payment.TransactionId.Length);
        var pending = await backend.GetTransactionAsync(accountId, payment.TransactionId);
        Assert.Equal(0, pending.Confirmations);
        Assert.Equal(1234567, Assert.Single(pending.Transfers).Amount);
        Assert.Contains(await backend.PollEventsAsync(), e => e.TransactionHash == payment.TransactionId);
        // A lost notification / failed listener must be retried before mining.
        Assert.Contains(await backend.PollEventsAsync(), e => e.TransactionHash == payment.TransactionId);
        Assert.Equal(payment.TransactionId, Assert.Single(await backend.GetTransfersAsync(accountId, [index])).TransactionId);

        await CheatMode.MineBlock(new ICheckoutCheatModeExtension.MineBlockContext { BlockCount = 2 });
        var confirmed = await backend.GetTransactionAsync(accountId, payment.TransactionId);
        Assert.Equal(2, confirmed.Confirmations);
        Assert.Equal(address, Assert.Single(confirmed.Transfers).Address);
        Assert.Contains(await backend.PollEventsAsync(), e => !string.IsNullOrEmpty(e.BlockHash));
        var transfers = await backend.GetTransfersAsync(accountId, [index]);
        Assert.Equal(payment.TransactionId, Assert.Single(transfers).TransactionId);
        Assert.Empty(await backend.GetTransfersAsync(accountId, [index + 1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialPaymentsRemainDiscoverableAtOldAndNewAddresses(bool useOldAddress)
    {
        var backend = (Services.ZkoolGraphQlBackend)Provider.WalletBackends["ZEC"];
        var created = await GraphQl.SendAsync("mutation($newAccount: NewAccount!) { createAccount(newAccount: $newAccount) }",
            new { newAccount = new { name = "partial-store", key = "partial-key" } });
        var id = created["createAccount"]!.Value<long>();
        async Task<(string Address, long Index)> Address()
        {
            var result = await GraphQl.SendAsync("mutation($idAccount: Int!) { newAddresses(idAccount: $idAccount) { ua diversifierIndex } }", new { idAccount = id });
            return (result["newAddresses"]!["ua"]!.Value<string>()!, result["newAddresses"]!["diversifierIndex"]!.Value<long>());
        }
        async Task<string> Pay(string address, decimal amount)
        {
            var result = await CheatMode.PayInvoice(new ICheckoutCheatModeExtension.PayInvoiceContext(
                null!, amount, null!, new PaymentPrompt { Destination = address }, null!));
            return result.TransactionId;
        }
        var oldAddress = await Address();
        var firstTx = await Pay(oldAddress.Address, 0.04m);
        var newAddress = await Address();
        var secondTx = await Pay(useOldAddress ? oldAddress.Address : newAddress.Address, 0.07m);
        // Null confirmed response must fall back to the mempool.
        Assert.Equal(4_000_000, Assert.Single((await backend.GetTransactionAsync(id, firstTx)).Transfers).Amount);
        Assert.Equal(7_000_000, Assert.Single((await backend.GetTransactionAsync(id, secondTx)).Transfers).Amount);
        await CheatMode.MineBlock(new ICheckoutCheatModeExtension.MineBlockContext { BlockCount = 2 });
        var transfers = await backend.GetTransfersAsync(id, [oldAddress.Index, newAddress.Index]);
        Assert.Equal(11_000_000, transfers.Sum(t => t.Amount));
        Assert.Equal(2, transfers.Count);
        Assert.All(transfers, t => Assert.Equal(2, t.Confirmations));
        var history = await backend.GetTransfersByHeightAsync(id, transfers[0].Height, [oldAddress.Index]);
        Assert.Equal(useOldAddress ? 2 : 1, history.Count);
        Assert.All(history, t => Assert.Equal(oldAddress.Index, t.AddressIndex));
        Assert.Empty(await backend.GetTransfersByHeightAsync(id, transfers[0].Height + 1, [oldAddress.Index, newAddress.Index]));
    }

    [Fact]
    public async Task SubscriptionsDiscoverAccountsImportedAfterStartupAndStopOnCancellation()
    {
        var backend = (Services.ZkoolGraphQlBackend)Provider.WalletBackends["ZEC"];
        using var cts = new CancellationTokenSource();
        using var http = new HttpClient { BaseAddress = MockEndpoint };
        var loop = backend.StartSubscriptionLoopAsync(new BTCPayServer.EventAggregator(new BTCPayServer.Logging.Logs()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, cts.Token);
        async Task WaitForAccounts(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                var ids = Newtonsoft.Json.JsonConvert.DeserializeObject<int[]>(await http.GetStringAsync("/test/subscriptions"))!;
                if (ids.Distinct().Count() == count) return;
                await Task.Delay(100);
            }
            Assert.Fail($"Expected {count} subscribed accounts.");
        }
        try
        {
            await WaitForAccounts(1);
            await GraphQl.SendAsync("mutation($newAccount: NewAccount!) { createAccount(newAccount: $newAccount) }",
                new { newAccount = new { name = "late-store", key = "late-key" } });
            await WaitForAccounts(2);
        }
        finally
        {
            cts.Cancel();
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ExistingWalletKeyCannotBeImportedForAnotherStore()
    {
        await GraphQl.SendAsync("mutation($newAccount: NewAccount!) { createAccount(newAccount: $newAccount) }",
            new { newAccount = new { name = "store:original", key = "existing-key" } });
        var backend = Provider.WalletBackends["ZEC"];
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.CreateAccountAsync(new Services.WalletAccountCreationRequest
            { Label = "store:other", Key = "existing-key" }));
        var recovered = await backend.CreateAccountAsync(new Services.WalletAccountCreationRequest
            { Label = "store:original", Key = "existing-key" });
        Assert.Equal(2, recovered.AccountIndex);
        Assert.Equal(2, (await backend.GetAccountsAsync()).Count);
    }

    [Fact]
    public async Task RecoveryFindsSpentReceiptsAndGroupsMultipleOutputsWithoutDoubleCounting()
    {
        var created = await GraphQl.SendAsync("mutation($newAccount: NewAccount!) { createAccount(newAccount: $newAccount) }",
            new { newAccount = new { name = "recovery-store", key = "recovery-key" } });
        var id = created["createAccount"]!.Value<long>();
        var addresses = new List<(string Address, long Index)>();
        for (var i = 0; i < 2; i++)
        {
            var a = await GraphQl.SendAsync("mutation($idAccount: Int!) { newAddresses(idAccount: $idAccount) { ua diversifierIndex } }", new { idAccount = id });
            addresses.Add((a["newAddresses"]!["ua"]!.Value<string>()!, a["newAddresses"]!["diversifierIndex"]!.Value<long>()));
        }
        var paid = await GraphQl.SendAsync("mutation($idAccount: Int!, $payment: Payment!) { pay(idAccount: $idAccount, payment: $payment) }",
            new { idAccount = 1, payment = new { recipients = new[] {
                new { address = addresses[0].Address, amount = "0.03" },
                new { address = addresses[0].Address, amount = "0.03" },
                new { address = addresses[1].Address, amount = "0.04" }
            } } });
        var txid = paid["pay"]!.Value<string>()!;
        var backend = Provider.WalletBackends["ZEC"];
        var pending = await backend.GetTransactionAsync(id, txid);
        Assert.Equal(2, pending.Transfers.Count);
        Assert.Equal(10_000_000, pending.Transfers.Sum(t => t.Amount));
        await CheatMode.MineBlock(new ICheckoutCheatModeExtension.MineBlockContext { BlockCount = 2 });
        await GraphQl.SendAsync("mutation($idAccount: Int!) { mockSpendNotes(idAccount: $idAccount) }", new { idAccount = id });
        var unspent = await GraphQl.SendAsync("query($idAccount: Int!) { notesByAccount(idAccount: $idAccount) { value } }", new { idAccount = id });
        Assert.Empty(unspent["notesByAccount"]!);
        var restarted = new Services.ZkoolGraphQlBackend("ZEC", GraphQl, null!, null!);
        // Confirmed receipts are reconciled at startup, even with no new block or mempool event.
        Assert.Contains(await restarted.PollEventsAsync(), e => e.Reconcile && string.IsNullOrEmpty(e.BlockHash));
        var receipts = await restarted.GetTransfersAsync(id, addresses.Select(a => a.Index).ToArray());
        Assert.Equal(2, receipts.Count);
        Assert.Equal(10_000_000, receipts.Sum(t => t.Amount));
        Assert.All(receipts, t => Assert.Equal(txid, t.TransactionId));
        Assert.Contains(await restarted.PollEventsAsync(), e => e.Reconcile && string.IsNullOrEmpty(e.BlockHash));
    }

    [Theory]
    [InlineData("transaction rejected because its inputs are already spent")]
    [InlineData("")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void WalletRejectionCannotBeReportedAsSuccessfulPayment(string walletResult)
    {
        Assert.Throws<InvalidOperationException>(() => Services.ZkoolGraphQlClient.RequireTransactionId(JToken.FromObject(walletResult)));
    }

    [Fact]
    public async Task UnsupportedGraphQlOperationFailsExplicitly()
    {
        await Assert.ThrowsAsync<Services.ZkoolGraphQlClient.GraphQlApiException>(() => GraphQl.SendAsync("query { notImplemented }"));
    }

    [Fact]
    public async Task InvalidMiningRequestIsRejected()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => CheatMode.MineBlock(new ICheckoutCheatModeExtension.MineBlockContext { BlockCount = 0 }));
    }
}
