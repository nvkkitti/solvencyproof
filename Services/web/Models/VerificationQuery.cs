namespace SolvencyProof.Models;

public class VerificationQuery
{
    public long Id { get; set; }

    public Guid AccountId { get; set; }

    public DateTime QueriedAt { get; set; }
}
