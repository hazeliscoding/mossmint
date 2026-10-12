using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mossmint.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class ForecastJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ForecastJournal",
                columns: table => new
                {
                    MadeOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EntryJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForecastJournal", x => x.MadeOn);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ForecastJournal");
        }
    }
}
