using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.ZCash.Data;

public class ZcashPluginDbContextFactory : BaseDbContextFactory<ZcashPluginDbContext>
{
    public ZcashPluginDbContextFactory(IOptions<DatabaseOptions> options)
        : base(options, "BTCPayServer.Plugins.ZCash")
    {
    }

    public override ZcashPluginDbContext CreateContext(
        Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
    {
        var builder = new DbContextOptionsBuilder<ZcashPluginDbContext>();
        ConfigureBuilder(builder, npgsqlOptionsAction);
        return new ZcashPluginDbContext(builder.Options);
    }
}