using System;

namespace SolvencyProof.Models
{
    // Represents the confidential cryptographic material associated with an audit.
    public class CryptographicSecret
    {
        public Guid Id { get; set; }

        // Optional foreign key to link back to the audit (one-to-one relationship)
        public Guid SolvencyAuditId { get; set; }

        // A fedezet elrejtéséhez használt vakító faktor (Commitment to the total assets) 
        public byte[] BlindingFactorR { get; set; }

        // Navigation back to the owning audit
        public SolvencyAudit Audit { get; set; }
    }
}
