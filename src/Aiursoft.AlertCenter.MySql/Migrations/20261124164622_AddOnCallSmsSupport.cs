using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddOnCallSmsSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SmsNotificationsEnabled",
                table: "OnCallGroups",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ChannelType",
                table: "OnCallEmailSnapshots",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "email")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "PhoneNumber",
                table: "OnCallEmailRecipient",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SmsNotificationsEnabled",
                table: "OnCallGroups");

            migrationBuilder.DropColumn(
                name: "ChannelType",
                table: "OnCallEmailSnapshots");

            migrationBuilder.DropColumn(
                name: "PhoneNumber",
                table: "OnCallEmailRecipient");
        }
    }
}
