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

public sealed class LocalRegtestBrowserFactAttribute : FactAttribute
{
    public LocalRegtestBrowserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BTCPAY_RUN_PLAYWRIGHT") != "1" ||
            Environment.GetEnvironmentVariable("BTCPAY_TEST_WALLET") != "regtest")
            Skip = "Set BTCPAY_TEST_WALLET=regtest and run scripts/test-playwright.sh for real zkool timing.";
    }
}

public class ZcashCheckoutPlaywrightTests(ITestOutputHelper output)
{
    [LocalRegtestBrowserFact]
    [Trait("Category", "Playwright")]
    [Trait("Wallet", "Regtest")]
    public Task RealZkoolSubscriptionPersistsPaymentWithinTwoSeconds() =>
        RunCheckoutAsync("subscription-live");

    [LocalRegtestBrowserFact]
    [Trait("Category", "Playwright")]
    [Trait("Wallet", "Regtest")]
    public Task RealZkoolSubscriptionStillReceivesPaymentsAfterIdleConnection() =>
        RunCheckoutAsync("subscription-live-idle");

    [LocalBrowserTheory]
    [InlineData("exact")]
    [InlineData("overpaid")]
    [InlineData("partial-new")]
    [InlineData("partial-old")]
    [Trait("Category", "Playwright")]
    [Trait("Wallet", "Regtest")]
    public Task StoreInvoiceIsPaidAndSettledThroughCheatMode(string scenario) => RunCheckoutAsync(scenario);

    [LocalBrowserTheory]
    [InlineData("offline-expired")]
    [InlineData("subscription-latency")]
    [Trait("Category", "Playwright")]
    public Task MockWalletRecoveryAndSubscription(string scenario) => RunCheckoutAsync(scenario, forceMock: true);

    private async Task RunCheckoutAsync(string scenario, bool forceMock = false)
    {
        await using var server = await BrowserTestServer.StartAsync(forceMock: forceMock);
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
            var legacyNotice = page.GetByText("This host has a legacy Zcash wallet.", new() { Exact = false });
            if (scenario == "exact")
            {
                var directory = Directory.CreateDirectory(Path.Combine(server.Artifacts, "legacy-wallet"));
                await Expect(legacyNotice).ToHaveCountAsync(0);
                var earlierFilename = Path.Combine(directory.FullName, "zec-wallet.db");
                File.WriteAllText(earlierFilename, "");
                await page.ReloadAsync();
                await Expect(legacyNotice).ToBeVisibleAsync();
                File.Move(earlierFilename, Path.Combine(directory.FullName, "zec-wallet2.db"));
                await page.ReloadAsync();
                await Expect(legacyNotice).ToBeVisibleAsync();
                await page.Locator("button[value='add-account']").ClickAsync();
                await Expect(page.GetByText("A viewing key is required.", new() { Exact = true }).First).ToBeVisibleAsync();
                await Expect(legacyNotice).ToBeVisibleAsync();
            }
            output.WriteLine(server.UsesRegtest ? "Wallet: local regtest" : "Wallet: mock");
            var viewingKey = await NewViewingKeyAsync(server);
            await page.Locator("#WalletPassword").FillAsync(viewingKey);
            await page.Locator("#BirthHeight").FillAsync("1");
            await page.Locator("button[value='add-account']").ClickAsync();
            await Expect(page.Locator(".alert-success")).ToContainTextAsync("created for this store");
            await Expect(legacyNotice).ToHaveCountAsync(0);
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
                await server.RestartDetectionAsync();
                Assert.True(File.Exists(Path.Combine(server.Artifacts, "legacy-wallet", "zec-wallet2.db")));
                await page.GotoAsync("/stores/create");
                await page.Locator("#Name").FillAsync("Duplicate viewing key test");
                await page.Locator("#Create").ClickAsync();
                await page.Locator("#menu-item-General").ClickAsync();
                var secondStore = await page.Locator("#Id").InputValueAsync();
                await page.GotoAsync($"/stores/{secondStore}/Zcashlike/ZEC");
                await Expect(page.Locator("#WalletPassword")).ToBeVisibleAsync();
                await Expect(legacyNotice).ToHaveCountAsync(0);
                await page.Locator("button[value='add-account']").ClickAsync();
                await Expect(page.GetByText("A viewing key is required.", new() { Exact = true }).First).ToBeVisibleAsync();
                await page.Locator("#WalletPassword").FillAsync(viewingKey);
                await page.Locator("button[value='add-account']").ClickAsync();
                await Expect(page.GetByText("This viewing key is already assigned to a store. Use a different viewing key.", new() { Exact = true }).First).ToBeVisibleAsync();
            }

