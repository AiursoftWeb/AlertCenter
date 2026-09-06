using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourceApplications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SecretHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceApplications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReportedMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReportId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Content = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Level = table.Column<string>(type: "TEXT", maxLength: 4, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ContextJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportedMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportedMessages_SourceApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "SourceApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportedMessages_ApplicationId_ReportId",
                table: "ReportedMessages",
                columns: new[] { "ApplicationId", "ReportId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportedMessages_ReceivedAtUtc",
                table: "ReportedMessages",
                column: "ReceivedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReportedMessages");

            migrationBuilder.DropTable(
                name: "SourceApplications");
        }
    }
}
