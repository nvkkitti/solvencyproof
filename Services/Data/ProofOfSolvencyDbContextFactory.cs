using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

public class ProofOfSolvencyDbContextFactory : IDesignTimeDbContextFactory<ProofOfSolvencyDbContext>
{
    public ProofOfSolvencyDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ProofOfSolvencyDbContext>();

        // Ez a string megegyezik azzal, amit az appsettings.json-be is írtál
        optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=ProofOfSolvencyDb;Trusted_Connection=True;MultipleActiveResultSets=true");

        return new ProofOfSolvencyDbContext(optionsBuilder.Options);
    }
}