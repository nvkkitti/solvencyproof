using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SolvencyProof.Models;

namespace SolvencyProof.Services;

public record ProofRequest(
    [property: JsonPropertyName("institution_id")] string InstitutionId,
    [property: JsonPropertyName("total_assets")] ulong TotalAssets,
    [property: JsonPropertyName("total_liabilities")] ulong TotalLiabilities,
    [property: JsonPropertyName("leaves")] string[] Leaves
);

public class ZkProofClient
{
    private readonly HttpClient _httpClient;

    public ZkProofClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        // A Rust szerver címe
        _httpClient.BaseAddress = new Uri("http://127.0.0.1:3000/");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public async Task<EvmProofResponse> GenerateProofAsync(
        string institutionId,
        ulong totalAssets,
        ulong totalLiabilities,
        IReadOnlyList<string> leaves,
        CancellationToken cancellationToken = default)
    {
        var requestPayload = new ProofRequest(institutionId, totalAssets, totalLiabilities, leaves.ToArray());
        var response = await _httpClient.PostAsJsonAsync("api/v1/generate-proof", requestPayload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"The proof service returned {(int)response.StatusCode}: {detail}");
        }

        return await response.Content.ReadFromJsonAsync<EvmProofResponse>(cancellationToken)
            ?? throw new InvalidOperationException("The proof service returned an empty response.");
    }
}
