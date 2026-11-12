using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;

namespace SolvencyProof.Services.Blockchain;

// Mirrors the SolvencyVerified event of the ProofOfSolvency contract (A ProofOfSolvency szerződés SolvencyVerified eseményének tükörképe)
[Event("SolvencyVerified")]
public sealed class SolvencyVerifiedEventDto : IEventDTO
{
    [Parameter("bytes32", "institutionId", 1, true)]
    public byte[] InstitutionId { get; set; } = [];

    [Parameter("uint256", "timestamp", 2, false)]
    public BigInteger Timestamp { get; set; }

    [Parameter("bytes32", "liabilitiesMerkleRoot", 3, false)]
    public byte[] LiabilitiesMerkleRoot { get; set; } = [];
}

public sealed class HardhatProofSubmitterService
{
    private const string AbiFileName = "SolvencyContractAbi.json";

    private readonly Account _account;
    private readonly string _contractAddress;
    private readonly Web3 _web3;
    private readonly Function _submitFunction;

    public HardhatProofSubmitterService(IConfiguration configuration)
    {
        IConfigurationSection hardhat = configuration.GetRequiredSection("Hardhat");
        string rpcUrl = hardhat["RpcUrl"]
            ?? throw new InvalidOperationException("Configuration 'Hardhat:RpcUrl' is required.");
        string privateKey = RequirePrivateKey(hardhat["PrivateKey"], "Hardhat:PrivateKey");
        string contractAddress = RequireAddress(hardhat["ContractAddress"], "Hardhat:ContractAddress");

        if (!Uri.TryCreate(rpcUrl, UriKind.Absolute, out Uri? rpcUri)
            || (rpcUri.Scheme != Uri.UriSchemeHttp && rpcUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Configuration 'Hardhat:RpcUrl' must be an absolute HTTP(S) URL.");
        }

        _account = new Account(privateKey);
        _contractAddress = contractAddress;
        _web3 = new Web3(_account, rpcUrl);
        _submitFunction = _web3.Eth.GetContract(LoadAbi(), contractAddress)
            .GetFunction("submitAndVerifyProof");
    }

    /// <summary>
    /// Sends the submitAndVerifyProof transaction to the chain and returns its hash (A tranzakció elküldése, a hash visszaadása).
    /// </summary>
    public async Task<string> SendProofAsync(
        byte[] institutionIdBytes,
        BigInteger[] proofElements,
        BigInteger[] publicInputs)
    {
        // Checked before sending so a missing deployment fails without mining a transaction (Még a küldés előtt ellenőrizzük, hogy a szerződés kódja létezik, így telepítés nélkül nem bányászódik tranzakció)
        string code = await _web3.Eth.GetCode.SendRequestAsync(_contractAddress);
        if (string.IsNullOrEmpty(code) || code == "0x")
        {
            throw new InvalidOperationException(
                $"A(z) {_contractAddress} címen nincs szerződés-kód (a szerződés valószínűleg nincs telepítve a Hardhat-láncon).");
        }

        byte[] institutionId = new byte[32];
        Array.Copy(institutionIdBytes, institutionId, Math.Min(institutionIdBytes.Length, institutionId.Length));
        return await _submitFunction.SendTransactionAsync(
            _account.Address,
            new HexBigInteger(3_000_000),
            new HexBigInteger(0),
            institutionId,
            proofElements,
            publicInputs);
    }

    /// <summary>
    /// Waits for the transaction to be mined; a reverted verification (status 0) is an error (Megvárja a tranzakciót; a visszagörgetett verifikáció hiba).
    /// </summary>
    public async Task WaitForVerificationAsync(string transactionHash, CancellationToken cancellationToken = default)
    {
        TransactionReceipt receipt = await _web3.TransactionReceiptPolling
            .PollForReceiptAsync(transactionHash, cancellationToken);

        if (receipt.Status?.Value != 1)
        {
            throw new InvalidOperationException(
                $"Az on-chain ellenőrzés sikertelen (a tranzakció visszagörgetődött): {transactionHash}");
        }

        // A transaction sent to an address without contract code also succeeds, so the receipt must contain the SolvencyVerified event of our contract (A kód nélküli címre küldött tranzakció is sikeres, ezért a nyugtának tartalmaznia kell a szerződésünk SolvencyVerified eseményét)
        bool verifiedEventEmitted = receipt.DecodeAllEvents<SolvencyVerifiedEventDto>()
            .Any(log => string.Equals(log.Log.Address, _contractAddress, StringComparison.OrdinalIgnoreCase));
        if (!verifiedEventEmitted)
        {
            throw new InvalidOperationException(
                $"A tranzakció lefutott, de a(z) {_contractAddress} címen lévő szerződés nem bocsátott ki SolvencyVerified eseményt (a szerződés valószínűleg nincs telepítve): {transactionHash}");
        }
    }

    /// <summary>
    /// Reads the liabilities Merkle root the contract emitted for an audit, straight from the chain logs (Az audithoz a szerződés által kibocsátott Merkle-gyökér kiolvasása közvetlenül a lánc naplóiból).
    /// Returns null when no SolvencyVerified event exists for the audit (Null, ha nincs SolvencyVerified esemény az audithoz).
    /// </summary>
    public async Task<byte[]?> GetVerifiedMerkleRootAsync(Guid auditId)
    {
        byte[] institutionId = ComputeInstitutionId(auditId);
        Event<SolvencyVerifiedEventDto> solvencyVerified = _web3.Eth.GetEvent<SolvencyVerifiedEventDto>(_contractAddress);
        NewFilterInput filter = solvencyVerified.CreateFilterInput(
            new object[] { institutionId },
            BlockParameter.CreateEarliest(),
            BlockParameter.CreateLatest());

        List<EventLog<SolvencyVerifiedEventDto>> logs = await solvencyVerified.GetAllChangesAsync(filter);

        // An audit id is submitted once, but if it were repeated the latest event is the authoritative one (Egy audit azonosító egyszer kerül beküldésre, ismétlésnél a legutóbbi esemény az irányadó)
        return logs
            .OrderByDescending(log => log.Log.BlockNumber.Value)
            .ThenByDescending(log => log.Log.LogIndex.Value)
            .Select(log => log.Event.LiabilitiesMerkleRoot)
            .FirstOrDefault();
    }

    // The contract identifies an institution by SHA-256 of the audit id (A szerződés az intézményt az audit azonosító SHA-256 hash-ével azonosítja)
    public static byte[] ComputeInstitutionId(Guid auditId)
    {
        return System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(auditId.ToString()));
    }

