using Microsoft.EntityFrameworkCore;
using SolvencyProof.Models;

public class SolvencyProofContext(DbContextOptions<SolvencyProofContext> options) : DbContext(options)
{
    public DbSet<SolvencyProof.Models.Account> Account { get; set; } = default!;
    // DbSet for SolvencyAudit matches usage in the Razor page (context.SolvencyAudit)
    public DbSet<SolvencyAudit> SolvencyAudit { get; set; } = default!;
}
