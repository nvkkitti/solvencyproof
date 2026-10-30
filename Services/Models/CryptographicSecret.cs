using System;
using System.ComponentModel.DataAnnotations;

namespace SolvencyProof.Models
{
    // Represents the confidential cryptographic material associated with an audit.
    public class CryptographicSecret
    {
        public Guid Id { get; set; }

        // Optional foreign key to link back to the audit (one-to-one relationship)
        public Guid SolvencyAuditId { get; set; }

        // Blinding factor encrypted by Data Protection (A Data Protection által titkosított vakító faktor).
        public byte[] BlindingFactorR { get; set; } = Array.Empty<byte>();

        // Navigation back to the owning audit
        [Required] public SolvencyAudit Audit { get; set; } = null!;
    }
}
