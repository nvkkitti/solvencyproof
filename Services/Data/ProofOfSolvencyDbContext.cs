using Microsoft.EntityFrameworkCore;
using SolvencyProof.Models;

namespace SolvencyProof.Data
{
    public class ProofOfSolvencyDbContext : DbContext
    {
        public ProofOfSolvencyDbContext(DbContextOptions<ProofOfSolvencyDbContext> options)
            : base(options)
        {
        }

        public DbSet<Account> Accounts { get; set; }
        public DbSet<SolvencyAudit> SolvencyAudits { get; set; }
        public DbSet<CryptographicSecret> CryptographicSecrets { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configure one-to-one relationship between SolvencyAudit and CryptographicSecret
            modelBuilder.Entity<SolvencyAudit>()
                .HasOne(a => a.Secret)
                .WithOne(s => s.Audit)
                .HasForeignKey<CryptographicSecret>(s => s.SolvencyAuditId);

            modelBuilder.Entity<Account>()
                .Property(a => a.Balance)
                .HasPrecision(28, 8);

            modelBuilder.Entity<SolvencyAudit>()
                .Property(a => a.TotalLiabilities)
                .HasPrecision(28, 8);

            modelBuilder.Entity<SolvencyAudit>()
                .Property(a => a.TotalAssets)
                .HasPrecision(28, 8);
        }
    }
}
