using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.MySql.Migrations
{
    /// <inheritdoc />
    public partial class SingleIncident : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Pre-release business history is intentionally discarded. Keep application and identity configuration.
            migrationBuilder.Sql("DELETE FROM PushDeliveries;");
            migrationBuilder.Sql("DELETE FROM ReportedMessages;");
            migrationBuilder.Sql("DELETE FROM Incidents;");
            // MySQL uses a correlation index to support this foreign key. Recreate it after the new indexes exist.
            migrationBuilder.DropForeignKey(
                name: "FK_Incidents_SourceApplications_ApplicationId",
                table: "Incidents");

            migrationBuilder.DropForeignKey(
                name: "FK_PushDeliveries_ReportedMessages_MessageId",
                table: "PushDeliveries");

            migrationBuilder.DropTable(
                name: "ReportedMessages");

            migrationBuilder.DropIndex(
                name: "IX_Incidents_ApplicationId_ActiveMatchKey",
                table: "Incidents");

            migrationBuilder.DropIndex(
                name: "IX_Incidents_ApplicationId_AlertTitle_AlertLevel_Status",
                table: "Incidents");

            migrationBuilder.DropIndex(
                name: "IX_Incidents_ApplicationId_IncidentKey",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "ActiveMatchKey",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "AlertLevel",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "AlertTitle",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "IncidentKey",
                table: "Incidents");

            migrationBuilder.RenameColumn(
                name: "MessageId",
                table: "PushDeliveries",
                newName: "IncidentId");

            migrationBuilder.RenameIndex(
                name: "IX_PushDeliveries_MessageId_ChannelId",
                table: "PushDeliveries",
                newName: "IX_PushDeliveries_IncidentId_ChannelId");

            migrationBuilder.RenameColumn(
                name: "TriggeredAtUtc",
                table: "Incidents",
                newName: "ReceivedAtUtc");

            migrationBuilder.RenameColumn(
                name: "MatchLevel",
                table: "Incidents",
                newName: "Level");

            migrationBuilder.AddColumn<string>(
                name: "Content",
                table: "Incidents",
                type: "varchar(4000)",
                maxLength: 4000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ContextJson",
                table: "Incidents",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "OccurredAtUtc",
                table: "Incidents",
                type: "datetime(6)",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "ReportId",
                table: "Incidents",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_ApplicationId_Level_Status",
                table: "Incidents",
                columns: new[] { "ApplicationId", "Level", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_ApplicationId_ReceivedAtUtc",
                table: "Incidents",
                columns: new[] { "ApplicationId", "ReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_ApplicationId_ReportId",
                table: "Incidents",
                columns: new[] { "ApplicationId", "ReportId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PushDeliveries_Incidents_IncidentId",
                table: "PushDeliveries",
                column: "IncidentId",
                principalTable: "Incidents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
            migrationBuilder.AddForeignKey(
                name: "FK_Incidents_SourceApplications_ApplicationId",
                table: "Incidents",
                column: "ApplicationId",
                principalTable: "SourceApplications",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Restore the pre-upgrade database backup to return to the previous schema.");
        }
    }
}
