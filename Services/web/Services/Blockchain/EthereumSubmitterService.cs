using System;
using System.Numerics;
using System.Threading.Tasks;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Nethereum.Contracts;

namespace SolvencyProof.Services.Blockchain
{
    public class EthereumSubmitterService
    {
        private readonly string _rpcUrl;
        private readonly string _contractAddress;
        private readonly Account _account;

        public EthereumSubmitterService(string rpcUrl, string privateKey, string contractAddress)
        {
            _rpcUrl = rpcUrl;
            _account = new Account(privateKey);
            _contractAddress = contractAddress;
        }

        public async Task<string> SubmitProofOnChainAsync(byte[] institutionIdBytes, BigInteger[] proofElements, BigInteger[] publicInputs)
        {
            // 1. Web3 kliens inicializálása
            var web3 = new Web3(_account, _rpcUrl);

            // 2. Az okosszerződés ABI-ja (A Solidity kód alapján generálva)
            string abi = System.IO.File.ReadAllText("SolvencyContractAbi.json");

            var contract = web3.Eth.GetContract(abi, _contractAddress);
            var submitFunction = contract.GetFunction("submitAndVerifyProof");

            // 3. Konvertálás Solidity kompatibilis típusokra
            // institutionIdBytes -> bytes32
            byte[] id32 = new byte[32];
            Array.Copy(institutionIdBytes, id32, Math.Min(institutionIdBytes.Length, 32));

            Console.WriteLine("Tranzakció aláírása és beküldése az Ethereum hálózatra...");

            // 4. Tranzakció beküldése
            var transactionHash = await submitFunction.SendTransactionAsync(
                _account.Address, // Feladó
                new Nethereum.Hex.HexTypes.HexBigInteger(3000000), // Gas limit
                new Nethereum.Hex.HexTypes.HexBigInteger(0), // Value (0 ETH)
                id32,
                proofElements,
                publicInputs
            );

            return transactionHash;
        }
    }
}