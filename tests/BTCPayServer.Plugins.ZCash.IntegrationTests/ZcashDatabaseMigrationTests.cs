using System.Collections.Concurrent;
using System.Data.Common;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.ZCash.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;

namespace BTCPayServer.Plugins.ZCash.IntegrationTests;

[Trait("Category", "Database")]
public class ZcashDatabaseMigrationTests
{
    [PostgresFact]
    public async Task EveryReleasedSchemaUpgradesWithoutLosingReceiversOrRecoveryCursors()
    {
        using var catalog = ZcashMigrationTests.Factory("Host=localhost;Database=unused").CreateContext();
        var migrations = catalog.Database.GetMigrations().ToArray();
        // Includes an empty install and an already-current database. New migrations
        // automatically add an upgrade starting point to this test.
        foreach (var previous in new[] { "0" }.Concat(migrations))
        {
            await using var database = await TestDatabase.Create();
            var factory = ZcashMigrationTests.Factory(database.ConnectionString);
            await using (var old = factory.CreateContext())
            {
                if (previous != "0")
                {
                    await old.GetService<IMigrator>().MigrateAsync(previous);
                    await database.Sql("""
                        INSERT INTO "BTCPayServer.Plugins.ZCash"."Receivers"
                            ("AccountIndex", "AddressIndex", "UnifiedAddress", "SaplingAddress", "OrchardAddress",
                             "TransparentAddress", "InvoiceId", "CreatedAt")
                        VALUES (7, 12, 'u-test-preserved', 'zs-test', 'orchard-test', 't-test', 'invoice-test', '2026-01-01T00:00:00Z')
                        """);
                    if (string.CompareOrdinal(previous, "20261006000000_AddRecoveryCursors") >= 0)
                        await database.Sql("""
                            INSERT INTO "BTCPayServer.Plugins.ZCash"."RecoveryCursors"
                            ("CryptoCode", "AccountIndex", "Height") VALUES ('ZEC', 7, 12345)
                            """);
                }
            }

            using var host = new HostBuilder().Build();
            var runner = ZcashMigrationTests.Runner(factory, host);
            await runner.Execute(CancellationToken.None);
            await runner.Execute(CancellationToken.None);
            await using var current = factory.CreateContext();
            Assert.Equal(migrations, await current.Database.GetAppliedMigrationsAsync());
            Assert.Empty(await current.Database.GetPendingMigrationsAsync());
            if (previous != "0")
            {
                var receiver = Assert.Single(await current.Receivers.ToListAsync());
                Assert.Equal((7, 12, "u-test-preserved", "invoice-test"),
                    (receiver.AccountIndex, receiver.AddressIndex, receiver.UnifiedAddress, receiver.InvoiceId));
                Assert.Equal(("zs-test", "orchard-test", "t-test"),
                    (receiver.SaplingAddress, receiver.OrchardAddress, receiver.TransparentAddress));
                Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), receiver.CreatedAt);
            }
            if (string.CompareOrdinal(previous, "20261006000000_AddRecoveryCursors") >= 0)
                Assert.Equal(12345, (await current.RecoveryCursors.SingleAsync()).Height);
            else
                Assert.Empty(await current.RecoveryCursors.ToListAsync());

