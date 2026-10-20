using System;
using Microsoft.EntityFrameworkCore;

namespace SolvencyProof.Models
{
    public class Account
    {
        public Guid Id { get; set; } // Primary key (Ügyfél ID)

        public decimal Balance { get; set; } // Account balance (Egyenleg)

        public byte[] MerkleLeafSalt { get; set; } // Brute-force protection salt for Merkle leaf (Merkle levél só)

        public DateTime LastUpdate { get; set; } // Last update timestamp (Utolsó frissítés)

    }
}
