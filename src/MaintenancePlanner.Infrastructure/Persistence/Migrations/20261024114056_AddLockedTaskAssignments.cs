using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintenancePlanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLockedTaskAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LockedTaskAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DraftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", nullable: false),
                    LockedTechnicianId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LockedTaskAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LockedTaskAssignments_Drafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "Drafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LockedTaskAssignments_DraftId_TaskId",
                table: "LockedTaskAssignments",
                columns: new[] { "DraftId", "TaskId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LockedTaskAssignments");
        }
    }
}
