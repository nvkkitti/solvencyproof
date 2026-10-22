using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SolvencyProof.Helpers;
using SolvencyProof.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace SolvencyProof.Services
{
    public class AuditProverService : IAuditProverService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AuditProverService> _logger;


        public AuditProverService(IServiceScopeFactory scopeFactory, ILogger<AuditProverService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
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

            audit.Status = AuditStatus.Proving;
            await db.SaveChangesAsync();

            // Az összegeket (decimal) át kell váltani a legkisebb oszthatatlan egységre (pl. egész számra).
            // Tegyük fel, hogy az értékünk 8 tizedesjegyet használ
            long totalLiabilitiesSats = (long)(audit.TotalLiabilities * 100_000_000m);
            long totalAssetsSats = (long)(audit.TotalAssets * 100_000_000m);

            // 1. Kötelezettségek (C_T)
            byte[] cT = PedersenHelper.CommitDeterministic(totalLiabilitiesSats);

            // 2. Fedezet (C_v) a generált vakító tényezővel (r)
            byte[] blindingFactor = CryptoHelper.GenerateRandomBytes(32);
            byte[] cV = PedersenHelper.Commit(totalAssetsSats, blindingFactor);

            // 3. Különbség homomorf kivonással (C_Delta)
            byte[] cDelta = PedersenHelper.SubtractCommitments(cV, cT);

            // (Dummy ZK proof marad egyelőre)
            byte[] zkProof = CryptoHelper.GenerateRandomBytes(256);

            audit.CommitmentCt = cT;
            audit.CommitmentCv = cV;
            audit.CommitmentCDelta = cDelta;
            audit.ZkProof = zkProof;
            audit.Status = AuditStatus.VerifiedOnChain;
            await db.SaveChangesAsync();
        }

        private static byte[] GenerateRandomBytes(int length)
        {
            return RandomNumberGenerator.GetBytes(length);
        }
    }
}