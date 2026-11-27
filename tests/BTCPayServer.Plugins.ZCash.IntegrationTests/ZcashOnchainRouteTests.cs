using System.Net;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.ZCash.IntegrationTests;

// The plugin redirects core's /stores/{storeId}/onchain/{cryptoCode} wallet setup
// URL to its own settings page for ZEC. That route must not also match other coins,
// or ASP.NET Core finds two endpoints for the request and answers HTTP 500.
public class ZcashOnchainRouteTests(ITestOutputHelper output)
{
    [LocalBrowserFact]
    [Trait("Category", "Playwright")]
    public async Task OnchainSetupRouteServesZcashAndLeavesOtherCoinsToCore()
    {
        // BTC is enabled beside ZEC so core's SetupWallet has a real network to serve.
        // It needs no NBXplorer to render the setup page.
        await using var server = await BrowserTestServer.StartAsync(forceMock: true, chains: "btc,zec");

        // Routing runs before authentication, so an anonymous request is enough to
        // tell an ambiguous match (500) from a resolved one (redirect to login).
        using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(server.Url) })
        {
            foreach (var coin in new[] { "BTC", "ZEC", "zec" })
            {
                using var anonymous = await http.GetAsync($"/stores/anystore/onchain/{coin}");
                output.WriteLine($"anonymous GET /stores/anystore/onchain/{coin}: {(int)anonymous.StatusCode} {anonymous.Headers.Location}");
                Assert.Equal(HttpStatusCode.Found, anonymous.StatusCode);
                Assert.Contains("/login", anonymous.Headers.Location?.ToString(), StringComparison.OrdinalIgnoreCase);
            }
        }

        using var playwright = await Playwright.CreateAsync();
        if (Environment.GetEnvironmentVariable("BTCPAY_PLAYWRIGHT_INSTALL") == "1")
            Assert.Equal(0, Microsoft.Playwright.Program.Main(["install", "chromium"]));
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            Channel = Environment.GetEnvironmentVariable("BTCPAY_PLAYWRIGHT_CHANNEL")
        });
        await using var context = await browser.NewContextAsync(new() { BaseURL = server.Url });
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
            await page.Locator("#Name").FillAsync("Onchain route test");
            await page.Locator("#Create").ClickAsync();
            await page.Locator("#menu-item-General").ClickAsync();
            var storeId = await page.Locator("#Id").InputValueAsync();
            Assert.False(string.IsNullOrWhiteSpace(storeId));

            // Core's wallet setup page for BTC.
            var btc = await page.GotoAsync($"/stores/{storeId}/onchain/BTC");
            output.WriteLine($"GET /stores/{storeId}/onchain/BTC: {btc?.Status} -> {page.Url}");
            Assert.Equal(200, btc?.Status);
            Assert.EndsWith($"/stores/{storeId}/onchain/BTC", page.Url);

            // ZEC, in either case, still lands on the plugin's own settings page.
            foreach (var coin in new[] { "ZEC", "zec" })
            {
                var zec = await page.GotoAsync($"/stores/{storeId}/onchain/{coin}");
                output.WriteLine($"GET /stores/{storeId}/onchain/{coin}: {zec?.Status} -> {page.Url}");
                Assert.Equal(200, zec?.Status);
                Assert.EndsWith($"/stores/{storeId}/Zcashlike/ZEC", page.Url, StringComparison.OrdinalIgnoreCase);
            }

            // The deeper core pages never collided; check they still reach core.
            var import = await page.GotoAsync($"/stores/{storeId}/onchain/BTC/import/xpub");
            Assert.Equal(200, import?.Status);

            Assert.DoesNotContain("AmbiguousMatchException", server.ReadLog());
        }
        catch
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(server.Artifacts, "failure.png"), FullPage = true });
            output.WriteLine(server.ReadLog());
            throw;
        }
    }
}