            if (scenario == "subscription-latency")
            {
                // The fresh mock wallet starts receiving at (2, 1). Preserve an older
                // wallet's row with those indices to exercise address-based matching.
                await using var db = new Npgsql.NpgsqlConnection(server.DatabaseConnectionString);
                await db.OpenAsync();
                await using var seed = new Npgsql.NpgsqlCommand("""
                    INSERT INTO "BTCPayServer.Plugins.ZCash"."Receivers"
                        ("AccountIndex", "AddressIndex", "UnifiedAddress", "CreatedAt")
                    VALUES (2, 1, 'retired-wallet-address', now())
                    """, db);
                await seed.ExecuteNonQueryAsync();
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
            if (scenario.StartsWith("subscription-"))
            {
                await VerifySubscriptionLatencyAsync(server, page, storeId, invoiceId, oldAddress!,
                    idleSeconds: scenario.EndsWith("-idle") ? 40 : 0);
                await context.Tracing.StopAsync();
                return;
            }
            if (scenario == "offline-expired")
            {
                await VerifyExpiredInvoiceRecoveryAsync(server, page, storeId, invoiceId, oldAddress!);
                await context.Tracing.StopAsync();
                return;
            }
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
            if (scenario == "exact" && !server.UsesRegtest)
            {
                using var wallet = new Services.ZkoolGraphQlClient(new Uri(server.CashcowUrl), new HttpClient());
                await wallet.SendAsync("mutation { mockDisconnectTip }");
                await AssertConfirmationsAsync(merchant, invoiceId, "1 / 2");
                await using var db = new Npgsql.NpgsqlConnection(server.DatabaseConnectionString);
                await db.OpenAsync();
                await using var status = new Npgsql.NpgsqlCommand(
                    """SELECT "Status" FROM "Payments" WHERE "InvoiceDataId" = @invoice""", db);
                status.Parameters.AddWithValue("invoice", invoiceId);
                Assert.Equal("Processing", await status.ExecuteScalarAsync());
            }
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

    private static async Task<string> NewViewingKeyAsync(BrowserTestServer server, bool secondOwner = false)
    {
        if (!server.UsesRegtest) return secondOwner ? MockZkool.MockViewingKeys.Second : MockZkool.MockViewingKeys.First;
        using var client = new Services.ZkoolGraphQlClient(new Uri(server.CashcowUrl), new HttpClient());
        var account = await client.SendAsync("mutation($newAccount: NewAccount!) { createAccount(newAccount: $newAccount) }",
            new { newAccount = new { name = $"browser-receiver-{Guid.NewGuid():N}", key = "", aindex = 0, birth = 1, useInternal = false } });
        var key = await client.SendAsync("query($id: Int!) { unifiedFullViewingKey(idAccount: $id) }",
            new { id = account["createAccount"]!.Value<int>() });
        return key["unifiedFullViewingKey"]!.Value<string>()!;
    }

    [LocalBrowserFact]
    [Trait("Category", "Playwright")]
    [Trait("Wallet", "Regtest")]
    public async Task TwoMerchantOwnersSettleInvoicesInSeparateWallets()
    {
        await using var server = await BrowserTestServer.StartAsync();
        if (!server.UsesRegtest)
        {
            // This legacy account has no exportable full viewing key. Both stores
            // must still be able to import keys, issue invoices and receive payments.
            using var wallet = new Services.ZkoolGraphQlClient(new Uri(server.CashcowUrl), new HttpClient());
            await wallet.SendAsync("mutation($a: NewAccount!) { createAccount(newAccount: $a) }",
                new { a = new { name = "store:retired", aindex = 0 } });
        }
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        async Task<IPage> RegisterAsync()
        {
            var context = await browser.NewContextAsync(new() { BaseURL = server.Url });
            var page = await context.NewPageAsync();
            await page.GotoAsync("/register");
            await page.Locator("#Email").FillAsync($"hosted-{Guid.NewGuid():N}@example.com");
            await page.Locator("#Password").FillAsync("LocalTest123!");
            await page.Locator("#ConfirmPassword").FillAsync("LocalTest123!");
            await page.Locator("#RegisterButton").ClickAsync();
            return page;
        }
        // BTCPay reserves the first registration for the host administrator.
        await (await RegisterAsync()).Context.CloseAsync();
        async Task<(IPage Page, string Store, string Invoice, string Address)> NewMerchantAsync(bool secondOwner = false)
        {
            var page = await RegisterAsync();
            await page.GotoAsync("/stores/create");
            await page.Locator("#Name").FillAsync("Hosted merchant");
            await page.Locator("#Create").ClickAsync();
            await page.Locator("#menu-item-General").ClickAsync();
            var store = await page.Locator("#Id").InputValueAsync();
            await page.Locator($"a[href='/stores/{store}/Zcashlike/ZEC']").First.ClickAsync();
            await page.Locator("#WalletPassword").FillAsync(await NewViewingKeyAsync(server, secondOwner));
            await page.Locator("#BirthHeight").FillAsync("1");
            await page.Locator("button[value='add-account']").ClickAsync();
            await Expect(page.Locator(".alert-success")).ToContainTextAsync("created for this store");
            await page.Locator("#Enabled").CheckAsync();
            await page.Locator("#SettlementConfirmationThresholdChoice").SelectOptionAsync("2");
            await page.Locator("#SaveButton").ClickAsync();
            await page.GotoAsync($"/stores/{store}/invoices");
            await page.Locator("#page-primary").ClickAsync();
            await page.Locator("#Amount").FillAsync("0.01");
            await page.Locator("#Currency").FillAsync("ZEC");
            await page.Locator("#page-primary").ClickAsync();
            var checkout = await page.Locator("a.invoice-checkout-link").First.GetAttributeAsync("href");
            var id = checkout!.Split('/').Last();
            await page.GotoAsync(checkout);
            var address = await page.Locator("#Address_ZEC-CHAIN [data-clipboard]").GetAttributeAsync("data-clipboard");
            return (page, store, id, address!);
        }
        var first = await NewMerchantAsync();
        var second = await NewMerchantAsync(secondOwner: true);
        if (server.UsesRegtest)
        {
            // Confirm cashcow change left by a previous run before spending again.
            await first.Page.Locator("#BlockCount").FillAsync("1");
            await SubmitCheatFormAsync(first.Page, "form#mine-block button", "/mine-blocks");
            first.Page.SetDefaultTimeout(120_000);
            second.Page.SetDefaultTimeout(120_000);
        }
        Assert.NotEqual(first.Address, second.Address);
        var lookup = await second.Page.Context.NewPageAsync();
        var denied = await lookup.GotoAsync($"/stores/{first.Store}/Zcashlike/ZEC");
        Assert.True(denied?.Status is 403 or 404 || new Uri(lookup.Url).AbsolutePath is "/errors/403" or "/errors/404",
            $"Expected a store access denial, received {denied?.Status} at {lookup.Url}");
        foreach (var merchant in new[] { first, second })
        {
            await SubmitCheatFormAsync(merchant.Page, "#FakePayment", "/test-payment");
            await Expect(merchant.Page.Locator("#CheatSuccessMessage")).ToBeVisibleAsync();
            await merchant.Page.Locator("#BlockCount").FillAsync("1");
            await SubmitCheatFormAsync(merchant.Page, "form#mine-block button", "/mine-blocks");
            await AssertInvoiceStatusAsync(merchant.Page, merchant.Store, merchant.Invoice, "Settled");
            if (merchant == first)
                await AssertInvoiceStatusAsync(lookup, second.Store, second.Invoice, "New");
        }
    }

    private async Task VerifySubscriptionLatencyAsync(BrowserTestServer server, IPage page,
        string storeId, string invoiceId, string address, int idleSeconds = 0)
    {
        await using var db = new Npgsql.NpgsqlConnection(server.DatabaseConnectionString);
        await db.OpenAsync();
        await using var receiver = new Npgsql.NpgsqlCommand(
            "SELECT \"AccountIndex\", \"AddressIndex\" FROM \"BTCPayServer.Plugins.ZCash\".\"Receivers\" WHERE \"UnifiedAddress\" = @address", db);
        receiver.Parameters.AddWithValue("address", address);
        int account, index;
        await using (var row = await receiver.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());
            account = row.GetInt32(0);
            index = row.GetInt32(1);
        }
        if (!server.UsesRegtest) Assert.Equal((2, 1), (account, index));
        using var http = new HttpClient { BaseAddress = new Uri(server.CashcowUrl) };
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (true)
        {
            if (server.UsesRegtest)
            {
                if (server.ReadLog().Contains($"GraphQL subscription requested for account {account} at "))
                {
                    // Local transport handshake precedes the later transaction broadcast.
                    await Task.Delay(500);
                    break;
                }
                Assert.True(DateTime.UtcNow < deadline, "Receiver subscription was not started. " + server.ReadLog());
                await Task.Delay(50);
                continue;
            }
            var accounts = JArray.Parse(await http.GetStringAsync("/test/subscriptions"));
            if (accounts.Values<int>().Contains(account)) break;
            Assert.True(DateTime.UtcNow < deadline, "Receiver subscription was not registered. " + server.ReadLog());
            await Task.Delay(50);
        }
        if (!server.UsesRegtest)
        {
            using var enabled = await http.PostAsync("/test/subscription-delivery/true", null);
            enabled.EnsureSuccessStatusCode();
        }
        if (idleSeconds > 0)
        {
            output.WriteLine($"Leaving the subscribed connection idle for {idleSeconds} seconds before paying.");
            await Task.Delay(TimeSpan.FromSeconds(idleSeconds));
        }
        var paymentLink = await page.Locator("#PayInWallet").GetAttributeAsync("href");
        var amount = paymentLink!.Split("amount=")[1].Split('&')[0];
        using var wallet = new Services.ZkoolGraphQlClient(new Uri(server.CashcowUrl), new HttpClient());
        var startedAt = DateTimeOffset.UtcNow;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var result = await wallet.SendAsync(
            "mutation($idAccount: Int!, $payment: Payment!) { pay(idAccount: $idAccount, payment: $payment) }",
            new { idAccount = 1, payment = new { recipients = new[] { new { address, amount } } } });
        var mutationReturnedAt = DateTimeOffset.UtcNow;
        var txid = Services.ZkoolGraphQlClient.RequireTransactionId(result["pay"]);
        var paymentId = $"{txid}#{account}#{index}";
        var wait = System.Diagnostics.Stopwatch.StartNew();
        await using var payment = new Npgsql.NpgsqlCommand(
            "SELECT \"Amount\" FROM \"Payments\" WHERE \"InvoiceDataId\" = @invoice AND \"Id\" = @payment", db);
        payment.Parameters.AddWithValue("invoice", invoiceId);
        payment.Parameters.AddWithValue("payment", paymentId);
        object? paidAmount;
        do
        {
            paidAmount = await payment.ExecuteScalarAsync();
            if (paidAmount != null && server.ReadLog().Contains($"{paymentId} via subscription at ")) break;
            Assert.True(wait.Elapsed < TimeSpan.FromSeconds(server.UsesRegtest ? 30 : 5), "Subscription did not persist payment. " + server.ReadLog());
            await Task.Delay(25);
        } while (true);
        elapsed.Stop();
        Assert.Equal(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), (decimal)paidAmount);
        // The persisted payment must come from a subscription, never a polling/reconciliation race.
        DateTimeOffset TraceTime(string pattern)
        {
            var match = System.Text.RegularExpressions.Regex.Match(server.ReadLog(), pattern);
            Assert.True(match.Success, "Missing subscription trace: " + pattern + "\n" + server.ReadLog());
            return DateTimeOffset.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        var receivedAt = TraceTime($"GraphQL subscription received transaction {txid} for account {account} at (\\S+)");
        var persistedAt = TraceTime($"{paymentId} via subscription at (\\S+)");
        if (!server.UsesRegtest)
        {
            using var sentEvents = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync("/test/sent-events"));
            var sentAt = sentEvents.RootElement.GetProperty(txid).GetDateTimeOffset();
            output.WriteLine($"Mock TX sent {sentAt:O}; send → BTCPay callback: {(receivedAt - sentAt).TotalMilliseconds:F1} ms.");
        }
        output.WriteLine($"TX {txid}: mutation started {startedAt:O}, returned {mutationReturnedAt:O}, subscription received {receivedAt:O}, payment persisted {persistedAt:O}");
        output.WriteLine($"Mutation start → persisted receipt observed: {elapsed.Elapsed.TotalMilliseconds:F1} ms; subscription callback → persistence: {(persistedAt - receivedAt).TotalMilliseconds:F1} ms.");
        if (!server.UsesRegtest)
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"Subscription detection took {elapsed.Elapsed.TotalMilliseconds:F1} ms.");
        Assert.InRange((persistedAt - receivedAt).TotalMilliseconds, 0, 2000);
        await AssertInvoiceStatusAsync(page, storeId, invoiceId, "Processing");
        await using var count = new Npgsql.NpgsqlCommand(
            "SELECT COUNT(*) FROM \"Payments\" WHERE \"InvoiceDataId\" = @invoice", db);
        count.Parameters.AddWithValue("invoice", invoiceId);
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    private static async Task VerifyExpiredInvoiceRecoveryAsync(BrowserTestServer server, IPage page,
        string storeId, string invoiceId, string address)
    {
        async Task<T> Scalar<T>(string sql)
        {
            await using var connection = new Npgsql.NpgsqlConnection(server.DatabaseConnectionString);
            await connection.OpenAsync();
            await using var command = new Npgsql.NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("invoice", invoiceId);
            command.Parameters.AddWithValue("address", address);
            return (T)(await command.ExecuteScalarAsync())!;
        }
        async Task WaitFor(Func<Task<bool>> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                if (await condition()) return;
                await Task.Delay(250);
            }
            Assert.Fail("Recovery assertion timed out. " + server.ReadLog());
        }
        const string schema = "\"BTCPayServer.Plugins.ZCash\"";
        var account = await Scalar<int>($"SELECT \"AccountIndex\" FROM {schema}.\"Receivers\" WHERE \"UnifiedAddress\" = @address");
        var index = await Scalar<int>($"SELECT \"AddressIndex\" FROM {schema}.\"Receivers\" WHERE \"UnifiedAddress\" = @address");
        var cursorSql = $"SELECT COALESCE((SELECT \"Height\" FROM {schema}.\"RecoveryCursors\" WHERE \"CryptoCode\" = 'ZEC' AND \"AccountIndex\" = {account}), -1)::bigint";
        await WaitFor(async () => await Scalar<long>(cursorSql) >= 200);
        var before = await Scalar<long>(cursorSql);
        var link = await page.Locator("#PayInWallet").GetAttributeAsync("href");
        var amount = link!.Split("amount=")[1].Split('&')[0];
        await page.Locator("#ExpirySeconds").FillAsync("2");
        await SubmitCheatFormAsync(page, "#Expire", "/expire");
        await server.StopDetectionAsync();
        using var wallet = new Services.ZkoolGraphQlClient(new Uri(server.CashcowUrl), new HttpClient());
        var paid = await wallet.SendAsync("mutation($idAccount: Int!, $payment: Payment!) { pay(idAccount: $idAccount, payment: $payment) }",
            new { idAccount = 1, payment = new { recipients = new[] { new { address, amount } } } });
        var txid = Services.ZkoolGraphQlClient.RequireTransactionId(paid["pay"]);
        using var http = new HttpClient();
        using var mined = await http.PostAsync(new Uri(new Uri(server.CashcowUrl), "/rpc"),
            new StringContent("{\"method\":\"generate\",\"params\":[2]}", System.Text.Encoding.UTF8, "application/json"));
        mined.EnsureSuccessStatusCode();
        await Task.Delay(2500);
        // BTCPay gives listeners a two-minute startup grace period before expiring invoices.
        // Keep the wallet unavailable until that period ends and the invoice expires.
        await server.SetMockWalletAvailableAsync(false);
        await server.RestartDetectionAsync();
        await AssertInvoiceStatusAsync(page, storeId, invoiceId, "Expired", timeoutSeconds: 180);
        Assert.Equal(0L, await Scalar<long>("SELECT COUNT(*) FROM \"Payments\" WHERE \"InvoiceDataId\" = @invoice"));
        Assert.Equal(before, await Scalar<long>(cursorSql));
        // A DB write failure must leave the cursor unchanged. This database is isolated per case.
        await Scalar<object>("CREATE FUNCTION reject_recovery_payment() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RAISE EXCEPTION ''test payment persistence failure''; END'; CREATE TRIGGER reject_recovery_payment BEFORE INSERT ON \"Payments\" FOR EACH ROW EXECUTE FUNCTION reject_recovery_payment(); SELECT 1;");
        await server.SetMockWalletAvailableAsync(true);
        await WaitFor(() => Task.FromResult(server.ReadLog().Contains("Could not persist Zcash payment")));
        Assert.Equal(before, await Scalar<long>(cursorSql));
        await Scalar<object>("DROP TRIGGER reject_recovery_payment ON \"Payments\"; DROP FUNCTION reject_recovery_payment(); SELECT 1;");
        await WaitFor(async () => await Scalar<long>(cursorSql) == 202);
        Assert.Equal(1L, await Scalar<long>("SELECT COUNT(*) FROM \"Payments\" WHERE \"InvoiceDataId\" = @invoice"));
        Assert.Equal($"{txid}#{account}#{index}", await Scalar<string>("SELECT \"Id\" FROM \"Payments\" WHERE \"InvoiceDataId\" = @invoice"));
        Assert.Equal(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture),
            await Scalar<decimal>("SELECT \"Amount\" FROM \"Payments\" WHERE \"InvoiceDataId\" = @invoice"));
        // The persistent cursor survives another restart; forced inclusive replay remains idempotent.
        await server.StopDetectionAsync();
        using var nextBlock = await http.PostAsync(new Uri(new Uri(server.CashcowUrl), "/rpc"),
            new StringContent("{\"method\":\"generate\",\"params\":[1]}", System.Text.Encoding.UTF8, "application/json"));
        nextBlock.EnsureSuccessStatusCode();
        await server.RestartDetectionAsync();
        await WaitFor(async () => await Scalar<long>(cursorSql) == 203);
        Assert.Equal(1L, await Scalar<long>("SELECT COUNT(*) FROM \"Payments\" WHERE \"InvoiceDataId\" = @invoice"));
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

    private static async Task AssertInvoiceStatusAsync(IPage page, string storeId, string invoiceId, string status, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
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
