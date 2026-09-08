using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddPushSeverityBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReceiveSev1",
                table: "ApplicationPushChannels",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReceiveSev2",
                table: "ApplicationPushChannels",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReceiveSev3",
                table: "ApplicationPushChannels",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReceiveSev4",
                table: "ApplicationPushChannels",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReceiveSev1",
                table: "ApplicationPushChannels");

            migrationBuilder.DropColumn(
                name: "ReceiveSev2",
                table: "ApplicationPushChannels");

            migrationBuilder.DropColumn(
                name: "ReceiveSev3",
                table: "ApplicationPushChannels");

            migrationBuilder.DropColumn(
                name: "ReceiveSev4",
                table: "ApplicationPushChannels");
        }
    }
}