            // History stays in the server's search path, not in the plugin table schema.
            Assert.Equal(migrations.Length, Convert.ToInt32(await database.Scalar(
                "SELECT count(*) FROM public.\"BTCPayServer.Plugins.ZCash\"")));
        }
    }

    [PostgresFact]
    public async Task ConcurrentStartupUsesEfMigrationLocking()
    {
        await using var database = await TestDatabase.Create();
        using var first = new HostBuilder().Build();
        using var second = new HostBuilder().Build();
        var factory = ZcashMigrationTests.Factory(database.ConnectionString);
        await Task.WhenAll(
            ZcashMigrationTests.Runner(factory, first).Execute(CancellationToken.None),
            ZcashMigrationTests.Runner(factory, second).Execute(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(60));
        await using var context = factory.CreateContext();
        Assert.Equal(context.Database.GetMigrations(), await context.Database.GetAppliedMigrationsAsync());
    }

    [PostgresFact]
    public async Task StartupRecoversAfterTransientConnectionFailure()
    {
        await using var database = await TestDatabase.Create();
        var factory = new RecoveringFactory(database.ConnectionString);
        using var host = new HostBuilder().Build();
        await ZcashMigrationTests.Runner(factory, host).Execute(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, factory.Attempts);
        await using var context = ZcashMigrationTests.Factory(database.ConnectionString).CreateContext();
        Assert.Equal(context.Database.GetMigrations(), await context.Database.GetAppliedMigrationsAsync());
    }

    [PostgresFact]
    public async Task LostConnectionDuringMigrationRollsBackAndRetriesWithANewSession()
    {
        await using var database = await TestDatabase.Create();
        var normal = ZcashMigrationTests.Factory(database.ConnectionString);
        await using (var old = normal.CreateContext())
            await old.GetService<IMigrator>().MigrateAsync("20260814003855_AddZcashReceivers");
        await database.Sql("""
            INSERT INTO "BTCPayServer.Plugins.ZCash"."Receivers"
            ("AccountIndex", "AddressIndex", "UnifiedAddress", "CreatedAt")
            VALUES (7, 12, 'u-before-disconnect', '2026-01-01T00:00:00Z')
            """);
        var interceptor = new InterruptedMigration();
        var factory = new InterceptingFactory(database.ConnectionString, interceptor);
        using var host = new HostBuilder().Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var startup = ZcashMigrationTests.Runner(factory, host).Execute(timeout.Token);
        try
        {
            var pid = await interceptor.Started.Task.WaitAsync(timeout.Token);
            Assert.Equal(true, await database.Scalar($"SELECT pg_terminate_backend({pid})"));
        }
        finally
        {
            interceptor.Continue.TrySetResult();
        }
        await startup.WaitAsync(timeout.Token);
        Assert.True(factory.Attempts >= 2);
        Assert.All(factory.Sessions.OpenCounts.Values, count => Assert.Equal(1, count));
        await using var current = normal.CreateContext();
        Assert.Equal(current.Database.GetMigrations(), await current.Database.GetAppliedMigrationsAsync());
        Assert.Equal("u-before-disconnect", (await current.Receivers.SingleAsync()).UnifiedAddress);
        Assert.Empty(await current.RecoveryCursors.ToListAsync());
        Assert.Equal(true, await database.Scalar("SELECT pg_try_advisory_lock(802469322)"));
    }

    [PostgresFact]
    public async Task CancellationWhileDatabaseIsLockedAbortsStartupSafely()
    {
        await using var database = await TestDatabase.Create();
        var factory = ZcashMigrationTests.Factory(database.ConnectionString);
        using var host = new HostBuilder().Build();
        var runner = ZcashMigrationTests.Runner(factory, host);
        await runner.Execute(CancellationToken.None);
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand(
                "LOCK TABLE public.\"BTCPayServer.Plugins.ZCash\" IN ACCESS EXCLUSIVE MODE", connection, transaction);
            await command.ExecuteNonQueryAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAsync<ConfigException>(() => runner.Execute(cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(10)));
        }
        await runner.Execute(CancellationToken.None);
    }

    [PostgresFact]
    public async Task HistoryUsesConfiguredSearchPathAndPreservesCoreHistory()
    {
        await using var database = await TestDatabase.Create();
        await database.Sql("""
            CREATE SCHEMA tenant;
            CREATE TABLE tenant."__EFMigrationsHistory" ("MigrationId" text PRIMARY KEY, "ProductVersion" text NOT NULL);
            INSERT INTO tenant."__EFMigrationsHistory" VALUES ('core-sentinel', '10.0');
            """);
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { SearchPath = "tenant,public" };
        var factory = ZcashMigrationTests.Factory(connection.ConnectionString);
        using var host = new HostBuilder().Build();
        await ZcashMigrationTests.Runner(factory, host).Execute(CancellationToken.None);
        Assert.Equal("core-sentinel", await database.Scalar("SELECT \"MigrationId\" FROM tenant.\"__EFMigrationsHistory\""));
        Assert.Equal(true, await database.Scalar("""SELECT to_regclass('tenant."BTCPayServer.Plugins.ZCash"') IS NOT NULL"""));
        await using var context = factory.CreateContext();
        Assert.Equal(context.Database.GetMigrations(), await context.Database.GetAppliedMigrationsAsync());
    }

    [PostgresFact]
    public async Task UnknownOrIncompleteHistoryStopsStartupWithoutRewritingHistory()
    {
        await using var database = await TestDatabase.Create();
        var factory = ZcashMigrationTests.Factory(database.ConnectionString);
        using var host = new HostBuilder().Build();
        var runner = ZcashMigrationTests.Runner(factory, host);
        await runner.Execute(CancellationToken.None);
        await database.Sql("""
            INSERT INTO public."BTCPayServer.Plugins.ZCash" ("MigrationId", "ProductVersion")
            VALUES ('99999999999999_FutureVersion', '99.0')
            """);
        await Assert.ThrowsAsync<ConfigException>(() => runner.Execute(CancellationToken.None));
        Assert.Equal(1L, await database.Scalar("""
            SELECT count(*) FROM public."BTCPayServer.Plugins.ZCash" WHERE "MigrationId" = '99999999999999_FutureVersion'
            """));
        await database.Sql("""
            DELETE FROM public."BTCPayServer.Plugins.ZCash"
            WHERE "MigrationId" IN ('99999999999999_FutureVersion', '20260814003855_AddZcashReceivers')
            """);
        await Assert.ThrowsAsync<ConfigException>(() => runner.Execute(CancellationToken.None));
    }

    [PostgresFact]
    public async Task SchemaConflictDoesNotMarkMigrationAppliedAndCanBeRetriedAfterRepair()
    {
        await using var database = await TestDatabase.Create();
        await database.Sql("""
            CREATE SCHEMA "BTCPayServer.Plugins.ZCash";
            CREATE TABLE "BTCPayServer.Plugins.ZCash"."Receivers" (sentinel text);
            INSERT INTO "BTCPayServer.Plugins.ZCash"."Receivers" VALUES ('keep-me');
            """);
        var factory = ZcashMigrationTests.Factory(database.ConnectionString);
        using var host = new HostBuilder().Build();
        var runner = ZcashMigrationTests.Runner(factory, host);
        await Assert.ThrowsAsync<ConfigException>(() => runner.Execute(CancellationToken.None));
        Assert.Equal("keep-me", await database.Scalar("SELECT sentinel FROM \"BTCPayServer.Plugins.ZCash\".\"Receivers\""));
        await using (var context = factory.CreateContext())
            Assert.Empty(await context.Database.GetAppliedMigrationsAsync());
        // Simulate an operator preserving the conflicting table, not automatic destructive repair.
        await database.Sql("ALTER TABLE \"BTCPayServer.Plugins.ZCash\".\"Receivers\" RENAME TO \"PreservedLegacyTable\"" );
        await runner.Execute(CancellationToken.None);
        Assert.Equal("keep-me", await database.Scalar("SELECT sentinel FROM \"BTCPayServer.Plugins.ZCash\".\"PreservedLegacyTable\""));
    }

    [PostgresFact]
    public async Task MigrationLockDoesNotLeakIntoPooledConnectionsAfterSuccessOrFailure()
    {
        await using var database = await TestDatabase.Create();
        var pooled = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            { Pooling = true, NoResetOnClose = true, MaxPoolSize = 1 };
        var factory = ZcashMigrationTests.Factory(pooled.ConnectionString);
        using var host = new HostBuilder().Build();
        var runner = ZcashMigrationTests.Runner(factory, host);
        try
        {
            await runner.Execute(CancellationToken.None);
            // This is a distinct, unpooled session. Closing it releases the probe lock.
            Assert.Equal(true, await database.Scalar("SELECT pg_try_advisory_lock(802469322)"));
            await database.Sql("""
                INSERT INTO public."BTCPayServer.Plugins.ZCash" ("MigrationId", "ProductVersion")
                VALUES ('99999999999999_FutureVersion', '99.0')
                """);
            await Assert.ThrowsAsync<ConfigException>(() => runner.Execute(CancellationToken.None));
            Assert.Equal(true, await database.Scalar("SELECT pg_try_advisory_lock(802469322)"));
        }
        finally
        {
            await using var connection = new NpgsqlConnection(pooled.ConnectionString);
            await connection.OpenAsync();
            NpgsqlConnection.ClearPool(connection);
        }
    }

    private sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BTCPAY_TEST_POSTGRES")))
                Skip = "Set BTCPAY_TEST_POSTGRES to an isolated PostgreSQL server; CI always runs these tests.";
        }
    }

    private sealed class RecoveringFactory(string connectionString)
        : ZcashPluginDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = connectionString }))
    {
        public int Attempts { get; private set; }
        public override ZcashPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? options = null)
        {
            if (++Attempts != 1) return base.CreateContext(options);
            // Exercise a real Npgsql connection failure and exhausted EF retry policy.
            var unavailable = new NpgsqlConnectionStringBuilder(connectionString) { Port = 1, Timeout = 1 };
            var builder = new DbContextOptionsBuilder<ZcashPluginDbContext>();
            builder.UseNpgsql(unavailable.ConnectionString, o => o.EnableRetryOnFailure(0));
            return new ZcashPluginDbContext(builder.Options);
        }
    }

    private sealed class InterceptingFactory(string connectionString, DbCommandInterceptor interceptor)
        : ZcashPluginDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = connectionString }))
    {
        public int Attempts { get; private set; }
        public SessionObserver Sessions { get; } = new();
        public override ZcashPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? options = null)
        {
            Attempts++;
            var builder = new DbContextOptionsBuilder<ZcashPluginDbContext>();
            ConfigureBuilder(builder, options);
            builder.AddInterceptors(interceptor, Sessions);
            return new ZcashPluginDbContext(builder.Options);
        }
    }

    private sealed class InterruptedMigration : DbCommandInterceptor
    {
        public TaskCompletionSource<int> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("CREATE TABLE \"BTCPayServer.Plugins.ZCash\".\"RecoveryCursors\"", StringComparison.Ordinal)
                && Started.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID))
                // The DDL has executed but the migration has not committed its history row.
                await Continue.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class SessionObserver : DbConnectionInterceptor
    {
        public ConcurrentDictionary<Guid, int> OpenCounts { get; } = new();
        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context && ReferenceEquals(connection, context.Database.GetDbConnection()))
                OpenCounts.AddOrUpdate(context.ContextId.InstanceId, 1, (_, count) => count + 1);
            return Task.CompletedTask;
        }
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string admin;
        private readonly string name = "zec_migration_" + Guid.NewGuid().ToString("N");
        public string ConnectionString { get; }

        private TestDatabase()
        {
            var cs = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("BTCPAY_TEST_POSTGRES"))
                { Pooling = false };
            admin = cs.ConnectionString;
            cs.Database = name;
            ConnectionString = cs.ConnectionString;
        }

        public static async Task<TestDatabase> Create()
        {
            var db = new TestDatabase();
            await using var connection = new NpgsqlConnection(db.admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {db.name}", connection);
            await command.ExecuteNonQueryAsync();
            return db;
        }

        public async Task<object?> Scalar(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return await command.ExecuteScalarAsync();
        }

        public async Task Sql(string sql) => _ = await Scalar(sql);

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
