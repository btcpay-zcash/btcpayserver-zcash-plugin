using BTCPayServer.Plugins.ZCash.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.ZCash.Data;

public class ZcashPluginDbContext : DbContext
{
    private readonly bool _designTime;

    public ZcashPluginDbContext(DbContextOptions<ZcashPluginDbContext> options, bool designTime = false)
        : base(options)
    {
        _designTime = designTime;
    }

    public DbSet<ZcashReceiver> Receivers { get; set; }
    public DbSet<ZcashRecoveryCursor> RecoveryCursors { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("BTCPayServer.Plugins.ZCash");

        modelBuilder.Entity<ZcashRecoveryCursor>(b =>
        {
            b.HasKey(e => new { e.CryptoCode, e.AccountIndex });
        });

        modelBuilder.Entity<ZcashReceiver>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.UnifiedAddress).IsRequired();
            b.HasIndex(e => e.UnifiedAddress).IsUnique();
            b.HasIndex(e => e.SaplingAddress);
            b.HasIndex(e => e.OrchardAddress);
            b.HasIndex(e => e.TransparentAddress);
            b.HasIndex(e => e.InvoiceId);
        });
    }
}
