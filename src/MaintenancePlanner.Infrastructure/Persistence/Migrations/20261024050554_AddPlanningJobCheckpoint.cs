using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintenancePlanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanningJobCheckpoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CheckpointPayload",
                table: "PlanningJobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckpointPayload",
                table: "PlanningJobs");
        }
    }
}
