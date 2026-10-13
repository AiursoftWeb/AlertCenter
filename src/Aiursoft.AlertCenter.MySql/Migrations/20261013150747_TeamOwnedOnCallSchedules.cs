using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.MySql.Migrations
{
    /// <inheritdoc />
    public partial class TeamOwnedOnCallSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 开发阶段直接切换最终模型，不迁移旧规则所属的成员和班次。
            migrationBuilder.Sql("DELETE FROM OnCallShiftMembers;");
            migrationBuilder.Sql("DELETE FROM OnCallShifts;");
            migrationBuilder.Sql("DELETE FROM OnCallRuleMembers;");
            migrationBuilder.Sql("DELETE FROM OnCallRules;");
            migrationBuilder.Sql("DELETE FROM OnCallGroups;");

            migrationBuilder.DropForeignKey(
                name: "FK_OnCallRules_OnCallGroups_GroupId",
                table: "OnCallRules");

            migrationBuilder.DropForeignKey(
                name: "FK_OnCallShifts_OnCallRules_RuleId",
                table: "OnCallShifts");

            migrationBuilder.DropTable(
                name: "OnCallRuleMembers");

            migrationBuilder.DropIndex(
                name: "IX_OnCallRules_GroupId_Name",
                table: "OnCallRules");

            migrationBuilder.DropColumn(
                name: "AnchorUtc",
                table: "OnCallRules");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "OnCallRules");

            migrationBuilder.DropColumn(
                name: "ScheduleVersion",
                table: "OnCallRules");

            migrationBuilder.RenameColumn(
                name: "RuleId",
                table: "OnCallShifts",
                newName: "GroupId");

            migrationBuilder.RenameIndex(
                name: "IX_OnCallShifts_RuleId_StartsAtUtc",
                table: "OnCallShifts",
                newName: "IX_OnCallShifts_GroupId_StartsAtUtc");

            migrationBuilder.AddColumn<int>(
                name: "RestHours",
                table: "OnCallShifts",
                type: "int",
                nullable: false,
                defaultValue: 16);

            migrationBuilder.AddColumn<DateTime>(
                name: "AnchorUtc",
                table: "OnCallGroups",
                type: "datetime(6)",
                nullable: false,
                defaultValue: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.AddColumn<Guid>(
                name: "RuleId",
                table: "OnCallGroups",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "ScheduleVersion",
                table: "OnCallGroups",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            migrationBuilder.CreateTable(
                name: "OnCallGroupMembers",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RotationOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallGroupMembers", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_OnCallGroupMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnCallGroupMembers_OnCallGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "OnCallGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallGroups_RuleId",
                table: "OnCallGroups",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallGroupMembers_GroupId_RotationOrder",
                table: "OnCallGroupMembers",
                columns: new[] { "GroupId", "RotationOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnCallGroupMembers_UserId",
                table: "OnCallGroupMembers",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_OnCallGroups_OnCallRules_RuleId",
                table: "OnCallGroups",
                column: "RuleId",
                principalTable: "OnCallRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OnCallShifts_OnCallGroups_GroupId",
                table: "OnCallShifts",
                column: "GroupId",
                principalTable: "OnCallGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 回退只恢复旧结构，同样不转换新模型的值班数据。
            migrationBuilder.Sql("DELETE FROM OnCallShiftMembers;");
            migrationBuilder.Sql("DELETE FROM OnCallShifts;");
            migrationBuilder.Sql("DELETE FROM OnCallGroupMembers;");
            migrationBuilder.Sql("DELETE FROM OnCallGroups;");
            migrationBuilder.Sql("DELETE FROM OnCallRules;");

            migrationBuilder.DropForeignKey(
                name: "FK_OnCallGroups_OnCallRules_RuleId",
                table: "OnCallGroups");

            migrationBuilder.DropForeignKey(
                name: "FK_OnCallShifts_OnCallGroups_GroupId",
                table: "OnCallShifts");

            migrationBuilder.DropTable(
                name: "OnCallGroupMembers");

            migrationBuilder.DropIndex(
                name: "IX_OnCallGroups_RuleId",
                table: "OnCallGroups");

            migrationBuilder.DropColumn(
                name: "RestHours",
                table: "OnCallShifts");

            migrationBuilder.DropColumn(
                name: "AnchorUtc",
                table: "OnCallGroups");

            migrationBuilder.DropColumn(
                name: "RuleId",
                table: "OnCallGroups");

            migrationBuilder.DropColumn(
                name: "ScheduleVersion",
                table: "OnCallGroups");

            migrationBuilder.RenameColumn(
                name: "GroupId",
                table: "OnCallShifts",
                newName: "RuleId");

            migrationBuilder.RenameIndex(
                name: "IX_OnCallShifts_GroupId_StartsAtUtc",
                table: "OnCallShifts",
                newName: "IX_OnCallShifts_RuleId_StartsAtUtc");

            migrationBuilder.AddColumn<DateTime>(
                name: "AnchorUtc",
                table: "OnCallRules",
                type: "datetime(6)",
                nullable: false,
                defaultValue: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "OnCallRules",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "ScheduleVersion",
                table: "OnCallRules",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci");

            migrationBuilder.CreateTable(
                name: "OnCallRuleMembers",
                columns: table => new
                {
                    RuleId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    UserId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RotationOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnCallRuleMembers", x => new { x.RuleId, x.UserId });
                    table.ForeignKey(
                        name: "FK_OnCallRuleMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnCallRuleMembers_OnCallRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "OnCallRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_OnCallRules_GroupId_Name",
                table: "OnCallRules",
                columns: new[] { "GroupId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnCallRuleMembers_RuleId_RotationOrder",
                table: "OnCallRuleMembers",
                columns: new[] { "RuleId", "RotationOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnCallRuleMembers_UserId",
                table: "OnCallRuleMembers",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_OnCallRules_OnCallGroups_GroupId",
                table: "OnCallRules",
                column: "GroupId",
                principalTable: "OnCallGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_OnCallShifts_OnCallRules_RuleId",
                table: "OnCallShifts",
                column: "RuleId",
                principalTable: "OnCallRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
