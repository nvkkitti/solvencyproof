using System.Text.Json.Serialization;

namespace SolvencyProof.Models
{
    public class ZkProverRequest
    {
        // Witness
        [JsonPropertyName("v")]
        public long TotalAssets { get; set; }

        [JsonPropertyName("T")]
        public long TotalLiabilities { get; set; }

        [JsonPropertyName("r")]
        public string BlindingFactorHex { get; set; } = string.Empty;

        // Public inputs
        [JsonPropertyName("C_v")]
        public string CommitmentCvHex { get; set; } = string.Empty;

        [JsonPropertyName("C_T")]
        public string CommitmentCTHex { get; set; } = string.Empty;

        [JsonPropertyName("C_Delta")]
        public string CommitmentCDeltaHex { get; set; } = string.Empty;
    }

    public class ZkProverResponse
    {
        [JsonPropertyName("proof_hex")]
        public string ProofHex { get; set; } = string.Empty;
    }
}