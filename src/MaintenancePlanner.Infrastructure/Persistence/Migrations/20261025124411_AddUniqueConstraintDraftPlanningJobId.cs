using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintenancePlanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueConstraintDraftPlanningJobId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Drafts_PlanningJobId",
                table: "Drafts");

            migrationBuilder.CreateIndex(
                name: "IX_Drafts_PlanningJobId",
                table: "Drafts",
                column: "PlanningJobId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Drafts_PlanningJobId",
                table: "Drafts");

            migrationBuilder.CreateIndex(
                name: "IX_Drafts_PlanningJobId",
                table: "Drafts",
                column: "PlanningJobId");
        }
    }
}
