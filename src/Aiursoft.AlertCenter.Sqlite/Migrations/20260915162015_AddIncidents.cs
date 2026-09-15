using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "IncidentId",
                table: "ReportedMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IncidentKey",
                table: "ReportedMessages",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "ReportedMessages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IncidentKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AlertTitle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AlertLevel = table.Column<string>(type: "TEXT", maxLength: 4, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    TriggeredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResolvedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Incidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Incidents_SourceApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "SourceApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IncidentTimelineEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IncidentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentTimelineEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentTimelineEntries_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IncidentTimelineEntries_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportedMessages_IncidentId",
                table: "ReportedMessages",
                column: "IncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_ApplicationId_AlertTitle_AlertLevel_Status",
                table: "Incidents",
                columns: new[] { "ApplicationId", "AlertTitle", "AlertLevel", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_ApplicationId_IncidentKey",
                table: "Incidents",
                columns: new[] { "ApplicationId", "IncidentKey" });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentTimelineEntries_ActorId",
                table: "IncidentTimelineEntries",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentTimelineEntries_IncidentId_CreatedAtUtc",
                table: "IncidentTimelineEntries",
                columns: new[] { "IncidentId", "CreatedAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_ReportedMessages_Incidents_IncidentId",
                table: "ReportedMessages",
                column: "IncidentId",
                principalTable: "Incidents",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ReportedMessages_Incidents_IncidentId",
                table: "ReportedMessages");

            migrationBuilder.DropTable(
                name: "IncidentTimelineEntries");

            migrationBuilder.DropTable(
                name: "Incidents");

            migrationBuilder.DropIndex(
                name: "IX_ReportedMessages_IncidentId",
                table: "ReportedMessages");

            migrationBuilder.DropColumn(
                name: "IncidentId",
                table: "ReportedMessages");

            migrationBuilder.DropColumn(
                name: "IncidentKey",
                table: "ReportedMessages");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "ReportedMessages");
        }
    }
}
