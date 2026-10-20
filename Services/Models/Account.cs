using System;
using Microsoft.EntityFrameworkCore;

namespace SolvencyProof.Models
{
    public class Account
    {
        public Guid Id { get; set; } // Primary key (Ügyfél ID)

        public decimal Balance { get; set; } // Account balance (Egyenleg)

        public byte[] MerkleLeafSalt { get; set; } = new byte[0];

        public DateTime LastUpdate { get; set; } // Last update timestamp (Utolsó frissítés)

        // HELLO THERE
        // Required by EF Core: initialize nullable byte[] with empty array to avoid CS8618
        // https://learn.microsoft.com/en-us/ef/core/walkthroughs/nullable-reference-types
        // https://github.com/dotnet/efcore/issues/21884
    }
}
