using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Plugins;
using BTCPayServer.Plugins.Altcoins;
using BTCPayServer.Plugins.ZCash.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;

namespace BTCPayServer.Plugins.ZCash.IntegrationTests;

public class ZcashMigrationTests
{
    [Fact]
    public void ModelHasAMatchingMigrationSnapshot()
    {
        using var context = Factory("Host=localhost;Database=unused").CreateContext();
        Assert.False(context.Database.HasPendingModelChanges(),
            "Generate and review a forward migration when changing the Zcash model.");
        Assert.NotEmpty(context.Database.GetMigrations());
    }

    [Fact]
    public void PluginRegistersMigrationsInBtcpayDatabaseStartupPhase()
    {
        var services = new ServiceCollection();
        using var bootstrap = new ServiceCollection()
            .AddSingleton(new NBXplorer.NBXplorerNetworkProvider(ChainName.Regtest))
            .BuildServiceProvider();
        new ZCashPlugin().Execute(new PluginServiceCollection(services, bootstrap));
        Assert.Single(services, s => s.ServiceType == typeof(IMigrationExecutor)
                                    && s.ImplementationType == typeof(ZcashMigrationRunner));
        Assert.DoesNotContain(services, s => s.ServiceType == typeof(IHostedService)
                                            && s.ImplementationType == typeof(ZcashMigrationRunner));
    }

    [Fact]
    public async Task PermanentDatabaseFailureStopsStartupAsConfigurationError()
    {
        var factory = new FailingFactory(new PostgresException("permission denied", "ERROR", "ERROR", "42501"));
        using var host = new HostBuilder().Build();
        var runner = Runner(factory, host);
        var error = await Assert.ThrowsAsync<ConfigException>(() => runner.Execute(CancellationToken.None));
        Assert.Contains("startup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, factory.Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransientDatabaseFailureWaitsAndCancellationDoesNotEscapeAsPluginCrash(bool stopHost)
    {
        using var cancellation = new CancellationTokenSource();
        using var host = new HostBuilder().Build();
        var logger = new RetryLogger();
        var factory = new FailingFactory(new NpgsqlException("database temporarily unavailable", new TimeoutException()));
        var runner = new ZcashMigrationRunner(factory, logger,
            host.Services.GetRequiredService<IHostApplicationLifetime>());
        var startup = runner.Execute(cancellation.Token);
        await logger.Retry.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(startup.IsCompleted);
        if (stopHost)
            host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        else
            cancellation.Cancel();
        await Assert.ThrowsAsync<ConfigException>(() => startup.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AlreadyCanceledStartupDoesNotAccessDatabaseOrReportSuccess()
    {
        var factory = new FailingFactory(new InvalidOperationException("Must not connect"));
        using var host = new HostBuilder().Build();
        await Assert.ThrowsAsync<ConfigException>(() => Runner(factory, host).Execute(new CancellationToken(true)));
        Assert.Equal(0, factory.Attempts);
    }

    internal static ZcashPluginDbContextFactory Factory(string connectionString) =>
        new(Options.Create(new DatabaseOptions { ConnectionString = connectionString }));

    internal static ZcashMigrationRunner Runner(ZcashPluginDbContextFactory factory, IHost host) =>
        new(factory, NullLogger<ZcashMigrationRunner>.Instance,
            host.Services.GetRequiredService<IHostApplicationLifetime>());

    private sealed class FailingFactory(Exception failure)
        : ZcashPluginDbContextFactory(Options.Create(new DatabaseOptions()))
    {
        public int Attempts { get; private set; }
        public override ZcashPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? options = null)
        {
            Attempts++;
            throw failure;
        }
    }

    private sealed class RetryLogger : ILogger<ZcashMigrationRunner>
    {
        public TaskCompletionSource Retry { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning) Retry.TrySetResult();
        }
    }
}
