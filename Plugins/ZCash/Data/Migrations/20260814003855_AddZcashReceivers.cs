using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BTCPayServer.Plugins.ZCash.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddZcashReceivers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "BTCPayServer.Plugins.ZCash");

            migrationBuilder.CreateTable(
                name: "Receivers",
                schema: "BTCPayServer.Plugins.ZCash",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AccountIndex = table.Column<int>(type: "integer", nullable: false),
                    AddressIndex = table.Column<int>(type: "integer", nullable: false),
                    UnifiedAddress = table.Column<string>(type: "text", nullable: false),
                    TransparentAddress = table.Column<string>(type: "text", nullable: true),
                    SaplingAddress = table.Column<string>(type: "text", nullable: true),
                    OrchardAddress = table.Column<string>(type: "text", nullable: true),
                    InvoiceId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Receivers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Receivers_InvoiceId",
                schema: "BTCPayServer.Plugins.ZCash",
                table: "Receivers",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Receivers_OrchardAddress",
                schema: "BTCPayServer.Plugins.ZCash",
                table: "Receivers",
                column: "OrchardAddress");

            migrationBuilder.CreateIndex(
                name: "IX_Receivers_SaplingAddress",
                schema: "BTCPayServer.Plugins.ZCash",
                table: "Receivers",
                column: "SaplingAddress");

            migrationBuilder.CreateIndex(
                name: "IX_Receivers_TransparentAddress",
                schema: "BTCPayServer.Plugins.ZCash",
                table: "Receivers",
                column: "TransparentAddress");

            migrationBuilder.CreateIndex(
                name: "IX_Receivers_UnifiedAddress",
                schema: "BTCPayServer.Plugins.ZCash",
                table: "Receivers",
                column: "UnifiedAddress",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Receivers",
                schema: "BTCPayServer.Plugins.ZCash");
        }
    }
}
