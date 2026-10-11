using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mossmint.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class DebtPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DebtCustomJson",
                table: "PlanSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DebtExtraMinor",
                table: "PlanSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DebtOrder",
                table: "PlanSettings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DebtCustomJson",
                table: "PlanSettings");

            migrationBuilder.DropColumn(
                name: "DebtExtraMinor",
                table: "PlanSettings");

            migrationBuilder.DropColumn(
                name: "DebtOrder",
                table: "PlanSettings");
        }
    }
}
