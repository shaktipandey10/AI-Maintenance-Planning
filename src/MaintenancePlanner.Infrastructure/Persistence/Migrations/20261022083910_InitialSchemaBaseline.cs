using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintenancePlanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchemaBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Assets",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PlantId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceWindows",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Start = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    End = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceWindows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SpareParts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Unit = table.Column<string>(type: "TEXT", nullable: false),
                    OnHandQuantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    ReservedQuantity = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpareParts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Technicians",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    HourlyRate = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Technicians", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceTaskDefinitions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    AssetId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    DurationMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    RequiredSkills = table.Column<string>(type: "TEXT", nullable: false),
                    RequiredCrewSize = table.Column<int>(type: "INTEGER", nullable: false),
                    RequiredPartId = table.Column<string>(type: "TEXT", nullable: true),
                    RequiredPartQuantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    PredecessorTaskId = table.Column<string>(type: "TEXT", nullable: true),
                    SourceDocumentId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceSection = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceTaskDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceTaskDefinitions_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaintenanceTaskDefinitions_MaintenanceTaskDefinitions_PredecessorTaskId",
                        column: x => x.PredecessorTaskId,
                        principalTable: "MaintenanceTaskDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaintenanceTaskDefinitions_SpareParts_RequiredPartId",
                        column: x => x.RequiredPartId,
                        principalTable: "SpareParts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TechnicalSkills",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TechnicianId = table.Column<string>(type: "TEXT", nullable: false),
                    SkillCode = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TechnicalSkills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TechnicalSkills_Technicians_TechnicianId",
                        column: x => x.TechnicianId,
                        principalTable: "Technicians",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TechnicianAvailabilities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TechnicianId = table.Column<string>(type: "TEXT", nullable: false),
                    AvailableFrom = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AvailableTo = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TechnicianAvailabilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TechnicianAvailabilities_Technicians_TechnicianId",
                        column: x => x.TechnicianId,
                        principalTable: "Technicians",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceTaskDefinitions_AssetId",
                table: "MaintenanceTaskDefinitions",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceTaskDefinitions_PredecessorTaskId",
                table: "MaintenanceTaskDefinitions",
                column: "PredecessorTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceTaskDefinitions_RequiredPartId",
                table: "MaintenanceTaskDefinitions",
                column: "RequiredPartId");

            migrationBuilder.CreateIndex(
                name: "IX_TechnicalSkills_TechnicianId",
                table: "TechnicalSkills",
                column: "TechnicianId");

            migrationBuilder.CreateIndex(
                name: "IX_TechnicianAvailabilities_TechnicianId",
                table: "TechnicianAvailabilities",
                column: "TechnicianId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaintenanceTaskDefinitions");

            migrationBuilder.DropTable(
                name: "MaintenanceWindows");

            migrationBuilder.DropTable(
                name: "TechnicalSkills");

            migrationBuilder.DropTable(
                name: "TechnicianAvailabilities");

            migrationBuilder.DropTable(
                name: "Assets");

            migrationBuilder.DropTable(
                name: "SpareParts");

            migrationBuilder.DropTable(
                name: "Technicians");
        }
    }
}
