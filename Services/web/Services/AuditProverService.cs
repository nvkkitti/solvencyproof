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

            // The liabilities sum tree: Poseidon2 leaves, every node commits to the sum below it (A kötelezettség-összegfa)
            List<ZkLeaf> zkLeaves = accounts.Select(MerkleTreeBuilder.ToLeaf).ToList();
            SumTree tree = MerkleTreeBuilder.Build(zkLeaves);
            byte[] localMerkleRoot = MerkleTreeBuilder.RootBytes(tree);

            // The customer balances are the liabilities; the pipeline's asset figure is their sum, as before (Az ügyfélegyenlegek a kötelezettségek)
            audit.TotalLiabilities = accounts.Sum(account => account.Balance);
            audit.TotalAssets = accounts.Sum(account => account.Balance);
            audit.MerkleRoot = localMerkleRoot;
            audit.TreeDepth = tree.Depth;
            await db.SaveChangesAsync(cancellationToken);

            // Amounts are proven in cents (Az összegeket centben bizonyítjuk)
            ulong totalAssetsInSmallestUnit = MerkleTreeBuilder.ToSmallestUnit(audit.TotalAssets, MerkleTreeBuilder.AssetBits);

            // The assets commitment C_A = Poseidon2(assets, r) with a fresh 256-bit blinding factor r. It is computed here and
            // handed to the prover, which must reproduce it (Az eszközök elkötelezettsége, amit a bizonyítónak is elő kell állítania)
            byte[] blindingFactor = CryptoHelper.GenerateRandomBytes(32);
            ulong[] blinding = Poseidon2Goldilocks.FieldElementsFromBytes(blindingFactor);
            byte[] assetCommitment = Poseidon2Goldilocks.CommitAssets(totalAssetsInSmallestUnit, blinding);

            // Step 2: ask the Rust Axum prover for the zk-STARK proof (2. lépés: zk-STARK bizonyíték kérése a Rust Axum szervertől)
            Report(progress, 2, "Merkle-fa és zk-STARK bizonyíték kérése a Rust szervertől (127.0.0.1:3000)...");
            string institutionId = audit.Id.ToString();
            string merkleRootHex = ToHex(localMerkleRoot);
            string commitmentHex = ToHex(assetCommitment);

            // The witness goes to the institution's own prover over loopback; only the proof and public inputs leave it
            // (A tanú az intézmény saját bizonyítójához megy loopbacken, csak a bizonyíték és a nyilvános bemenetek hagyják el)
            EvmProofResponse proofData = await _zkProofClient.GenerateProofAsync(
                new ProofRequest(
                    institutionId,
                    totalAssetsInSmallestUnit,
                    blinding,
                    commitmentHex,
                    merkleRootHex,
                    zkLeaves.Select(leaf => new ZkLeafRequest(leaf.Id, leaf.Salt, leaf.Balance)).ToArray()),
                cancellationToken);

            if (proofData.ProofElements.Length == 0 || proofData.PublicInputs.Length < 2 || !proofData.Verified)
            {
                throw new InvalidOperationException("A Rust szerver hiányos vagy nem ellenőrzött bizonyítékot adott vissza.");
            }

            // The commitment and the root computed by Rust must match the ones computed here (A Rust által számolt értékeknek egyezniük kell a helyben számoltakkal)
            if (!string.Equals(proofData.PublicInputs[0], commitmentHex, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"A Rust és a C# eszköz-elkötelezettsége eltér: {proofData.PublicInputs[0]} != {commitmentHex}");
            }

            if (!string.Equals(proofData.PublicInputs[1], merkleRootHex, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"A Rust és a C# Merkle-gyökere eltér: {proofData.PublicInputs[1]} != {merkleRootHex}");
            }

            var proofElements = proofData.ProofElements.Select(HardhatProofSubmitterService.ParseUint256).ToArray();
            var publicInputs = proofData.PublicInputs.Select(HardhatProofSubmitterService.ParseUint256).ToArray();

            audit.CommitmentCv = assetCommitment;
            audit.CommitmentCt = Array.Empty<byte>();
            audit.CommitmentCDelta = Array.Empty<byte>();
            audit.ZkProof = Convert.FromHexString(proofData.Proof[2..]);
            db.CryptographicSecrets.Add(new CryptographicSecret
            {
                Id = Guid.NewGuid(),
                SolvencyAuditId = audit.Id,
                BlindingFactorR = _blindingFactorProtector.Protect(blindingFactor)
            });
            await db.SaveChangesAsync(cancellationToken);

            // Step 3: send the transaction to the Hardhat chain (3. lépés: tranzakció küldése a Hardhat láncra)
            Report(progress, 3, "submitAndVerifyProof tranzakció küldése a Hardhat láncra (127.0.0.1:8545)...");
            byte[] institutionIdBytes = HardhatProofSubmitterService.ComputeInstitutionId(audit.Id);
            string transactionHash = await _proofSubmitter.SendProofAsync(institutionIdBytes, proofElements, publicInputs);

            // Step 4: the contract verifies the proof on-chain via MockStarkVerifier (4. lépés: on-chain ellenőrzés)
            Report(progress, 4, $"On-chain ellenőrzés, tranzakció: {transactionHash}");
            await _proofSubmitter.WaitForVerificationAsync(transactionHash, cancellationToken);

            audit.Status = AuditStatus.VerifiedOnChain;
            await db.SaveChangesAsync(cancellationToken);

            Report(progress, AuditProgressReport.TotalSteps + 1, $"Az audit sikeres. Tranzakciós hash: {transactionHash}");
            return new AuditProofResult(transactionHash, merkleRootHex);
        }

        private static string ToHex(byte[] bytes) => "0x" + Convert.ToHexString(bytes).ToLowerInvariant();

        private void Report(IProgress<AuditProgressReport>? progress, int stepIndex, string message)
        {
            _logger.LogInformation("Audit {Step}. lépés: {Message}", stepIndex, message);
            progress?.Report(new AuditProgressReport { StepIndex = stepIndex, Message = message });
        }
    }
}
