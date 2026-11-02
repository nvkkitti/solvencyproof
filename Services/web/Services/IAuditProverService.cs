using System;
using System.Threading.Tasks;

namespace SolvencyProof.Services
{
    public interface IAuditProverService
    {
        Task GenerateProofAsync(Guid auditId);
    }
}
