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

        // Cryptographic commitments and STARK data (Kriptogáriai elkötelezettségek és STARK adatok)
        [MaxLength(32)] // Limit byte array length to 32 bytes (32 bájtos korlátozás)
        public byte[] MerkleRoot { get; set; } = Array.Empty<byte>(); // Root digest of the liabilities sum tree (A kötelezettség-összegfa gyökere)
        public byte[] CommitmentCv { get; set; } = Array.Empty<byte>(); // Poseidon2 commitment to the assets, C_A (Az eszközök Poseidon2 elkötelezettsége)
        public byte[] ZkProof { get; set; } = Array.Empty<byte>(); // The full serialized zk-STARK proof (A teljes zk-STARK bizonyíték)

        // Depth of the liabilities tree the proof was made for; 0 for audits made before the zk-STARK (A bizonyítás fájának mélysége, 0 a régi auditoknál)
        public int TreeDepth { get; set; }

        public AuditStatus Status { get; set; } // Audit status (Audit állapota)

        // Navigation property to the cryptographic secret (Navigációs tulajdonság a kriptográfiai titok felé (1:1 kapcsolat))
        [Required] public CryptographicSecret Secret { get; set; } = null!;
    }
}
