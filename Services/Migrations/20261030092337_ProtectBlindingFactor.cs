using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SolvencyProof.Migrations
{
    /// <inheritdoc />
    public partial class ProtectBlindingFactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<byte[]>(
                name: "BlindingFactorR",
                table: "CryptographicSecrets",
                type: "varbinary(max)",
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "varbinary(32)",
                oldMaxLength: 32);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<byte[]>(
                name: "BlindingFactorR",
                table: "CryptographicSecrets",
                type: "varbinary(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(byte[]),
                oldType: "varbinary(max)");
        }
    }
}
