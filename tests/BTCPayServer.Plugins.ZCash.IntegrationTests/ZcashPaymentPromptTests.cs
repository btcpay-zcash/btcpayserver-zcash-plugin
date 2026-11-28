using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Altcoins;
using BTCPayServer.Plugins.ZCash.Payments;
using BTCPayServer.Plugins.ZCash.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.ZCash.IntegrationTests;

public class ZcashPaymentPromptTests : ZcashIntegrationTestBase
{
    [Fact]
    public async Task WalletTimeoutIsLoggedWithInvoiceAndStoreAndRethrown()
    {
        using var http = new HttpClient(new HangingHttpHandler()) { Timeout = TimeSpan.FromMilliseconds(100) };
        using var graphQl = new ZkoolGraphQlClient(new Uri("http://127.0.0.1/graphql"), http);
        Provider.WalletBackends = Provider.WalletBackends.SetItem("ZEC",
            new ZkoolGraphQlBackend("ZEC", graphQl, null!, null!, NullLogger<ZkoolGraphQlBackend>.Instance));
        Provider.Summaries["ZEC"] = new ZcashRPCProvider.ZcashLikeSummary { Synced = true, WalletAvailable = true };
        var logger = new PromptLogger();
        var handler = new ZcashLikePaymentMethodHandler(new ZcashLikeSpecificBtcPayNetwork { CryptoCode = "ZEC" }, Provider, logger);
        // BTCPay 2.4 adds an InvoiceRepository argument.
        var constructor = Assert.Single(typeof(PaymentMethodContext).GetConstructors());
        object?[] arguments = [
            new StoreData { Id = "store-id" }, new StoreBlob(),
            new JObject { ["accountIndex"] = 1, ["viewingKeyHash"] = "hash" },
            handler, new InvoiceEntity { Id = "invoice-id", Currency = "USD" }, new InvoiceLogs()
        ];
        var context = (PaymentMethodContext)constructor.Invoke(
            constructor.GetParameters().Length == 6 ? arguments : [.. arguments, null]);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.ConfigurePrompt(context));

        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.Same(error, logger.Exception);
        Assert.Contains("invoice-id", logger.Message);
        Assert.Contains("store-id", logger.Message);
    }

    private sealed class HangingHttpHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The request should have timed out.");
        }
    }

    private sealed class PromptLogger : ILogger<ZcashLikePaymentMethodHandler>
    {
        public Exception? Exception { get; private set; }
        public string Message { get; private set; } = "";
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level != LogLevel.Error) return;
            Exception = exception;
            Message = formatter(state, exception);
        }
    }
}
