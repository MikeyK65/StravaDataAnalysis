using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StravaReports.Core.Archive.Migrations
{
    /// <inheritdoc />
    public partial class InitialArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArchivedActivities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ActivityId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ActivityType = table.Column<string>(type: "TEXT", nullable: false),
                    SportType = table.Column<string>(type: "TEXT", nullable: false),
                    StartDateUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    DistanceM = table.Column<double>(type: "REAL", nullable: true),
                    MovingTimeS = table.Column<int>(type: "INTEGER", nullable: true),
                    ElapsedTimeS = table.Column<int>(type: "INTEGER", nullable: true),
                    ElevationGainM = table.Column<double>(type: "REAL", nullable: true),
                    MaxSpeed = table.Column<double>(type: "REAL", nullable: true),
                    AvgSpeed = table.Column<double>(type: "REAL", nullable: true),
                    AvgHr = table.Column<double>(type: "REAL", nullable: true),
                    MaxHr = table.Column<double>(type: "REAL", nullable: true),
                    AvgWatts = table.Column<double>(type: "REAL", nullable: true),
                    WeightedAvgWatts = table.Column<double>(type: "REAL", nullable: true),
                    AvgCadence = table.Column<double>(type: "REAL", nullable: true),
                    Calories = table.Column<double>(type: "REAL", nullable: true),
                    RelativeEffort = table.Column<double>(type: "REAL", nullable: true),
                    IsCommute = table.Column<bool>(type: "INTEGER", nullable: true),
                    GearName = table.Column<string>(type: "TEXT", nullable: true),
                    Filename = table.Column<string>(type: "TEXT", nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false),
                    RowHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FirstBatchId = table.Column<int>(type: "INTEGER", nullable: false),
                    LastBatchId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchivedActivities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImportBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RowsRead = table.Column<int>(type: "INTEGER", nullable: false),
                    Inserted = table.Column<int>(type: "INTEGER", nullable: false),
                    Updated = table.Column<int>(type: "INTEGER", nullable: false),
                    Unchanged = table.Column<int>(type: "INTEGER", nullable: false),
                    Skipped = table.Column<int>(type: "INTEGER", nullable: false),
                    DuplicatesInFile = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportBatches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArchivedActivities_ActivityId",
                table: "ArchivedActivities",
                column: "ActivityId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArchivedActivities_SportType",
                table: "ArchivedActivities",
                column: "SportType");

            migrationBuilder.CreateIndex(
                name: "IX_ArchivedActivities_StartDateUtc",
                table: "ArchivedActivities",
                column: "StartDateUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchivedActivities");

            migrationBuilder.DropTable(
                name: "ImportBatches");
        }
    }
}
