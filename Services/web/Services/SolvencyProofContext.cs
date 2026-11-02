using Microsoft.EntityFrameworkCore;
using SolvencyProof.Models;

public class SolvencyProofContext(DbContextOptions<SolvencyProofContext> options) : DbContext(options)
{
    public DbSet<SolvencyProof.Models.Account> Account { get; set; } = default!;
    // DbSet for SolvencyAudit matches usage in the Razor page (context.SolvencyAudit)
    public DbSet<SolvencyAudit> SolvencyAudit { get; set; } = default!;
    public DbSet<CryptographicSecret> CryptographicSecrets { get; set; } = default!;
    public DbSet<VerificationQuery> VerificationQueries { get; set; } = default!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SolvencyAudit>()
            .HasOne(a => a.Secret)
            .WithOne(s => s.Audit)
            .HasForeignKey<CryptographicSecret>(s => s.SolvencyAuditId);

        modelBuilder.Entity<Account>().HasData(
            new Account
            {
                Id = Guid.Parse("d3b07384-d113-4f9a-ba00-a5fb9d72de45"),
                Balance = 1500.50m,
                MerkleLeafSalt = new byte[] { 0x1A, 0x2B, 0x3C, 0x4D, 0x5E },
                LastUpdate = new DateTime(2026, 10, 20, 10, 0, 0)
            },
            new Account
            {
                Id = Guid.Parse("f9a2e34b-7489-4e5a-8b1a-9c0d1e2f3a4b"),
                Balance = 250000.00m,
                MerkleLeafSalt = new byte[] { 0xFF, 0xEE, 0xDD, 0xCC, 0xBB },
                LastUpdate = new DateTime(2026, 10, 19, 14, 30, 0)
            },
            new Account
            {
                Id = Guid.Parse("a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d"),
                Balance = 0.00m,
                MerkleLeafSalt = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 },
                LastUpdate = new DateTime(2026, 10, 18, 9, 15, 0)
            });
    }
}
