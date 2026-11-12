using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SolvencyProof.Models;

namespace SolvencyProof.Services;

/// <summary>One account leaf of the private witness (Egy számla-levél a titkos tanúból).</summary>
public sealed record ZkLeafRequest(
    [property: JsonPropertyName("id")] ulong[] Id,
    [property: JsonPropertyName("salt")] ulong[] Salt,
    [property: JsonPropertyName("balance")] ulong Balance
);

/// <summary>
/// The witness the institution's own prover needs. It travels over loopback only; what leaves the institution
/// is the proof and its public inputs (Az intézmény saját bizonyítójának tanúja, csak loopbacken utazik).
/// </summary>
public sealed record ProofRequest(
    [property: JsonPropertyName("institution_id")] string InstitutionId,
    [property: JsonPropertyName("total_assets")] ulong TotalAssets,
    [property: JsonPropertyName("assets_blinding")] ulong[] AssetsBlinding,
    [property: JsonPropertyName("expected_assets_commitment")] string ExpectedAssetsCommitment,
    [property: JsonPropertyName("expected_root")] string ExpectedRoot,
    [property: JsonPropertyName("leaves")] ZkLeafRequest[] Leaves
);

/// <summary>A proof check needs public data only (A bizonyíték ellenőrzéséhez csak nyilvános adat kell).</summary>
public sealed record VerifyProofRequest(
    [property: JsonPropertyName("depth")] int Depth,
    [property: JsonPropertyName("proof")] string Proof,
    [property: JsonPropertyName("assets_commitment")] string AssetsCommitment,
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("institution_id")] string InstitutionId
);

public sealed record VerifyProofResponse(
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("error")] string? Error
);

public class ZkProofClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ZkProofClient> _logger;

    public ZkProofClient(HttpClient httpClient, IConfiguration configuration, ILogger<ZkProofClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        // A Rust server address, loopback:3000 unless 'ZkProver:BaseUrl' says otherwise (A Rust szerver címe)
        _httpClient.BaseAddress = new Uri(configuration["ZkProver:BaseUrl"] ?? "http://127.0.0.1:3000/");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        // Proving a large tree takes a while (Nagy fa bizonyítása időbe telik)
        _httpClient.Timeout = TimeSpan.FromMinutes(10);
    }

    public async Task<EvmProofResponse> GenerateProofAsync(
        ProofRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/v1/generate-proof", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await LogFailureAsync("generate-proof", response, cancellationToken);
            throw new HttpRequestException("A bizonyítási szolgáltatás hibát jelzett.");
        }

        return await response.Content.ReadFromJsonAsync<EvmProofResponse>(cancellationToken)
            ?? throw new InvalidOperationException("A bizonyítási szolgáltatás üres választ adott.");
    }

    /// <summary>Asks the prover service to check a STARK proof against public inputs (A STARK bizonyíték ellenőrzése nyilvános bemenetekkel).</summary>
    public async Task<VerifyProofResponse> VerifyProofAsync(
        VerifyProofRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/v1/verify-proof", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await LogFailureAsync("verify-proof", response, cancellationToken);
            throw new HttpRequestException("A bizonyítási szolgáltatás hibát jelzett.");
        }

        return await response.Content.ReadFromJsonAsync<VerifyProofResponse>(cancellationToken)
            ?? throw new InvalidOperationException("A bizonyítási szolgáltatás üres választ adott.");
    }

    /// <summary>Logs the prover's status and body for the operator; the body never reaches the UI.</summary>
    private async Task LogFailureAsync(string operation, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogError("Prover {Operation} failed with {StatusCode}: {Body}", operation, (int)response.StatusCode, body);
    }
}
