using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.MySql.Migrations
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
                type: "varchar(220)",
                maxLength: 220,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PushDeliveryRecipients",
                columns: table => new
                {
                    DeliveryId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
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
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.Sql("""
                UPDATE Incidents AS target
                INNER JOIN (
                    SELECT Id, MatchKey
                    FROM (
                        SELECT Id,
                               CONCAT(
                                   CASE
                                       WHEN IncidentKey IS NULL OR TRIM(IncidentKey) = '' THEN 'alert:'
                                       ELSE 'key:'
                                   END,
                                   CASE
                                       WHEN IncidentKey IS NULL OR TRIM(IncidentKey) = '' THEN CONCAT(AlertTitle, CHAR(31), AlertLevel)
                                       ELSE IncidentKey
                                   END
                               ) AS MatchKey,
                               ROW_NUMBER() OVER (
                                   PARTITION BY ApplicationId,
                                   CASE
                                       WHEN IncidentKey IS NULL OR TRIM(IncidentKey) = '' THEN CONCAT('alert:', AlertTitle, CHAR(31), AlertLevel)
                                       ELSE CONCAT('key:', IncidentKey)
                                   END
                                   ORDER BY TriggeredAtUtc DESC, Id DESC
                               ) AS RowNumber
                        FROM Incidents
                        WHERE Status <> 'resolved'
                    ) AS ranked
                    WHERE RowNumber = 1
                ) AS active ON active.Id = target.Id
                SET target.ActiveMatchKey = active.MatchKey;
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
