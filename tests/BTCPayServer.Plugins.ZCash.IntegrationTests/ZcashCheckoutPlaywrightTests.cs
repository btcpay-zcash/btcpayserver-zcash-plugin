using Newtonsoft.Json.Linq;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace BTCPayServer.Plugins.ZCash.IntegrationTests;

public sealed class LocalBrowserFactAttribute : FactAttribute
{
    public LocalBrowserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BTCPAY_RUN_PLAYWRIGHT") != "1")
            Skip = "Run scripts/test-playwright.sh to enable the PostgreSQL-backed browser test.";
    }
}

public sealed class LocalBrowserTheoryAttribute : TheoryAttribute
{
    public LocalBrowserTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("BTCPAY_RUN_PLAYWRIGHT") != "1")
            Skip = "Run scripts/test-playwright.sh to enable the PostgreSQL-backed browser test.";
    }
}

public class ZcashCheckoutPlaywrightTests(ITestOutputHelper output)
{
    [LocalBrowserTheory]
    [InlineData("exact")]
    [InlineData("overpaid")]
    [InlineData("partial-new")]
    [InlineData("partial-old")]
    [Trait("Category", "Playwright")]
    public async Task StoreInvoiceIsPaidAndSettledThroughCheatMode(string scenario)
    {
        await using var server = await BrowserTestServer.StartAsync();
        using var playwright = await Playwright.CreateAsync();
        if (Environment.GetEnvironmentVariable("BTCPAY_PLAYWRIGHT_INSTALL") == "1")
            Assert.Equal(0, Microsoft.Playwright.Program.Main(["install", "chromium"]));
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            Channel = Environment.GetEnvironmentVariable("BTCPAY_PLAYWRIGHT_CHANNEL")
        });
        await using var context = await browser.NewContextAsync(new() { BaseURL = server.Url });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(30_000);
        try
        {
            await page.GotoAsync("/");
            await page.Locator("#Email").FillAsync($"playwright-{Guid.NewGuid():N}@example.com");
            await page.Locator("#Password").FillAsync("LocalTest123!");
            await page.Locator("#ConfirmPassword").FillAsync("LocalTest123!");
            await page.Locator("#RegisterButton").ClickAsync();

            await page.GotoAsync("/stores/create");
            await page.Locator("#Name").FillAsync("Zcash browser test");
            await page.Locator("#Create").ClickAsync();
            await page.Locator("#menu-item-General").ClickAsync();
            var storeId = await page.Locator("#Id").InputValueAsync();
            Assert.False(string.IsNullOrWhiteSpace(storeId));
            output.WriteLine($"Created store {storeId}");

            await page.GotoAsync($"/stores/{storeId}/Zcashlike/ZEC");
            output.WriteLine(server.UsesRegtest ? "Wallet: local regtest" : "Wallet: mock");
            var viewingKey = "mock-viewing-key";
            if (server.UsesRegtest)
            {
                using var client = new Services.ZkoolGraphQlClient(new Uri(server.CashcowUrl), new HttpClient());
                var account = await client.SendAsync("mutation($newAccount: NewAccount!) { createAccount(newAccount: $newAccount) }",
                    new { newAccount = new { name = $"browser-receiver-{Guid.NewGuid():N}", key = "", aindex = 0, birth = 1, useInternal = false } });
                var keys = await client.SendAsync("query($accountFilter: AccountFilter) { accounts(accountFilter: $accountFilter) { ufvk } }",
                    new { accountFilter = new { id = account["createAccount"]!.Value<int>() } });
                viewingKey = keys["accounts"]![0]!["ufvk"]!.Value<string>()!;
            }
            await page.Locator("#WalletPassword").FillAsync(viewingKey);
            await page.Locator("#BirthHeight").FillAsync("1");
            await page.Locator("button[value='add-account']").ClickAsync();
            await Expect(page.Locator(".alert-success")).ToContainTextAsync("created for this store");
            await page.Locator("#Enabled").CheckAsync();
            // Require two confirmations so settlement cannot pass at zero confirmations.
            await page.Locator("#SettlementConfirmationThresholdChoice").SelectOptionAsync("4");
            await page.Locator("#CustomSettlementConfirmationThreshold").FillAsync("2");
            await page.Locator("#SaveButton").ClickAsync();
            await Expect(page.Locator("svg.text-success")).ToBeVisibleAsync();
            await page.GotoAsync($"/stores/{storeId}/Zcashlike/ZEC");
            await Expect(page.Locator("#Enabled")).ToBeCheckedAsync();
            await Expect(page.Locator("#CustomSettlementConfirmationThreshold")).ToHaveValueAsync("2");

            if (scenario == "exact")
            {
                await page.GotoAsync("/stores/create");
                await page.Locator("#Name").FillAsync("Duplicate viewing key test");
                await page.Locator("#Create").ClickAsync();
                await page.Locator("#menu-item-General").ClickAsync();
                var secondStore = await page.Locator("#Id").InputValueAsync();
                await page.GotoAsync($"/stores/{secondStore}/Zcashlike/ZEC");
                await page.Locator("button[value='add-account']").ClickAsync();
                await Expect(page.GetByText("A viewing key is required.", new() { Exact = true }).First).ToBeVisibleAsync();
                await page.Locator("#WalletPassword").FillAsync(viewingKey);
                await page.Locator("button[value='add-account']").ClickAsync();
                await Expect(page.GetByText("This viewing key is already assigned to a store. Use a different viewing key.", new() { Exact = true }).First).ToBeVisibleAsync();
            }

            await page.GotoAsync($"/stores/{storeId}/invoices");
            await page.Locator("#page-primary").ClickAsync();
            await page.Locator("#Amount").FillAsync("0.1");
            // Denominate in ZEC to avoid exchange/network rate dependencies.
            await page.Locator("#Currency").FillAsync("ZEC");
            await page.Locator("#page-primary").ClickAsync();
            var checkoutLink = page.Locator("a.invoice-checkout-link").First;
            var href = await checkoutLink.GetAttributeAsync("href");
            Assert.NotNull(href);
            var invoiceId = href!.Split('/').Last();
            output.WriteLine($"Created invoice {invoiceId}");
            await page.GotoAsync(href);
            await Expect(page.Locator("#test-payment-crypto-code")).ToHaveTextAsync("ZEC");
            // Clear pending cashcow inputs left by earlier cases/runs on the shared regtest chain.
            if (server.UsesRegtest)
            {
                await page.Locator("#BlockCount").FillAsync("1");
                await SubmitCheatFormAsync(page, "form#mine-block button", "/mine-blocks");
            }
            var oldAddress = await page.Locator("#Address_ZEC-CHAIN [data-clipboard]").GetAttributeAsync("data-clipboard");
            if (scenario.StartsWith("partial")) await page.Locator("#test-payment-amount").FillAsync("0.04");
            if (scenario == "overpaid") await page.Locator("#test-payment-amount").FillAsync("0.12");
            await SubmitCheatFormAsync(page, "#FakePayment", "/test-payment");
            await Expect(page.Locator("#CheatSuccessMessage")).ToBeVisibleAsync();
            await Expect(page.Locator("#CheatErrorMessage")).ToHaveCountAsync(0);

            // Read the persisted merchant view in a second tab while checkout stays open.
            var merchant = await context.NewPageAsync();
            if (scenario.StartsWith("partial"))
            {
                await Expect(page.Locator("#Address_ZEC-CHAIN [data-clipboard]")).Not.ToHaveAttributeAsync("data-clipboard", oldAddress!, new() { Timeout = 60_000 });
                await AssertInvoiceStatusAsync(merchant, storeId, invoiceId, "New (paid partial)");
                // The real cashcow has one funded note; confirm its first spend before reusing change.
                if (server.UsesRegtest)
                {
                    await page.Locator("#BlockCount").FillAsync("1");
                    await SubmitCheatFormAsync(page, "form#mine-block button", "/mine-blocks");
                    await AssertConfirmationsAsync(merchant, invoiceId, "1 / 2");
                }
                var paymentLink = await page.Locator("#PayInWallet").GetAttributeAsync("href");
                var remainingAmount = paymentLink!.Split("amount=")[1].Split('&')[0];
                if (scenario == "partial-old")
                {
                    using var client = new Services.ZkoolGraphQlClient(new Uri(server.CashcowUrl), new HttpClient());
                    await client.SendAsync("mutation($idAccount: Int!, $payment: Payment!) { pay(idAccount: $idAccount, payment: $payment) }",
                        new { idAccount = 1, payment = new { recipients = new[] { new { address = oldAddress!.Trim(), amount = remainingAmount } } } });
                }
                else
                {
                    await page.Locator("#test-payment-amount").FillAsync(remainingAmount);
                    await SubmitCheatFormAsync(page, "#FakePayment", "/test-payment");
                }
            }
            await AssertInvoiceStatusAsync(merchant, storeId, invoiceId, scenario == "overpaid" ? "Processing (paid over)" : "Processing");
            await page.Locator("#BlockCount").FillAsync("1");
            await SubmitCheatFormAsync(page, "form#mine-block button", "/mine-blocks");
            await Expect(page.Locator("#CheatSuccessMessage")).ToBeVisibleAsync();
            // Wait for the persisted confirmation count, not just the mining acknowledgement.
            await AssertConfirmationsAsync(merchant, invoiceId, "1 / 2");
            // One block must remain insufficient for the configured two confirmations.
            await AssertInvoiceStatusAsync(merchant, storeId, invoiceId, scenario == "overpaid" ? "Processing (paid over)" : "Processing");
            await page.Locator("#BlockCount").FillAsync("1");
            await SubmitCheatFormAsync(page, "form#mine-block button", "/mine-blocks");
            await AssertInvoiceStatusAsync(merchant, storeId, invoiceId, scenario == "overpaid" ? "Settled (paid over)" : "Settled");
            await Expect(page.Locator("xpath=//*[text()=\"Invoice Paid\" or text()=\"Payment Received\"]")).ToBeVisibleAsync(new() { Timeout = 60_000 });
            await context.Tracing.StopAsync();
        }
        catch
        {
            Directory.CreateDirectory(server.Artifacts);
            await page.ScreenshotAsync(new() { Path = Path.Combine(server.Artifacts, "failure.png"), FullPage = true });
            await context.Tracing.StopAsync(new() { Path = Path.Combine(server.Artifacts, "trace.zip") });
            output.WriteLine($"Browser artifacts and server log: {server.Artifacts}");
            output.WriteLine(server.ReadLog());
            throw;
        }
    }

    private static async Task SubmitCheatFormAsync(IPage page, string selector, string action)
    {
        var response = await page.RunAndWaitForResponseAsync(
            () => page.Locator(selector).ClickAsync(),
            r => r.Request.Method == "POST" && new Uri(r.Url).AbsolutePath.EndsWith(action));
        Assert.True(response.Ok, await response.TextAsync());
        await Expect(page.Locator("#CheatErrorMessage")).ToHaveCountAsync(0);
    }

    private static async Task AssertConfirmationsAsync(IPage page, string invoiceId, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        do
        {
            await page.GotoAsync($"/invoices/{invoiceId}");
            if (await page.GetByText(expected, new() { Exact = true }).CountAsync() > 0) return;
            await Task.Delay(500);
        } while (DateTime.UtcNow < deadline);
        Assert.Fail($"Invoice {invoiceId} did not show {expected} confirmations.");
    }

    private static async Task AssertInvoiceStatusAsync(IPage page, string storeId, string invoiceId, string status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        string last = "";
        do
        {
            await page.GotoAsync($"/stores/{storeId}/invoices");
            var row = page.Locator($"#invoice_{invoiceId}");
            last = await row.InnerTextAsync();
            if (await row.GetByText(status, new() { Exact = true }).CountAsync() > 0) return;
            await Task.Delay(500);
        } while (DateTime.UtcNow < deadline);
        Assert.Fail($"Invoice {invoiceId} did not reach {status}. Last merchant row: {last}");
    }
}
