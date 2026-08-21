using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BTCPayServer.Plugins.ZCash.Data;

// ReSharper disable once UnusedType.Global
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ZcashPluginDbContext>
{
    public ZcashPluginDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ZcashPluginDbContext>();

        // FIXME: Somehow the DateTimeOffset column types get messed up when not using Postgres
        // https://docs.microsoft.com/en-us/ef/core/managing-schemas/migrations/providers?tabs=dotnet-core-cli
        builder.UseNpgsql("User ID=postgres;Host=127.0.0.1;Port=39372;Database=designtimebtcpay");

        return new ZcashPluginDbContext(builder.Options, true);
    }
}