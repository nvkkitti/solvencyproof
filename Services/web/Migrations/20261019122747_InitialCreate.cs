using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SolvencyProof.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Account",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Balance = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    MerkleLeafSalt = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    LastUpdate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Account", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SolvencyAudit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TotalLiabilities = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalAssets = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    MerkleRoot = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CommitmentCv = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CommitmentCt = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CommitmentCDelta = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    ZkProof = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolvencyAudit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CryptographicSecret",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SolvencyAuditId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BlindingFactorR = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CryptographicSecret", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CryptographicSecret_SolvencyAudit_SolvencyAuditId",
                        column: x => x.SolvencyAuditId,
                        principalTable: "SolvencyAudit",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CryptographicSecret_SolvencyAuditId",
                table: "CryptographicSecret",
                column: "SolvencyAuditId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Account");

            migrationBuilder.DropTable(
                name: "CryptographicSecret");

            migrationBuilder.DropTable(
                name: "SolvencyAudit");
        }
    }
}
