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

        await CheatMode.MineBlock(new ICheckoutCheatModeExtension.MineBlockContext { BlockCount = 2 });
        var confirmed = await backend.GetTransactionAsync(accountId, payment.TransactionId);
        Assert.Equal(2, confirmed.Confirmations);
        Assert.Equal(address, Assert.Single(confirmed.Transfers).Address);
        Assert.Contains(await backend.PollEventsAsync(), e => !string.IsNullOrEmpty(e.BlockHash));
        var transfers = await backend.GetTransfersAsync(accountId, [index]);
        Assert.Equal(payment.TransactionId, Assert.Single(transfers).TransactionId);
        Assert.Empty(await backend.GetTransfersAsync(accountId, [index + 1]));
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
