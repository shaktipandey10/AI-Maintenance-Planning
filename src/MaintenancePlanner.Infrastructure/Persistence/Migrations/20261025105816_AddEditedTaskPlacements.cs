using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintenancePlanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEditedTaskPlacements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EditedTaskPlacements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DraftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalStart = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    OriginalEnd = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    OriginalTechnicianIds = table.Column<string>(type: "TEXT", nullable: false),
                    NewStart = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    NewEnd = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    NewTechnicianIds = table.Column<string>(type: "TEXT", nullable: false),
                    ValidationStatus = table.Column<string>(type: "TEXT", nullable: false),
                    ValidationViolations = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EditedTaskPlacements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EditedTaskPlacements_Drafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "Drafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EditedTaskPlacements_DraftId_TaskId",
                table: "EditedTaskPlacements",
                columns: new[] { "DraftId", "TaskId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EditedTaskPlacements");
        }
    }
}
