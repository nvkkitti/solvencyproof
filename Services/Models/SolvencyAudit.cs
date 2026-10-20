using System;
using Microsoft.EntityFrameworkCore;

namespace SolvencyProof.Models
{
    public enum AuditStatus
    {
        Prepared, // Audit prepared but not yet started (Audit előkészítve, de még nem indult el)
        Proving, // Audit is currently being proven (Audit bizonyítása folyamatban)
        VerifiedOnChain, // Audit verified on blockchain (Audit blokkláncon ellenőrizve)
        Failed // Audit failed (Audit sikertelen, hiba vagy érvénytelen fedezet)
    }
    public class SolvencyAudit
    {
        public Guid Id { get; set; } // Primary key (Audit ID)

        public DateTime Timestamp { get; set; } // Audit timestamp (Audit időbélyegző)

        public decimal TotalLiabilities { get; set; } // T (Kötelezettség)

        public decimal TotalAssets { get; set; } // v (Rendelkezésre álló fedezet)

        //Kriptogáriai elkötelezettségek és STARK adatok
        public byte[] MerkleRoot { get; set; }
        public byte[] CommitmentCv { get; set; }
        public byte[] CommitmentCt { get; set; }
        public byte[] CommitmentCDelta { get; set; }
        public byte[] ZkProof { get; set; }

        public AuditStatus Status { get; set; } // Audit status (Audit állapota)

        //Navigációs tulajdonság a kriptográfiai titok felé (1:1 kapcsolat)
        public CryptographicSecret Secret { get; set; }
    }
}
