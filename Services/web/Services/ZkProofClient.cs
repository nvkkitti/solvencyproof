using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SolvencyProof.Services;

public record ProofRequest(
    [property: JsonPropertyName("public_input")] string PublicInput
);

public record ProofResponse(
    [property: JsonPropertyName("proof_data")] string ProofData,
    [property: JsonPropertyName("status")] string Status
);

public class ZkProofClient
{
    private readonly HttpClient _httpClient;

    public ZkProofClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        // A Rust szerver címe
        _httpClient.BaseAddress = new Uri("http://localhost:3000/");
    }

    public async Task<ProofResponse?> GenerateProofAsync(string publicInput)
    {
        var requestPayload = new ProofRequest(publicInput);
        var response = await _httpClient.PostAsJsonAsync("prove", requestPayload);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProofResponse>();
    }
}