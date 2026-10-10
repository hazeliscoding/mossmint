using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mossmint.Persistence.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManualLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PaysBillId",
                table: "Transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PaysNominal",
                table: "Transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RecordedAt",
                table: "Transactions",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PaysBillId",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "PaysNominal",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "RecordedAt",
                table: "Transactions");
        }
    }
}
