using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicationOnCallRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OnCallGroupId",
                table: "SourceApplications",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "PushDeliveryRecipients",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "PushDeliveryRecipients",
                type: "varchar(320)",
                maxLength: 320,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "SystemKey",
                table: "PushChannels",
                type: "varchar(40)",
                maxLength: 40,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "OceDisplayName",
                table: "Incidents",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "OceEmail",
                table: "Incidents",
                type: "varchar(320)",
                maxLength: 320,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "OceNotifiedAtUtc",
                table: "Incidents",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OceUserId",
                table: "Incidents",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<Guid>(
                name: "OnCallGroupId",
                table: "Incidents",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "OnCallShiftId",
                table: "Incidents",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.CreateTable(
                name: "AlertRoutingFailures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ApplicationId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    MessageId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ReasonCode = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reason = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertRoutingFailures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertRoutingFailures_ReportedMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "ReportedMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AlertRoutingFailures_SourceApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "SourceApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_SourceApplications_OnCallGroupId",
                table: "SourceApplications",
                column: "OnCallGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_PushChannels_SystemKey",
                table: "PushChannels",
                column: "SystemKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_OnCallGroupId_OceUserId",
                table: "Incidents",
                columns: new[] { "OnCallGroupId", "OceUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertRoutingFailures_ApplicationId_CreatedAtUtc",
                table: "AlertRoutingFailures",
                columns: new[] { "ApplicationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertRoutingFailures_MessageId",
                table: "AlertRoutingFailures",
                column: "MessageId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_SourceApplications_OnCallGroups_OnCallGroupId",
                table: "SourceApplications",
                column: "OnCallGroupId",
                principalTable: "OnCallGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SourceApplications_OnCallGroups_OnCallGroupId",
                table: "SourceApplications");

            migrationBuilder.DropTable(
                name: "AlertRoutingFailures");

            migrationBuilder.DropIndex(
                name: "IX_SourceApplications_OnCallGroupId",
                table: "SourceApplications");

            migrationBuilder.DropIndex(
                name: "IX_PushChannels_SystemKey",
                table: "PushChannels");

            migrationBuilder.DropIndex(
                name: "IX_Incidents_OnCallGroupId_OceUserId",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "OnCallGroupId",
                table: "SourceApplications");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "PushDeliveryRecipients");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "PushDeliveryRecipients");

            migrationBuilder.DropColumn(
                name: "SystemKey",
                table: "PushChannels");

            migrationBuilder.DropColumn(
                name: "OceDisplayName",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "OceEmail",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "OceNotifiedAtUtc",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "OceUserId",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "OnCallGroupId",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "OnCallShiftId",
                table: "Incidents");
        }
    }
}
