using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using SolvencyProof.Models;
using SolvencyProof.Data;
using Microsoft.Extensions.DependencyInjection;

public class ProofOfSolvencyDbContext : DbContext 
{ 
    public ProofOfSolvencyDbContext(DbContextOptions<ProofOfSolvencyDbContext> options) : base(options) { }
    protected override void OnModelCreating(ModelBuilder modelBuilder) 
    { 
        base.OnModelCreating(modelBuilder);
        // Itt történik a seedelés
        modelBuilder.Entity<Account>().HasData(
            new Account { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Balance = 1500.50m },
            new Account { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Balance = 0.00m },
            new Account { Id = Guid.Parse("00000000-0000-0000-0000-000000000003"), Balance = 250000.75m },
            new Account { Id = Guid.Parse("00000000-0000-0000-0000-000000000004"), Balance = -150.20m } );
            // A 'm' betű a szám végén jelzi, hogy ez decimal típus );
    } 
}