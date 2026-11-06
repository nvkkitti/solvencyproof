using System.Numerics;
using System.Text.RegularExpressions;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;

namespace SolvencyProof.Services.Blockchain;

public sealed class HardhatProofSubmitterService
{
    private const string ContractAbi =
        """[{"inputs":[{"internalType":"bytes32","name":"institutionId","type":"bytes32"},{"internalType":"uint256[]","name":"proof","type":"uint256[]"},{"internalType":"uint256[]","name":"publicInputs","type":"uint256[]"}],"name":"submitAndVerifyProof","outputs":[],"stateMutability":"nonpayable","type":"function"}]""";

    private readonly Account _account;
    private readonly Function _submitFunction;
    private readonly string _contractAddress;

    public HardhatProofSubmitterService(IConfiguration configuration)
    {
        IConfigurationSection hardhat = configuration.GetRequiredSection("Hardhat");
        string rpcUrl = hardhat["JsonRpcUrl"]
            ?? throw new InvalidOperationException("Configuration 'Hardhat:JsonRpcUrl' is required.");
        string privateKey = RequirePrivateKey(hardhat["PrivateKey"], "Hardhat:PrivateKey");
        _contractAddress = RequireAddress(hardhat["ProofContractAddress"], "Hardhat:ProofContractAddress");

        if (!Uri.TryCreate(rpcUrl, UriKind.Absolute, out Uri? rpcUri)
            || (rpcUri.Scheme != Uri.UriSchemeHttp && rpcUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Configuration 'Hardhat:JsonRpcUrl' must be an absolute HTTP(S) URL.");
        }

        _account = new Account(privateKey);
        Web3 web3 = new(_account, rpcUrl);
        _submitFunction = web3.Eth.GetContract(ContractAbi, _contractAddress)
            .GetFunction("submitAndVerifyProof");
    }

    public async Task<string> SubmitProofAsync(
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
