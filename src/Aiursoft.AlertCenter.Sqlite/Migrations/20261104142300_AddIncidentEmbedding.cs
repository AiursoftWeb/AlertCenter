using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddIncidentEmbedding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "Embedding",
                table: "Incidents",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastEmbeddedAt",
                table: "Incidents",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateTable(
                name: "SearchEmbeddings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    QueryText = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Embedding = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastAccessedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchEmbeddings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SearchEmbeddings_QueryText",
                table: "SearchEmbeddings",
                column: "QueryText",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SearchEmbeddings");

            migrationBuilder.DropColumn(
                name: "Embedding",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "LastEmbeddedAt",
                table: "Incidents");
        }
    }
}
