using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SolvencyProof.Migrations.ProofOfSolvencyDb
{
    /// <inheritdoc />
    public partial class DataAnnotationDataSeedData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SolvencyRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Balance = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    MerkleLeafSalt = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    LastUpdate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolvencyRecords", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "SolvencyRecords",
                columns: new[] { "Id", "Balance", "LastUpdate", "MerkleLeafSalt" },
                values: new object[,]
                {
                    { new Guid("a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d"), 0.00m, new DateTime(2026, 10, 18, 9, 15, 0, 0, DateTimeKind.Unspecified), new byte[] { 1, 2, 3, 4, 5 } },
                    { new Guid("d3b07384-d113-4f9a-ba00-a5fb9d72de45"), 1500.50m, new DateTime(2026, 10, 20, 10, 0, 0, 0, DateTimeKind.Unspecified), new byte[] { 26, 43, 60, 77, 94 } },
                    { new Guid("f9a2e34b-7489-4e5a-8b1a-9c0d1e2f3a4b"), 250000.00m, new DateTime(2026, 10, 19, 14, 30, 0, 0, DateTimeKind.Unspecified), new byte[] { 255, 238, 221, 204, 187 } }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SolvencyRecords");
        }
    }
}
