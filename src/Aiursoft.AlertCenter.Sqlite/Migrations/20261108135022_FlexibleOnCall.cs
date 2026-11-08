using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class FlexibleOnCall : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OnCallShifts_GroupId_StartsAtUtc",
                table: "OnCallShifts");

            migrationBuilder.AddColumn<long>(
                name: "RotationPosition",
                table: "OnCallShifts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "OnCallShifts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ShiftMinutes",
                table: "OnCallRules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StaffPerShift",
                table: "OnCallRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "SwapAlternateRounds",
                table: "OnCallRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AutoExtend",
                table: "OnCallGroups",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "MaximumContinuousHours",
                table: "OnCallGroups",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MinimumRestHours",
                table: "OnCallGroups",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinimumShiftStaff",
                table: "OnCallGroups",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HandoverContent",
                table: "OnCallEmailSnapshots",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HandoverKey",
                table: "OnCallEmailSnapshots",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsFallback",
                table: "OnCallEmailSnapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OnCallSubscriptionTokenHash",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OnCallFallbackRecipient",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallFallbackRecipient", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_OnCallFallbackRecipient_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OnCallFallbackRecipient_OnCallGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "OnCallGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OnCallHandoverPoint",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DayOfWeek = table.Column<int>(type: "INTEGER", nullable: false),
                    MinuteOfDay = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallHandoverPoint", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnCallHandoverPoint_OnCallRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "OnCallRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShifts_GroupId_StartsAtUtc",
                table: "OnCallShifts",
                columns: new[] { "GroupId", "StartsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OnCallEmailSnapshots_HandoverKey",
                table: "OnCallEmailSnapshots",
                column: "HandoverKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnCallFallbackRecipient_UserId",
                table: "OnCallFallbackRecipient",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallHandoverPoint_RuleId",
                table: "OnCallHandoverPoint",
                column: "RuleId");
            migrationBuilder.Sql("UPDATE OnCallRules SET StaffPerShift = COALESCE(TargetStaff, 1), SwapAlternateRounds = BalanceShiftTimes;");
            migrationBuilder.Sql("UPDATE OnCallGroups SET MinimumRestHours = (SELECT RestHours FROM OnCallRules WHERE Id = OnCallGroups.RuleId), MinimumShiftStaff = (SELECT MinimumStaff FROM OnCallRules WHERE Id = OnCallGroups.RuleId);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OnCallFallbackRecipient");

            migrationBuilder.DropTable(
                name: "OnCallHandoverPoint");

            migrationBuilder.DropIndex(
                name: "IX_OnCallShifts_GroupId_StartsAtUtc",
                table: "OnCallShifts");

            migrationBuilder.DropIndex(
                name: "IX_OnCallEmailSnapshots_HandoverKey",
                table: "OnCallEmailSnapshots");

            migrationBuilder.DropColumn(
                name: "RotationPosition",
                table: "OnCallShifts");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "OnCallShifts");

            migrationBuilder.DropColumn(
                name: "ShiftMinutes",
                table: "OnCallRules");

            migrationBuilder.DropColumn(
                name: "StaffPerShift",
                table: "OnCallRules");

            migrationBuilder.DropColumn(
                name: "SwapAlternateRounds",
                table: "OnCallRules");

            migrationBuilder.DropColumn(
                name: "AutoExtend",
                table: "OnCallGroups");

            migrationBuilder.DropColumn(
                name: "MaximumContinuousHours",
                table: "OnCallGroups");

            migrationBuilder.DropColumn(
                name: "MinimumRestHours",
                table: "OnCallGroups");

            migrationBuilder.DropColumn(
                name: "MinimumShiftStaff",
                table: "OnCallGroups");

            migrationBuilder.DropColumn(
                name: "HandoverContent",
                table: "OnCallEmailSnapshots");

            migrationBuilder.DropColumn(
                name: "HandoverKey",
                table: "OnCallEmailSnapshots");

            migrationBuilder.DropColumn(
                name: "IsFallback",
                table: "OnCallEmailSnapshots");

            migrationBuilder.DropColumn(
                name: "OnCallSubscriptionTokenHash",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShifts_GroupId_StartsAtUtc",
                table: "OnCallShifts",
                columns: new[] { "GroupId", "StartsAtUtc" },
                unique: true);
        }
    }
}
