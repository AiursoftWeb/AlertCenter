using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddSev0PushRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReceiveSev0",
                table: "ApplicationPushChannels",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReceiveSev0",
                table: "ApplicationPushChannels");
        }
    }
}
