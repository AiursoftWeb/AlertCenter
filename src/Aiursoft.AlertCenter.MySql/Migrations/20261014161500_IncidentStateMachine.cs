using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.MySql.Migrations
{
    /// <inheritdoc />
    public partial class IncidentStateMachine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "IncidentEventId",
                table: "PushDeliveries",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "NewLevel",
                table: "IncidentTimelineEntries",
                type: "varchar(4)",
                maxLength: 4,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "NewOwnerId",
                table: "IncidentTimelineEntries",
                type: "varchar(450)",
                maxLength: 450,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "NewOwnerName",
                table: "IncidentTimelineEntries",
                type: "varchar(30)",
                maxLength: 30,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "NewStatus",
                table: "IncidentTimelineEntries",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "PreviousLevel",
                table: "IncidentTimelineEntries",
                type: "varchar(4)",
                maxLength: 4,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "PreviousOwnerId",
                table: "IncidentTimelineEntries",
                type: "varchar(450)",
                maxLength: 450,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "PreviousOwnerName",
                table: "IncidentTimelineEntries",
                type: "varchar(30)",
                maxLength: 30,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "PreviousStatus",
                table: "IncidentTimelineEntries",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "MatchLevel",
                table: "Incidents",
                type: "varchar(4)",
                maxLength: 4,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "MitigatedAtUtc",
                table: "Incidents",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerDisplayName",
                table: "Incidents",
                type: "varchar(30)",
                maxLength: 30,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "OwnerId",
                table: "Incidents",
                type: "varchar(450)",
                maxLength: 450,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "Incidents",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            migrationBuilder.CreateIndex(
                name: "IX_PushDeliveries_IncidentEventId",
                table: "PushDeliveries",
                column: "IncidentEventId");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_OwnerId",
                table: "Incidents",
                column: "OwnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_Incidents_AspNetUsers_OwnerId",
                table: "Incidents",
                column: "OwnerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_PushDeliveries_IncidentTimelineEntries_IncidentEventId",
                table: "PushDeliveries",
                column: "IncidentEventId",
                principalTable: "IncidentTimelineEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.Sql("UPDATE Incidents SET MatchLevel = AlertLevel, Status = CASE Status WHEN 'triggered' THEN 'new' WHEN 'acknowledged' THEN 'acked' ELSE Status END, Version = Id");
            migrationBuilder.Sql("UPDATE Incidents SET OwnerId = (SELECT ActorId FROM IncidentTimelineEntries WHERE IncidentId = Incidents.Id AND Type = 'acknowledged' AND ActorId IS NOT NULL ORDER BY CreatedAtUtc DESC LIMIT 1) WHERE Status = 'acked'");
            migrationBuilder.Sql("UPDATE Incidents JOIN AspNetUsers ON AspNetUsers.Id = Incidents.OwnerId SET Incidents.OwnerDisplayName = AspNetUsers.DisplayName");
            migrationBuilder.Sql("UPDATE IncidentTimelineEntries SET Type = CASE Type WHEN 'triggered' THEN 'created' WHEN 'acknowledged' THEN 'acked' ELSE Type END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM PushDeliveries WHERE IncidentEventId IS NOT NULL");
            migrationBuilder.Sql("UPDATE Incidents SET Status = CASE Status WHEN 'new' THEN 'triggered' WHEN 'acked' THEN 'acknowledged' WHEN 'mitigated' THEN 'acknowledged' ELSE Status END");
            migrationBuilder.Sql("UPDATE IncidentTimelineEntries SET Type = CASE Type WHEN 'created' THEN 'triggered' WHEN 'acked' THEN 'acknowledged' WHEN 'ownership_changed' THEN 'updated' WHEN 'mitigated' THEN 'updated' WHEN 'reopened' THEN 'updated' WHEN 'level_changed' THEN 'updated' ELSE Type END");
            migrationBuilder.DropForeignKey(
                name: "FK_Incidents_AspNetUsers_OwnerId",
                table: "Incidents");

            migrationBuilder.DropForeignKey(
                name: "FK_PushDeliveries_IncidentTimelineEntries_IncidentEventId",
                table: "PushDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_PushDeliveries_IncidentEventId",
                table: "PushDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_Incidents_OwnerId",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "IncidentEventId",
                table: "PushDeliveries");

            migrationBuilder.DropColumn(
                name: "NewLevel",
                table: "IncidentTimelineEntries");

            migrationBuilder.DropColumn(
                name: "NewOwnerId",
                table: "IncidentTimelineEntries");

            migrationBuilder.DropColumn(
                name: "NewOwnerName",
                table: "IncidentTimelineEntries");

            migrationBuilder.DropColumn(
                name: "NewStatus",
                table: "IncidentTimelineEntries");

            migrationBuilder.DropColumn(
                name: "PreviousLevel",
                table: "IncidentTimelineEntries");

            migrationBuilder.DropColumn(
                name: "PreviousOwnerId",
                table: "IncidentTimelineEntries");

            migrationBuilder.DropColumn(
                name: "PreviousOwnerName",
                table: "IncidentTimelineEntries");

            migrationBuilder.DropColumn(
                name: "PreviousStatus",
                table: "IncidentTimelineEntries");

            migrationBuilder.DropColumn(
                name: "MatchLevel",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "MitigatedAtUtc",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "OwnerDisplayName",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Incidents");
        }
    }
}
