using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintenancePlanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPartAssetCompatibilityAndHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaintenanceHistoryRecords",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    AssetId = table.Column<string>(type: "TEXT", nullable: false),
                    PerformedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    SourceDocumentId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceReference = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceHistoryRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceHistoryRecords_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SparePartAssetCompatibility",
                columns: table => new
                {
                    SparePartId = table.Column<string>(type: "TEXT", nullable: false),
                    AssetId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SparePartAssetCompatibility", x => new { x.SparePartId, x.AssetId });
                    table.ForeignKey(
                        name: "FK_SparePartAssetCompatibility_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SparePartAssetCompatibility_SpareParts_SparePartId",
                        column: x => x.SparePartId,
                        principalTable: "SpareParts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceHistoryRecords_AssetId_PerformedAt",
                table: "MaintenanceHistoryRecords",
                columns: new[] { "AssetId", "PerformedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SparePartAssetCompatibility_AssetId_SparePartId",
                table: "SparePartAssetCompatibility",
                columns: new[] { "AssetId", "SparePartId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaintenanceHistoryRecords");

            migrationBuilder.DropTable(
                name: "SparePartAssetCompatibility");
        }
    }
}
