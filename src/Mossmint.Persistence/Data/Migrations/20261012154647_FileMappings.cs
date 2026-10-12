using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mossmint.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class FileMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileMappings",
                columns: table => new
                {
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Mapping = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileMappings", x => x.Fingerprint);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileMappings");
        }
    }
}
