using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintenancePlanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessActionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BusinessActionRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    PerformedBy = table.Column<string>(type: "TEXT", nullable: false),
                    PerformedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PlanningJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DraftId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TaskId = table.Column<string>(type: "TEXT", nullable: true),
                    Details = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessActionRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessActionRecords_DraftId_PerformedAtUtc",
                table: "BusinessActionRecords",
                columns: new[] { "DraftId", "PerformedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessActionRecords_PlanningJobId_PerformedAtUtc",
                table: "BusinessActionRecords",
                columns: new[] { "PlanningJobId", "PerformedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessActionRecords");
        }
    }
}
