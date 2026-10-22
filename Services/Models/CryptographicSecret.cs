using System;
using System.ComponentModel.DataAnnotations;

namespace SolvencyProof.Models
{
    // Represents the confidential cryptographic material associated with an audit.
    public class CryptographicSecret
    {
        [Key]
        public Guid Id { get; set; }

        // Optional foreign key to link back to the audit (one-to-one relationship)
        public Guid SolvencyAuditId { get; set; }

        // A fedezet elrejtéséhez használt vakító faktor (Commitment to the total assets) 
        [MaxLength(32)]
        public byte[] BlindingFactorR { get; set; } = Array.Empty<byte>();

        // Navigation back to the owning audit
        public SolvencyAudit Audit { get; set; }
    }
}
