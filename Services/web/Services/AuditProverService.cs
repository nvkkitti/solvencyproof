using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection;
using SolvencyProof.Helpers;
using SolvencyProof.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SolvencyProof.Services.Blockchain;
using Nethereum.Hex.HexConvertors.Extensions;

namespace SolvencyProof.Services
{
    public class AuditProverService : IAuditProverService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AuditProverService> _logger;
        private readonly IDataProtector _blindingFactorProtector;


        public AuditProverService(
            IServiceScopeFactory scopeFactory,
            ILogger<AuditProverService> logger,
            IDataProtectionProvider dataProtectionProvider)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _blindingFactorProtector = dataProtectionProvider.CreateProtector("SolvencyProof.Audit.BlindingFactor.v1");
        }
        public async Task GenerateProofAsync(Guid auditId)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            SolvencyProofContext db = scope.ServiceProvider.GetRequiredService<SolvencyProofContext>();
            SolvencyAudit? audit = await db.SolvencyAudit.FindAsync(auditId);

            if (audit is null)
            {
                return;
            }

            List<Account> accounts = await db.Account
                .AsNoTracking()
                .OrderBy(account => account.Id)
                .ToListAsync();

            if (accounts.Count == 0)
            {
                audit.Status = AuditStatus.Failed;
                await db.SaveChangesAsync();
                return;
            }

            audit.Status = AuditStatus.Proving;
            audit.TotalAssets = accounts.Sum(account => account.Balance);
            audit.MerkleRoot = MerkleTreeBuilder.BuildTreeAndGetRoot(accounts);
            await db.SaveChangesAsync();

            // Convert the amounts (decimal) to the smallest indivisible unit (for example, an integer) (Az összegeket (decimal) át kell váltani a legkisebb oszthatatlan egységre (pl. egész számra)).
            // Assume that our value uses 8 decimal places (Tegyük fel, hogy az értékünk 8 tizedesjegyet használ)
            long totalLiabilitiesInSmallestUnit = (long)(audit.TotalLiabilities * 100_000_000m);
            long totalAssetsInSmallestUnit = checked((long)(audit.TotalAssets * 100_000_000m));

            // 1. Liabilities (C_T) (Kötelezettségek (C_T))
            byte[] liabilityCommitment = PedersenHelper.CommitDeterministic(totalLiabilitiesInSmallestUnit);

            // 2. Assets (C_v) with the generated blinding factor (r) (Fedezet (C_v) a generált vakító tényezővel (r))
            byte[] blindingFactor = CryptoHelper.GenerateRandomBytes(32);
            byte[] assetCommitment = PedersenHelper.Commit(totalAssetsInSmallestUnit, blindingFactor);

            // 3. Difference using homomorphic subtraction (C_Delta) (Különbség homomorf kivonással (C_Delta))
            byte[] differenceCommitment = PedersenHelper.SubtractCommitments(assetCommitment, liabilityCommitment);

            // The dummy ZK proof remains for now (Dummy ZK proof marad egyelőre)
            byte[] zkProof = CryptoHelper.GenerateRandomBytes(256);

            audit.CommitmentCt = liabilityCommitment;
            audit.CommitmentCv = assetCommitment;
            audit.CommitmentCDelta = differenceCommitment;
            audit.ZkProof = zkProof;
            db.CryptographicSecrets.Add(new CryptographicSecret
            {
                Id = Guid.NewGuid(),
                SolvencyAuditId = audit.Id,
                BlindingFactorR = _blindingFactorProtector.Protect(blindingFactor)
            });
            audit.Status = AuditStatus.VerifiedOnChain;
            await db.SaveChangesAsync();
        }
        
        public class ProofOrchestratorService
        {
            private readonly HttpClient _rustAxumClient;
            private readonly EthereumSubmitterService _ethereumSubmitter;

            public ProofOrchestratorService(HttpClient rustAxumClient, EthereumSubmitterService ethereumSubmitter)
            {
                _rustAxumClient = rustAxumClient;
                _ethereumSubmitter = ethereumSubmitter;
            }
            public async Task RunAuditAndSubmitAsync(string institutionId)
            {
                //Rust Axum service call to generate the proof (Rust Axum szolgáltatás hívása a bizonyíték generálásához)
                var rustResponse = await _rustAxumClient.PostAsJsonAsync("/api/v1/generate-proof", new {id = institutionId});
                var proofData = await rustResponse.Content.ReadFromJsonAsync<EvmProofResponse>();

                //Type conversions for data coming from the Rust Axum service (Típuskonverziók a Rust Axum szolgáltatásból érkező adatokhoz)    
                var proofElements = proofData.ProofElements
                    .Select(Hex => Hex.HexToBigInteger(isHexLittleEndian : false))
                    .ToArray();

                var publicInputs = proofData.PublicInputs
                    .Select(Hex => Hex.HexToBigInteger(isHexLittleEndian: false))
                    .ToArray();

                //Submit the proof to Ethereum (A bizonyíték benyújtása az Ethereumhoz)
                byte[] idBytes = System.Text.Encoding.UTF8.GetBytes(institutionId);
                string txHash = await _ethereumSubmitter.SubmitProofOnChainAsync(idBytes, proofElements, publicInputs);

                Console.WriteLine($"Sikeres benyújtás! Tranzakciós hash: {txHash}");
            }
        }
    }
}