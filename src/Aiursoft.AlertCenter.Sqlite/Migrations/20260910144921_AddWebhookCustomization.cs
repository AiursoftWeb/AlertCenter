using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookCustomization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WebhookHeadersProtected",
                table: "PushChannels",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhookPayloadTemplate",
                table: "PushChannels",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WebhookHeadersProtected",
                table: "PushChannels");

            migrationBuilder.DropColumn(
                name: "WebhookPayloadTemplate",
                table: "PushChannels");
        }
    }
}
