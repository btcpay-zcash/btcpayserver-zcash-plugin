using BTCPayServer.Plugins.ZCash.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BTCPayServer.Plugins.ZCash.Data.Migrations;

[DbContext(typeof(ZcashPluginDbContext))]
[Migration("20261006000000_AddRecoveryCursors")]
public partial class AddRecoveryCursors : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "RecoveryCursors", schema: "BTCPayServer.Plugins.ZCash",
            columns: table => new
            {
                CryptoCode = table.Column<string>(type: "text", nullable: false),
                AccountIndex = table.Column<long>(type: "bigint", nullable: false),
                Height = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_RecoveryCursors", x => new { x.CryptoCode, x.AccountIndex }));
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "RecoveryCursors", schema: "BTCPayServer.Plugins.ZCash");

}
