using System;
using System.Threading;
using System.Threading.Tasks;
using SolvencyProof.Models;

namespace SolvencyProof.Services
{
    public interface IAuditProverService
    {
        Task<AuditProofResult> GenerateProofAsync(
            Guid auditId,
            IProgress<AuditProgressReport>? progress = null,
            CancellationToken cancellationToken = default);
    }
}
