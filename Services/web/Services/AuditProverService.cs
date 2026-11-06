using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection;
using SolvencyProof.Helpers;
using SolvencyProof.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SolvencyProof.Services.Blockchain;

namespace SolvencyProof.Services
{
    public class AuditProverService : IAuditProverService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AuditProverService> _logger;
        private readonly IDataProtector _blindingFactorProtector;
        private readonly ZkProofClient _zkProofClient;
        private readonly HardhatProofSubmitterService _proofSubmitter;

        public AuditProverService(
            IServiceScopeFactory scopeFactory,
            ILogger<AuditProverService> logger,
            IDataProtectionProvider dataProtectionProvider,
            ZkProofClient zkProofClient,
            HardhatProofSubmitterService proofSubmitter)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _blindingFactorProtector = dataProtectionProvider.CreateProtector("SolvencyProof.Audit.BlindingFactor.v1");
            _zkProofClient = zkProofClient;
            _proofSubmitter = proofSubmitter;
        }

        public async Task<AuditProofResult> GenerateProofAsync(
            Guid auditId,
            IProgress<AuditProgressReport>? progress = null,
            CancellationToken cancellationToken = default)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            SolvencyProofContext db = scope.ServiceProvider.GetRequiredService<SolvencyProofContext>();
            SolvencyAudit audit = await db.SolvencyAudit.FindAsync([auditId], cancellationToken)
                ?? throw new InvalidOperationException($"A(z) {auditId} azonosítójú audit nem található.");

            try
            {
                return await RunPipelineAsync(db, audit, progress, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Az audit ({AuditId}) sikertelen.", auditId);
                audit.Status = AuditStatus.Failed;
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
        }

        private async Task<AuditProofResult> RunPipelineAsync(
            SolvencyProofContext db,
            SolvencyAudit audit,
            IProgress<AuditProgressReport>? progress,
            CancellationToken cancellationToken)
        {
            // Step 1: read the customer balances from the SQL database (1. lépés: egyenlegek kiolvasása az SQL adatbázisból)
            Report(progress, 1, "Ügyfélegyenlegek lekérdezése az adatbázisból...");
            List<Account> accounts = await db.Account
                .AsNoTracking()
                .OrderBy(account => account.Id)
                .ToListAsync(cancellationToken);

            if (accounts.Count == 0)
            {
                throw new InvalidOperationException("Nincs ügyfélszámla az adatbázisban, az audit nem futtatható.");
            }

            audit.Status = AuditStatus.Proving;
            audit.TotalAssets = accounts.Sum(account => account.Balance);
            byte[] localMerkleRoot = MerkleTreeBuilder.BuildTreeAndGetRoot(accounts);
            audit.MerkleRoot = localMerkleRoot;
            await db.SaveChangesAsync(cancellationToken);

            // Convert the amounts (decimal) to the smallest indivisible unit (for example, an integer) (Az összegeket (decimal) át kell váltani a legkisebb oszthatatlan egységre (pl. egész számra)).
            // Assume that our value uses 8 decimal places (Tegyük fel, hogy az értékünk 8 tizedesjegyet használ)
            long totalLiabilitiesInSmallestUnit = checked((long)(audit.TotalLiabilities * 100_000_000m));
            long totalAssetsInSmallestUnit = checked((long)(audit.TotalAssets * 100_000_000m));

            // 1. Liabilities (C_T) (Kötelezettségek (C_T))
            byte[] liabilityCommitment = PedersenHelper.CommitDeterministic(totalLiabilitiesInSmallestUnit);

            // 2. Assets (C_v) with the generated blinding factor (r) (Fedezet (C_v) a generált vakító tényezővel (r))
            byte[] blindingFactor = CryptoHelper.GenerateRandomBytes(32);
            byte[] assetCommitment = PedersenHelper.Commit(totalAssetsInSmallestUnit, blindingFactor);

            // 3. Difference using homomorphic subtraction (C_Delta) (Különbség homomorf kivonással (C_Delta))
            byte[] differenceCommitment = PedersenHelper.SubtractCommitments(assetCommitment, liabilityCommitment);

            // Step 2: ask the Rust Axum prover for the zk-STARK proof (2. lépés: zk-STARK bizonyíték kérése a Rust Axum szervertől)
            Report(progress, 2, "Merkle-fa és zk-STARK bizonyíték kérése a Rust szervertől (127.0.0.1:3000)...");
            string institutionId = audit.Id.ToString();
            string[] leaves = accounts
                .Select(account => "0x" + Convert.ToHexString(MerkleTreeBuilder.GenerateLeafHash(account)).ToLowerInvariant())
                .ToArray();

            EvmProofResponse proofData = await _zkProofClient.GenerateProofAsync(
                institutionId,
                checked((ulong)totalAssetsInSmallestUnit),
                checked((ulong)totalLiabilitiesInSmallestUnit),
                leaves,
                cancellationToken);

            if (proofData.ProofElements.Length == 0 || proofData.PublicInputs.Length < 2)
            {
                throw new InvalidOperationException("A Rust szerver hiányos bizonyítékot adott vissza.");
            }

            // The root computed by Rust must match the one computed here (A Rust által számolt gyökérnek egyeznie kell a helyben számolttal)
            string merkleRootHex = "0x" + Convert.ToHexString(localMerkleRoot).ToLowerInvariant();
            if (!string.Equals(proofData.PublicInputs[1], merkleRootHex, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"A Rust és a C# Merkle-gyökere eltér: {proofData.PublicInputs[1]} != {merkleRootHex}");
            }

            var proofElements = proofData.ProofElements.Select(HardhatProofSubmitterService.ParseUint256).ToArray();
            var publicInputs = proofData.PublicInputs.Select(HardhatProofSubmitterService.ParseUint256).ToArray();

            audit.CommitmentCt = liabilityCommitment;
            audit.CommitmentCv = assetCommitment;
            audit.CommitmentCDelta = differenceCommitment;
            audit.ZkProof = proofData.ProofElements
                .SelectMany(element => Convert.FromHexString(element[2..]))
                .ToArray();
            db.CryptographicSecrets.Add(new CryptographicSecret
            {
                Id = Guid.NewGuid(),
                SolvencyAuditId = audit.Id,
                BlindingFactorR = _blindingFactorProtector.Protect(blindingFactor)
            });
            await db.SaveChangesAsync(cancellationToken);

            // Step 3: send the transaction to the Hardhat chain (3. lépés: tranzakció küldése a Hardhat láncra)
            Report(progress, 3, "submitAndVerifyProof tranzakció küldése a Hardhat láncra (127.0.0.1:8545)...");
            byte[] institutionIdBytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(institutionId));
            string transactionHash = await _proofSubmitter.SendProofAsync(institutionIdBytes, proofElements, publicInputs);

            // Step 4: the contract verifies the proof on-chain via MockStarkVerifier (4. lépés: on-chain ellenőrzés)
            Report(progress, 4, $"On-chain ellenőrzés, tranzakció: {transactionHash}");
            await _proofSubmitter.WaitForVerificationAsync(transactionHash, cancellationToken);

            audit.Status = AuditStatus.VerifiedOnChain;
            await db.SaveChangesAsync(cancellationToken);

            Report(progress, AuditProgressReport.TotalSteps + 1, $"Az audit sikeres. Tranzakciós hash: {transactionHash}");
            return new AuditProofResult(transactionHash, merkleRootHex);
        }

        private void Report(IProgress<AuditProgressReport>? progress, int stepIndex, string message)
        {
            _logger.LogInformation("Audit {Step}. lépés: {Message}", stepIndex, message);
            progress?.Report(new AuditProgressReport { StepIndex = stepIndex, Message = message });
        }
    }
}
