namespace SolvencyProof.Models;

// Progress of the audit pipeline reported to the UI (Az audit folyamat állapota a felület felé)
// StepIndex: 1-4 = the step that has just started, 5 = every step finished
public class AuditProgressReport
{
    public const int TotalSteps = 4;

    public int StepIndex { get; set; }

    // For terminal logging (terminál naplózáshoz)
    public string Message { get; set; } = string.Empty;
}

public sealed record AuditProofResult(string TransactionHash, string MerkleRoot);
