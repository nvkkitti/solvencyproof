using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SolvencyProof.Migrations
{
    /// <inheritdoc />
    public partial class AlignSolvencyProofModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CryptographicSecret_SolvencyAudit_SolvencyAuditId",
                table: "CryptographicSecret");

            migrationBuilder.DropPrimaryKey(
                name: "PK_CryptographicSecret",
                table: "CryptographicSecret");

            migrationBuilder.RenameTable(
                name: "CryptographicSecret",
                newName: "CryptographicSecrets");

            migrationBuilder.RenameIndex(
                name: "IX_CryptographicSecret_SolvencyAuditId",
                table: "CryptographicSecrets",
                newName: "IX_CryptographicSecrets_SolvencyAuditId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_CryptographicSecrets",
                table: "CryptographicSecrets",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_CryptographicSecrets_SolvencyAudit_SolvencyAuditId",
                table: "CryptographicSecrets",
                column: "SolvencyAuditId",
                principalTable: "SolvencyAudit",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CryptographicSecrets_SolvencyAudit_SolvencyAuditId",
                table: "CryptographicSecrets");

            migrationBuilder.DropPrimaryKey(
                name: "PK_CryptographicSecrets",
                table: "CryptographicSecrets");

            migrationBuilder.RenameTable(
                name: "CryptographicSecrets",
                newName: "CryptographicSecret");

            migrationBuilder.RenameIndex(
                name: "IX_CryptographicSecrets_SolvencyAuditId",
                table: "CryptographicSecret",
                newName: "IX_CryptographicSecret_SolvencyAuditId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_CryptographicSecret",
                table: "CryptographicSecret",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_CryptographicSecret_SolvencyAudit_SolvencyAuditId",
                table: "CryptographicSecret",
                column: "SolvencyAuditId",
                principalTable: "SolvencyAudit",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
