using System.Text.Json.Serialization;

namespace SolvencyProof.Models;

public sealed class ProofResponse
{
    [JsonPropertyName("proof_elements")]
    public required string[] ProofElements { get; init; }

    [JsonPropertyName("public_inputs")]
    public required string[] PublicInputs { get; init; }
}
