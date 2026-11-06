using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SolvencyProof.Models;

namespace SolvencyProof.Services;

public record ProofRequest(
    [property: JsonPropertyName("institution_id")] string InstitutionId,
    [property: JsonPropertyName("total_assets")] ulong TotalAssets,
    [property: JsonPropertyName("total_liabilities")] ulong TotalLiabilities
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

    public async Task<bool> IsRustAvailableAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));

        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync("api/v1/health", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    public async Task<ProofResponse> GenerateProofAsync(
        string institutionId,
        ulong totalAssets,
        ulong totalLiabilities)
    {
        var requestPayload = new ProofRequest(institutionId, totalAssets, totalLiabilities);
        var response = await _httpClient.PostAsJsonAsync("api/v1/generate-proof", requestPayload);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProofResponse>()
            ?? throw new InvalidOperationException("The proof service returned an empty response.");
    }
}