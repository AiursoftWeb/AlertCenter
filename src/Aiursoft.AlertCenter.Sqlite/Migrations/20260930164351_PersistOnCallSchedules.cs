using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class PersistOnCallSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE OnCallRules SET ShiftHours = 8;");
            migrationBuilder.AddColumn<bool>(
                name: "BalanceShiftTimes",
                table: "OnCallRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ScheduleVersion",
                table: "OnCallRules",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "OnCallShifts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallShifts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnCallShifts_OnCallRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "OnCallRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OnCallShiftMembers",
                columns: table => new
                {
                    ShiftId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallShiftMembers", x => new { x.ShiftId, x.UserId });
                    table.ForeignKey(
                        name: "FK_OnCallShiftMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnCallShiftMembers_OnCallShifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "OnCallShifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShiftMembers_UserId",
                table: "OnCallShiftMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallShifts_RuleId_StartsAtUtc",
                table: "OnCallShifts",
                columns: new[] { "RuleId", "StartsAtUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OnCallShiftMembers");

            migrationBuilder.DropTable(
                name: "OnCallShifts");

            migrationBuilder.DropColumn(
                name: "BalanceShiftTimes",
                table: "OnCallRules");

            migrationBuilder.DropColumn(
                name: "ScheduleVersion",
                table: "OnCallRules");
        }
    }
}
