using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintenancePlanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanningJobExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "PlanningJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CurrentStep",
                table: "PlanningJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: "Queued");

            migrationBuilder.AddColumn<string>(
                name: "FailureCode",
                table: "PlanningJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LeaseExpiresAtUnixMilliseconds",
                table: "PlanningJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxAttempts",
                table: "PlanningJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<string>(
                name: "ResultPayload",
                table: "PlanningJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkerLeaseToken",
                table: "PlanningJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlanningJobs_Status_CreatedAtUtc",
                table: "PlanningJobs",
                columns: new[] { "Status", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlanningJobs_Status_CreatedAtUtc",
                table: "PlanningJobs");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "PlanningJobs");

            migrationBuilder.DropColumn(
                name: "CurrentStep",
                table: "PlanningJobs");

            migrationBuilder.DropColumn(
                name: "FailureCode",
                table: "PlanningJobs");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresAtUnixMilliseconds",
                table: "PlanningJobs");

            migrationBuilder.DropColumn(
                name: "MaxAttempts",
                table: "PlanningJobs");

            migrationBuilder.DropColumn(
                name: "ResultPayload",
                table: "PlanningJobs");

            migrationBuilder.DropColumn(
                name: "WorkerLeaseToken",
                table: "PlanningJobs");
        }
    }
}
