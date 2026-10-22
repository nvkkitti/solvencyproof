using System;
using System.ComponentModel.DataAnnotations;
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
        [MaxLength(32)] // Limit byte array length to 32 bytes (32 bájtos korlátozás)
        public byte[] MerkleRoot { get; set; } = Array.Empty<byte>();
        public byte[] CommitmentCv { get; set; } = Array.Empty<byte>();
        public byte[] CommitmentCt { get; set; } = Array.Empty<byte>();
        public byte[] CommitmentCDelta { get; set; } = Array.Empty<byte>();
        public byte[] ZkProof { get; set; } = Array.Empty<byte>();

        public AuditStatus Status { get; set; } // Audit status (Audit állapota)

        //Navigációs tulajdonság a kriptográfiai titok felé (1:1 kapcsolat)
        public CryptographicSecret Secret { get; set; }
    }
}
