using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.Sqlite.Migrations
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
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "PushDeliveryRecipients",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "PushDeliveryRecipients",
                type: "TEXT",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SystemKey",
                table: "PushChannels",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OceDisplayName",
                table: "Incidents",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OceEmail",
                table: "Incidents",
                type: "TEXT",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OceNotifiedAtUtc",
                table: "Incidents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OceUserId",
                table: "Incidents",
                type: "TEXT",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OnCallGroupId",
                table: "Incidents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OnCallShiftId",
                table: "Incidents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AlertRoutingFailures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                });

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
