using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;

namespace SolvencyProof.Services.Blockchain;

public sealed class HardhatProofSubmitterService
{
    private const string AbiFileName = "SolvencyContractAbi.json";

    private readonly Account _account;
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
