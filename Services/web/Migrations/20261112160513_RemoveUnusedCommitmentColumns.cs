using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SolvencyProof.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUnusedCommitmentColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommitmentCDelta",
                table: "SolvencyAudit");

            migrationBuilder.DropColumn(
                name: "CommitmentCt",
                table: "SolvencyAudit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "CommitmentCDelta",
                table: "SolvencyAudit",
                type: "varbinary(max)",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "CommitmentCt",
                table: "SolvencyAudit",
                type: "varbinary(max)",
                nullable: false,
                defaultValue: new byte[0]);
        }
    }
}
