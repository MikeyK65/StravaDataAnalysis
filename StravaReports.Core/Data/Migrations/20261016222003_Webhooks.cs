using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StravaReports.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Webhooks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebhookEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ObjectType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ObjectId = table.Column<long>(type: "INTEGER", nullable: false),
                    AspectType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    OwnerId = table.Column<long>(type: "INTEGER", nullable: false),
                    SubscriptionId = table.Column<long>(type: "INTEGER", nullable: false),
                    EventTime = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatesJson = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ReceivedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProcessedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEvents_Status_Id",
                table: "WebhookEvents",
                columns: new[] { "Status", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebhookEvents");
        }
    }
}
