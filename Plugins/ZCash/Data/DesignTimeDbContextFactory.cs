using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BTCPayServer.Plugins.ZCash.Data;

// ReSharper disable once UnusedType.Global
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ZcashPluginDbContext>
{
    public ZcashPluginDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ZcashPluginDbContext>();

        // Generate the same PostgreSQL types and schema used by BTCPay at runtime.
        // Migration scaffolding does not connect to this design-time database.
        builder.UseNpgsql("User ID=postgres;Host=127.0.0.1;Port=39372;Database=designtimebtcpay");

        return new ZcashPluginDbContext(builder.Options, true);
    }
}
