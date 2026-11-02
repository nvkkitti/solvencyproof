using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

public class SolvencyProofContextFactory : IDesignTimeDbContextFactory<SolvencyProofContext>
{
    public SolvencyProofContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SolvencyProofContext>();

        optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=ProofOfSolvencyDb;Trusted_Connection=True;MultipleActiveResultSets=true");

        return new SolvencyProofContext(optionsBuilder.Options);
    }
}
