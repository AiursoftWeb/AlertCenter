using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.Sqlite.Migrations
{
    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public partial class AddOnCallSmsSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SmsNotificationsEnabled",
                table: "OnCallGroups",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ChannelType",
                table: "OnCallEmailSnapshots",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "email");

            migrationBuilder.AddColumn<string>(
                name: "PhoneNumber",
                table: "OnCallEmailRecipient",
                type: "TEXT",
                maxLength: 32,
                nullable: true);
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
