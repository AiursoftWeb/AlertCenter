using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.MySql.Migrations
{
    /// <inheritdoc />
    public partial class SplitEmailRecipientAndCcRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailRecipients",
                table: "PushChannels");

            migrationBuilder.AddColumn<string>(
                name: "EmailCcRecipients",
                table: "PushChannels",
                type: "text",
                maxLength: 8000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "EmailRecipient",
                table: "PushChannels",
                type: "varchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailCcRecipients",
                table: "PushChannels");

            migrationBuilder.DropColumn(
                name: "EmailRecipient",
                table: "PushChannels");

            migrationBuilder.AddColumn<string>(
                name: "EmailRecipients",
                table: "PushChannels",
                type: "text",
                maxLength: 8000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }
    }
}
