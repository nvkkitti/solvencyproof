using System.Text.Json.Serialization;

namespace SolvencyProof.Models;

public sealed class EvmProofResponse
{
    /// <summary>The SHA-256 digest of the STARK proof, as the contract's uint256[] (A STARK bizonyíték SHA-256 digestje).</summary>
    [JsonPropertyName("proof_elements")]
    public required string[] ProofElements { get; init; }

    /// <summary>[0] assets commitment, [1] liabilities Merkle root, [2] audit nonce (Eszköz-elkötelezettség, Merkle-gyökér, nonce).</summary>
    [JsonPropertyName("public_inputs")]
    public required string[] PublicInputs { get; init; }

    /// <summary>The full serialized zk-STARK proof, "0x" hex (A teljes zk-STARK bizonyíték).</summary>
    [JsonPropertyName("proof")]
    public required string Proof { get; init; }

    /// <summary>The depth of the liabilities tree the proof was made for (A kötelezettségfa mélysége).</summary>
    [JsonPropertyName("depth")]
    public required int Depth { get; init; }

    /// <summary>The prover verified its own proof before answering (A bizonyító a válasz előtt ellenőrizte a saját bizonyítékát).</summary>
    [JsonPropertyName("verified")]
    public required bool Verified { get; init; }
}
