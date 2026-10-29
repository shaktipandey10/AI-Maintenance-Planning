using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintenancePlanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDraftTaskProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DraftTaskProgress",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DraftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DraftTaskProgress", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DraftTaskProgress_Drafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "Drafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DraftTaskProgress_DraftId_TaskId",
                table: "DraftTaskProgress",
                columns: new[] { "DraftId", "TaskId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DraftTaskProgress");
        }
    }
}
