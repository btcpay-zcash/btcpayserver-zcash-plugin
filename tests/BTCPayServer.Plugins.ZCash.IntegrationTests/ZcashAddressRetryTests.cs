using Microsoft.Playwright;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace BTCPayServer.Plugins.ZCash.IntegrationTests;

// zkool before 6.36 can fail newAddresses with "database is locked" while a
// synchronize for the same account is writing. One such failure used to drop
// ZEC-CHAIN from the invoice; the plugin now retries it a few times.
public class ZcashAddressRetryTests(ITestOutputHelper output)
{
    [LocalBrowserTheory]
    [InlineData(1)]
    [InlineData(3)]
    [Trait("Category", "Playwright")]
    public async Task InvoiceKeepsZcashWhenNewAddressesIsBrieflyLocked(int lockedCalls)
    {
        await using var server = await BrowserTestServer.StartAsync(forceMock: true);
        await using var browser = await OpenStoreAsync(server);
        var (page, storeId) = (browser.Page, browser.StoreId);

        var before = await server.GetMockNewAddressesCallsAsync();
        await server.SetMockNewAddressesLockedAsync(lockedCalls);
        var invoiceId = await CreateInvoiceAsync(page, storeId);
        Assert.NotNull(invoiceId);

        var destination = await ZecDestinationAsync(server, invoiceId!);
        output.WriteLine($"invoice {invoiceId}: ZEC-CHAIN destination {destination ?? "(none)"}");
        Assert.False(string.IsNullOrEmpty(destination));
        Assert.Equal(lockedCalls + 1, await server.GetMockNewAddressesCallsAsync() - before);
        await page.GotoAsync($"/i/{invoiceId}");
        await Expect(page.Locator("#test-payment-crypto-code")).ToHaveTextAsync("ZEC");

        var log = server.ReadLog();
        Assert.Equal(lockedCalls, CountOccurrences(log, "found the wallet database locked; retrying"));
        Assert.DoesNotContain("could not create the payment prompt", log);
    }

    [LocalBrowserFact]
    [Trait("Category", "Playwright")]
    public async Task PersistentLockGivesUpAndLogsTheDroppedPaymentMethod()
    {
        await using var server = await BrowserTestServer.StartAsync(forceMock: true);
        await using var browser = await OpenStoreAsync(server);
        var (page, storeId) = (browser.Page, browser.StoreId);

        var before = await server.GetMockNewAddressesCallsAsync();
        await server.SetMockNewAddressesLockedAsync(100);
        var invoiceId = await CreateInvoiceAsync(page, storeId);
        await server.SetMockNewAddressesLockedAsync(0);

        // A store with ZEC as its only payment method gets no invoice at all;
        // a store with other methods gets one without ZEC-CHAIN.
        if (invoiceId != null)
            Assert.True(string.IsNullOrEmpty(await ZecDestinationAsync(server, invoiceId)));
        // The first attempt plus one per retry delay, and no more.
        Assert.Equal(4, await server.GetMockNewAddressesCallsAsync() - before);
        var log = server.ReadLog();
        output.WriteLine(string.Join(Environment.NewLine, log.Split('\n').Where(l => l.Contains("database is locked") || l.Contains("payment prompt"))));
        Assert.Equal(3, CountOccurrences(log, "found the wallet database locked; retrying"));
        Assert.Contains("could not create the payment prompt for invoice", log);
    }

    private sealed class StoreSession : IAsyncDisposable
    {
        public required IPlaywright Playwright { get; init; }
        public required IBrowser Browser { get; init; }
        public required IPage Page { get; init; }
        public required string StoreId { get; init; }
        public async ValueTask DisposeAsync() { await Browser.DisposeAsync(); Playwright.Dispose(); }
    }

    private async Task<StoreSession> OpenStoreAsync(BrowserTestServer server)
    {
        var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        if (Environment.GetEnvironmentVariable("BTCPAY_PLAYWRIGHT_INSTALL") == "1")
            Assert.Equal(0, Microsoft.Playwright.Program.Main(["install", "chromium"]));
        var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            Channel = Environment.GetEnvironmentVariable("BTCPAY_PLAYWRIGHT_CHANNEL")
        });
        var context = await browser.NewContextAsync(new() { BaseURL = server.Url });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(30_000);
        await page.GotoAsync("/");
        await page.Locator("#Email").FillAsync($"playwright-{Guid.NewGuid():N}@example.com");
        await page.Locator("#Password").FillAsync("LocalTest123!");
        await page.Locator("#ConfirmPassword").FillAsync("LocalTest123!");
        await page.Locator("#RegisterButton").ClickAsync();
        await page.GotoAsync("/stores/create");
        await page.Locator("#Name").FillAsync("Address retry test");
        await page.Locator("#Create").ClickAsync();
        await page.Locator("#menu-item-General").ClickAsync();
        var storeId = await page.Locator("#Id").InputValueAsync();
        await page.GotoAsync($"/stores/{storeId}/Zcashlike/ZEC");
        await page.Locator("#WalletPassword").FillAsync(MockZkool.MockViewingKeys.First);
        await page.Locator("#BirthHeight").FillAsync("1");
        await page.Locator("button[value='add-account']").ClickAsync();
        await Expect(page.Locator(".alert-success")).ToContainTextAsync("created for this store");
        await page.Locator("#Enabled").CheckAsync();
        await page.Locator("#SaveButton").ClickAsync();
        await Expect(page.Locator("svg.text-success")).ToBeVisibleAsync();
        return new StoreSession { Playwright = playwright, Browser = browser, Page = page, StoreId = storeId };
    }

    // Returns the new invoice's id, or null when BTCPay refused to create it.
    private async Task<string?> CreateInvoiceAsync(IPage page, string storeId)
    {
        await page.GotoAsync($"/stores/{storeId}/invoices");
        await page.Locator("#page-primary").ClickAsync();
        await page.Locator("#Amount").FillAsync("0.1");
        await page.Locator("#Currency").FillAsync("ZEC");
        await page.Locator("#page-primary").ClickAsync();
        var link = page.Locator("a.invoice-checkout-link").First;
        var created = page.WaitForURLAsync(url => !url.Contains("/invoices/create"), new() { Timeout = 15_000 });
        try { await created; }
        catch (TimeoutException)
        {
            output.WriteLine("Invoice creation was refused: " + await page.Locator(".alert, .text-danger, .validation-summary-errors").AllInnerTextsAsync().ContinueWith(t => string.Join(" | ", t.Result)));
            return null;
        }
        var href = await link.GetAttributeAsync("href");
        return href?.Split('/').Last();
    }

    private static async Task<string?> ZecDestinationAsync(BrowserTestServer server, string invoiceId)
    {
        await using var db = new NpgsqlConnection(server.DatabaseConnectionString);
        await db.OpenAsync();
        await using var query = new NpgsqlCommand("""SELECT "Blob2"->'prompts'->'ZEC-CHAIN'->>'destination' FROM "Invoices" WHERE "Id" = @id""", db);
        query.Parameters.AddWithValue("id", invoiceId);
        return await query.ExecuteScalarAsync() as string;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
