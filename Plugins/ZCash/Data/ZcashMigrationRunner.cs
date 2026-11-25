using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BTCPayServer.Plugins.ZCash.Data;

// Executed by BTCPay's MigrationStartupTask, before the web host and wallet services start.
// Keep BaseDbContextFactory's history-table configuration compatible with installed versions.
public class ZcashMigrationRunner(
    ZcashPluginDbContextFactory dbContextFactory,
    ILogger<ZcashMigrationRunner> logger,
    IHostApplicationLifetime lifetime) : IMigrationExecutor
{
    public async Task Execute(CancellationToken cancellationToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, lifetime.ApplicationStopping);
        var token = stopping.Token;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    // Retry the entire operation with a fresh connection: a reconnect must
                    // never continue after losing the session-level bootstrap lock.
                    await using var ctx = dbContextFactory.CreateContext(o => o.EnableRetryOnFailure(0));
                    // Match BTCPay's migration budget; the normal query timeout is too short
                    // for an upgrade that backfills data or waits for another migrator's lock.
                    ctx.Database.SetCommandTimeout(TimeSpan.FromDays(1));
                    if (ctx.Database.HasPendingModelChanges())
                        throw new InvalidOperationException("The Zcash model has changes without a migration.");

                    await ctx.Database.OpenConnectionAsync(token);
                    try
                    {
                        // Serialize validation and the entire migration batch, including
                        // history bootstrap and gaps between EF's migration transactions.
                        // This is a session lock, not an EF transaction.
                        // Keep this plugin-specific key stable across releases.
                        await ctx.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(802469322)", token);
                        var known = ctx.Database.GetMigrations().ToArray();
                        var applied = (await ctx.Database.GetAppliedMigrationsAsync(token)).ToArray();
                        if (known.Length == 0 || !applied.SequenceEqual(known.Take(applied.Length)))
                            throw new InvalidOperationException(
                                "The Zcash migration history does not match this plugin version. " +
                                "Use the matching or a newer plugin; do not delete migration-history rows.");

                        // EF/Npgsql still owns migration transactions and history-table locking.
                        // Do not wrap MigrateAsync in a transaction or use EnsureCreated.
                        await ctx.Database.MigrateAsync(token);
                    }
                    finally
                    {
                        await ReleaseLockAsync((NpgsqlConnection)ctx.Database.GetDbConnection());
                    }
                    logger.LogInformation("Zcash database migrations completed");
                    return;
                }
                catch (Exception ex) when (!token.IsCancellationRequested && IsTransient(ex))
                {
                    // Keep startup waiting for database recovery rather than allowing a
                    // temporary outage to disable the plugin.
                    logger.LogWarning(ex, "Zcash database is unavailable; retrying initialization in 5 seconds");
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // Program.Main handles ConfigException before attributing crashes to plugins.
            // Returning here would incorrectly let startup proceed without a usable schema.
            throw new ConfigException("Zcash database initialization was canceled; server startup stopped.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Zcash database initialization failed; server startup stopped");
            throw new ConfigException(
                "Zcash database initialization failed; server startup stopped. See the preceding database error, correct the " +
                "database permissions/schema or plugin version, then restart. The plugin has not been disabled.");
        }
    }

    private async Task ReleaseLockAsync(NpgsqlConnection connection)
    {
        // A session lock belongs to this physical connection. EF's ExecuteSqlRawAsync
        // can reopen a broken connection, so cleanup must use the existing connection
        // directly. Never reconnect to unlock, or mask the original migration failure.
        try
        {
            if (connection.State != ConnectionState.Open)
            {
                NpgsqlConnection.ClearPool(connection);
                return;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_unlock(802469322)";
            command.CommandTimeout = 5;
            // Attempt cleanup even when startup has been canceled.
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Discard the session on disposal instead of returning a possibly locked
            // connection to the pool, including when NoResetOnClose is configured.
            NpgsqlConnection.ClearPool(connection);
            logger.LogWarning(ex, "Closing database session after migration lock cleanup failed");
        }
    }

    private static bool IsTransient(Exception exception) => exception switch
    {
        NpgsqlException postgres => postgres.IsTransient,
        TimeoutException => true,
        RetryLimitExceededException { InnerException: { } inner } => IsTransient(inner),
        _ => false
    };
}
