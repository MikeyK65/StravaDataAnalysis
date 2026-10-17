using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StravaReports.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Activities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StravaId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SportType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    StartDateUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartDateLocal = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Timezone = table.Column<string>(type: "TEXT", nullable: true),
                    DistanceM = table.Column<double>(type: "REAL", nullable: false),
                    MovingTimeS = table.Column<int>(type: "INTEGER", nullable: false),
                    ElapsedTimeS = table.Column<int>(type: "INTEGER", nullable: false),
                    ElevationGainM = table.Column<double>(type: "REAL", nullable: false),
                    AvgSpeed = table.Column<double>(type: "REAL", nullable: false),
                    MaxSpeed = table.Column<double>(type: "REAL", nullable: false),
                    AvgHr = table.Column<double>(type: "REAL", nullable: true),
                    MaxHr = table.Column<double>(type: "REAL", nullable: true),
                    AvgWatts = table.Column<double>(type: "REAL", nullable: true),
                    WeightedAvgWatts = table.Column<double>(type: "REAL", nullable: true),
                    Kilojoules = table.Column<double>(type: "REAL", nullable: true),
                    AvgCadence = table.Column<double>(type: "REAL", nullable: true),
                    DeviceWatts = table.Column<bool>(type: "INTEGER", nullable: false),
                    SufferScore = table.Column<double>(type: "REAL", nullable: true),
                    GearId = table.Column<string>(type: "TEXT", nullable: true),
                    IsTrainer = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsCommute = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsManual = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsPrivate = table.Column<bool>(type: "INTEGER", nullable: false),
                    SummaryPolyline = table.Column<string>(type: "TEXT", nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false),
                    SummaryFetchedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DetailFetchedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StreamsFetchedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Athletes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StravaId = table.Column<long>(type: "INTEGER", nullable: false),
                    FirstName = table.Column<string>(type: "TEXT", nullable: false),
                    LastName = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    MeasurementPreference = table.Column<string>(type: "TEXT", nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Athletes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Gear",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StravaId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    GearType = table.Column<string>(type: "TEXT", nullable: false),
                    BrandName = table.Column<string>(type: "TEXT", nullable: true),
                    ModelName = table.Column<string>(type: "TEXT", nullable: true),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsRetired = table.Column<bool>(type: "INTEGER", nullable: false),
                    DistanceM = table.Column<double>(type: "REAL", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Gear", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LogEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ActivityStravaId = table.Column<long>(type: "INTEGER", nullable: false),
                    JobType = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NewestActivityEpoch = table.Column<long>(type: "INTEGER", nullable: true),
                    BackfillCursorEpoch = table.Column<long>(type: "INTEGER", nullable: true),
                    BackfillComplete = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastRunStartedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRunFinishedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastStatus = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    ResumeAfterUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastReconcileUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ShortLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    ShortUsage = table.Column<int>(type: "INTEGER", nullable: true),
                    DailyLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    DailyUsage = table.Column<int>(type: "INTEGER", nullable: true),
                    RateLimitObservedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ActivityBestEfforts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ActivityId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    DistanceM = table.Column<double>(type: "REAL", nullable: false),
                    MovingTimeS = table.Column<int>(type: "INTEGER", nullable: false),
                    ElapsedTimeS = table.Column<int>(type: "INTEGER", nullable: false),
                    StartDateLocal = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PrRank = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityBestEfforts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityBestEfforts_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ActivityLaps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ActivityId = table.Column<int>(type: "INTEGER", nullable: false),
                    LapIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    DistanceM = table.Column<double>(type: "REAL", nullable: false),
                    MovingTimeS = table.Column<int>(type: "INTEGER", nullable: false),
                    ElapsedTimeS = table.Column<int>(type: "INTEGER", nullable: false),
                    AvgSpeed = table.Column<double>(type: "REAL", nullable: false),
                    AvgHr = table.Column<double>(type: "REAL", nullable: true),
                    AvgWatts = table.Column<double>(type: "REAL", nullable: true),
                    AvgCadence = table.Column<double>(type: "REAL", nullable: true),
                    ElevationGainM = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityLaps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityLaps_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ActivityStreams",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ActivityId = table.Column<int>(type: "INTEGER", nullable: false),
                    StreamType = table.Column<string>(type: "TEXT", nullable: false),
                    PointCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityStreams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityStreams_Activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "Activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_GearId",
                table: "Activities",
                column: "GearId");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_SportType_StartDateLocal",
                table: "Activities",
                columns: new[] { "SportType", "StartDateLocal" });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_StartDateUtc",
                table: "Activities",
                column: "StartDateUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_StravaId",
                table: "Activities",
                column: "StravaId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityBestEfforts_ActivityId",
                table: "ActivityBestEfforts",
                column: "ActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityBestEfforts_Name_MovingTimeS",
                table: "ActivityBestEfforts",
                columns: new[] { "Name", "MovingTimeS" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityLaps_ActivityId_LapIndex",
                table: "ActivityLaps",
                columns: new[] { "ActivityId", "LapIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivityStreams_ActivityId_StreamType",
                table: "ActivityStreams",
                columns: new[] { "ActivityId", "StreamType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Athletes_StravaId",
                table: "Athletes",
                column: "StravaId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Gear_StravaId",
                table: "Gear",
                column: "StravaId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_TimestampUtc",
                table: "LogEntries",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SyncJobs_ActivityStravaId_JobType",
                table: "SyncJobs",
                columns: new[] { "ActivityStravaId", "JobType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncJobs_Status_NextAttemptUtc",
                table: "SyncJobs",
                columns: new[] { "Status", "NextAttemptUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityBestEfforts");

            migrationBuilder.DropTable(
                name: "ActivityLaps");

            migrationBuilder.DropTable(
                name: "ActivityStreams");

            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "Athletes");

            migrationBuilder.DropTable(
                name: "Gear");

            migrationBuilder.DropTable(
                name: "LogEntries");

            migrationBuilder.DropTable(
                name: "SyncJobs");

            migrationBuilder.DropTable(
                name: "SyncStates");

            migrationBuilder.DropTable(
                name: "Activities");
        }
    }
}
