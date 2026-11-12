using Microsoft.EntityFrameworkCore;
using SolvencyProof.Helpers;
using SolvencyProof.Models;
using SolvencyProof.Services.Blockchain;

namespace SolvencyProof.Services
{
    public enum InclusionStatus
    {
        Verified, // The path leads to the root stored on the blockchain (Az útvonal a blokkláncon tárolt gyökérhez vezet)
        AccountNotFound, // Unknown account id (Ismeretlen számlaazonosító)
        NoVerifiedAudit, // There is no audit confirmed on-chain yet (Még nincs a láncon megerősített audit)
        LegacyAudit, // The latest audit predates the zk-STARK tree, no path can match it (A legutóbbi audit a zk-STARK fa előtti)
        RootNotOnChain, // The chain holds no root for the latest audit (A láncon nincs gyökér a legutóbbi audithoz)
        RootMismatch, // The path does not lead to the on-chain root (Az útvonal nem a láncon lévő gyökérhez vezet)
        StarkProofInvalid // The path is fine, but the zk-STARK proof of the audit does not verify (Az útvonal rendben, de a zk-STARK bizonyíték nem ellenőrizhető)
    }

    public sealed record InclusionVerificationResult(
        InclusionStatus Status,
        Guid? AuditId = null,
        MerkleInclusionProof? Proof = null,
        byte[]? OnChainRoot = null,
        byte[]? RecomputedRoot = null,
        bool AccountChangedSinceAudit = false,
        bool? StarkProofValid = null);

    public sealed class AccountInclusionVerifier
    {
        private readonly IDbContextFactory<SolvencyProofContext> _dbFactory;
        private readonly HardhatProofSubmitterService _chain;
        private readonly ZkProofClient _zkProofClient;

        public AccountInclusionVerifier(
            IDbContextFactory<SolvencyProofContext> dbFactory,
            HardhatProofSubmitterService chain,
            ZkProofClient zkProofClient)
        {
            _dbFactory = dbFactory;
            _chain = chain;
            _zkProofClient = zkProofClient;
        }

        public async Task<InclusionVerificationResult> VerifyAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            await using SolvencyProofContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            // Same ordering as in AuditProverService, otherwise the leaf positions would differ (Ugyanaz a sorrend, mint az AuditProverService-ben, különben a levelek pozíciója eltérne)
            List<Account> accounts = await db.Account
                .AsNoTracking()
                .OrderBy(account => account.Id)
                .ToListAsync(cancellationToken);

            if (!accounts.Any(account => account.Id == accountId))
            {
                return new InclusionVerificationResult(InclusionStatus.AccountNotFound);
            }

            SolvencyAudit? audit = await db.SolvencyAudit
                .AsNoTracking()
                .Where(candidate => candidate.Status == AuditStatus.VerifiedOnChain)
                .OrderByDescending(candidate => candidate.Timestamp)
                .FirstOrDefaultAsync(cancellationToken);

            if (audit is null)
            {
                return new InclusionVerificationResult(InclusionStatus.NoVerifiedAudit);
            }

            if (audit.TreeDepth == 0)
            {
                return new InclusionVerificationResult(InclusionStatus.LegacyAudit, audit.Id);
            }

            // The root the customer compares against comes from the chain, not from the database (A gyökeret, amihez az ügyfél hasonlít, a lánc adja, nem az adatbázis)
            byte[]? onChainRoot = await _chain.GetVerifiedMerkleRootAsync(audit.Id);
            if (onChainRoot is null)
            {
                return new InclusionVerificationResult(InclusionStatus.RootNotOnChain, audit.Id);
            }

            MerkleInclusionProof proof = MerkleTreeBuilder.BuildInclusionProof(accounts, accountId);
            byte[] recomputedRoot = Poseidon2Goldilocks.DigestToBytes(
                MerkleTreeBuilder.ComputeRootFromPath(proof.Leaf, proof.Path).Digest);

            if (!MerkleTreeBuilder.VerifyInclusionProof(proof.Leaf, proof.Path, onChainRoot))
            {
                // If the stored audit root still equals the chain, the accounts were modified after the audit (Ha a tárolt audit gyökér még egyezik a lánccal, a számlák az audit után módosultak)
                bool changedSinceAudit = audit.MerkleRoot.AsSpan().SequenceEqual(onChainRoot);
                return new InclusionVerificationResult(
                    InclusionStatus.RootMismatch, audit.Id, proof, onChainRoot, recomputedRoot, changedSinceAudit);
            }

            // The on-chain root must also carry a valid zk-STARK proof: a >= T without revealing either (A láncon lévő gyökérhez érvényes zk-STARK bizonyíték is tartozik)
            bool? starkValid = await VerifyStarkProofAsync(audit, onChainRoot, cancellationToken);
            InclusionStatus status = starkValid == false ? InclusionStatus.StarkProofInvalid : InclusionStatus.Verified;
            return new InclusionVerificationResult(status, audit.Id, proof, onChainRoot, recomputedRoot, false, starkValid);
        }

        // Null when the prover service cannot be reached: the proof is then unchecked, not invalid (Null, ha a bizonyító szolgáltatás nem érhető el)
        private async Task<bool?> VerifyStarkProofAsync(SolvencyAudit audit, byte[] onChainRoot, CancellationToken cancellationToken)
        {
            if (audit.ZkProof.Length == 0 || audit.CommitmentCv.Length == 0)
            {
                return false;
            }

            try
            {
                VerifyProofResponse response = await _zkProofClient.VerifyProofAsync(
                    new VerifyProofRequest(
                        audit.TreeDepth,
                        "0x" + Convert.ToHexString(audit.ZkProof).ToLowerInvariant(),
                        "0x" + Convert.ToHexString(audit.CommitmentCv).ToLowerInvariant(),
                        "0x" + Convert.ToHexString(onChainRoot).ToLowerInvariant(),
                        audit.Id.ToString()),
                    cancellationToken);
                return response.Valid;
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }
    }
}
