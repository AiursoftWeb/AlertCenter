using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.MySql.Migrations
{
    /// <inheritdoc />
    public partial class OnCallStaffingRequirements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MinimumStaff",
                table: "OnCallShifts",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "MinimumStaff",
                table: "OnCallRules",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TargetStaff",
                table: "OnCallRules",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MinimumStaff",
                table: "OnCallShifts");

            migrationBuilder.DropColumn(
                name: "MinimumStaff",
                table: "OnCallRules");

            migrationBuilder.DropColumn(
                name: "TargetStaff",
                table: "OnCallRules");
        }
    }
}
