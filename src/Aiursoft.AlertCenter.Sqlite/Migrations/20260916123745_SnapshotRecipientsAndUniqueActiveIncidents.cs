using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class SnapshotRecipientsAndUniqueActiveIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActiveMatchKey",
                table: "Incidents",
                type: "TEXT",
                maxLength: 220,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PushDeliveryRecipients",
                columns: table => new
                {
                    DeliveryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushDeliveryRecipients", x => new { x.DeliveryId, x.UserId });
                    table.ForeignKey(
                        name: "FK_PushDeliveryRecipients_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PushDeliveryRecipients_PushDeliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalTable: "PushDeliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                WITH RankedIncidents AS (
                    SELECT Id,
                           ROW_NUMBER() OVER (
                               PARTITION BY ApplicationId,
                               CASE
                                   WHEN IncidentKey IS NULL OR trim(IncidentKey) = '' THEN 'alert:' || AlertTitle || char(31) || AlertLevel
                                   ELSE 'key:' || IncidentKey
                               END
                               ORDER BY TriggeredAtUtc DESC, Id DESC
                           ) AS RowNumber,
                           CASE
                               WHEN IncidentKey IS NULL OR trim(IncidentKey) = '' THEN 'alert:' || AlertTitle || char(31) || AlertLevel
                               ELSE 'key:' || IncidentKey
                           END AS MatchKey
                    FROM Incidents
                    WHERE Status <> 'resolved'
                )
                UPDATE Incidents
                SET ActiveMatchKey = (
                    SELECT MatchKey FROM RankedIncidents WHERE RankedIncidents.Id = Incidents.Id
                )
                WHERE Id IN (SELECT Id FROM RankedIncidents WHERE RowNumber = 1);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_ApplicationId_ActiveMatchKey",
                table: "Incidents",
                columns: new[] { "ApplicationId", "ActiveMatchKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushDeliveryRecipients_UserId",
                table: "PushDeliveryRecipients",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PushDeliveryRecipients");

            migrationBuilder.DropIndex(
                name: "IX_Incidents_ApplicationId_ActiveMatchKey",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "ActiveMatchKey",
                table: "Incidents");
        }
    }
}