    // Parses a 0x-prefixed big-endian hex string as an unsigned uint256 (0x előtagú, big-endian hex előjel nélküli értelmezése)
    public static BigInteger ParseUint256(string hex)
    {
        string digits = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        // The leading zero keeps BigInteger.Parse from reading a high bit as a sign (A vezető nulla megakadályozza az előjeles értelmezést)
        return BigInteger.Parse("0" + digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    private static string LoadAbi()
    {
        string path = Path.Combine(AppContext.BaseDirectory, AbiFileName);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetRawText();
    }

    private static string RequireAddress(string? value, string settingName)
    {
        if (value is null || !Regex.IsMatch(value, "^0x[0-9a-fA-F]{40}$"))
        {
            throw new InvalidOperationException($"Configuration '{settingName}' must be a 20-byte hexadecimal address.");
        }

        return value;
    }

    private static string RequirePrivateKey(string? value, string settingName)
    {
        string privateKey = value?.Trim() ?? string.Empty;
        if (!Regex.IsMatch(privateKey, "^(?:0x)?[0-9a-fA-F]{64}$"))
        {
            throw new InvalidOperationException(
                $"Configuration '{settingName}' must be a 32-byte hexadecimal private key (64 hex characters, optionally prefixed with '0x'). Do not include placeholder text or angle brackets.");
        }

        return privateKey;
    }
}
