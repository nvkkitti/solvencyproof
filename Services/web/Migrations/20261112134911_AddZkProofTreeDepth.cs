using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SolvencyProof.Migrations
{
    /// <inheritdoc />
    public partial class AddZkProofTreeDepth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TreeDepth",
                table: "SolvencyAudit",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TreeDepth",
                table: "SolvencyAudit");
        }
    }
}
