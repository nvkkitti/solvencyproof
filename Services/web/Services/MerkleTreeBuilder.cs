using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using SolvencyProof.Helpers;
using SolvencyProof.Models;

namespace SolvencyProof.Services
{
    /// <summary>A node of the sum tree: the Poseidon2 digest and the sum of all balances below it (A fa csomópontja: digest és az alatta lévő egyenlegek összege).</summary>
    public sealed record SumNode(ulong[] Digest, ulong Sum);

    /// <summary>One sibling on the path to the root (Egy testvér a gyökérig vezető úton).</summary>
    /// <param name="Sibling">The sibling node (A testvér csomópont)</param>
    /// <param name="IsRightChild">True when the proven node is the right child, so the sibling is the left operand (Igaz, ha a bizonyított csomópont a jobb gyerek)</param>
    public sealed record MerkleProofStep(SumNode Sibling, bool IsRightChild);

    public sealed record MerkleInclusionProof(SumNode Leaf, IReadOnlyList<MerkleProofStep> Path, SumNode Root);

    /// <summary>The private data of one leaf, as field elements (Egy levél titkos adatai testelemekként).</summary>
    public sealed record ZkLeaf(ulong[] Id, ulong[] Salt, ulong Balance);

    /// <summary>The whole tree, level by level; Levels[0] are the leaves (A teljes fa szintenként).</summary>
    public sealed class SumTree
    {
        public SumTree(int depth, IReadOnlyList<SumNode[]> levels)
        {
            Depth = depth;
            Levels = levels;
        }

        public int Depth { get; }

        public IReadOnlyList<SumNode[]> Levels { get; }

        public SumNode Root => Levels[Depth][0];

        /// <summary>The sibling path from leaf <paramref name="index"/> up to the root (A testvér-útvonal a levéltől a gyökérig).</summary>
        public IReadOnlyList<MerkleProofStep> Path(int index)
        {
            List<MerkleProofStep> path = new List<MerkleProofStep>(Depth);
            for (int level = 0; level < Depth; level++)
            {
                int position = index >> level;
                path.Add(new MerkleProofStep(Levels[level][position ^ 1], (position & 1) == 1));
            }

            return path;
        }
    }

    /// <summary>
    /// Builds the liabilities Merkle sum tree the zk-STARK is proven over (A zk-STARK által bizonyított kötelezettség-összegfa).
    /// Leaf = Poseidon2(id, balance, salt), node = Poseidon2(left, right, sums). Because every node hash also commits to the sum of the
    /// balances below it, the root digest alone binds the total liabilities. The algorithm is identical to calc/src/hash.rs.
    /// </summary>
    public static class MerkleTreeBuilder
    {
        /// <summary>Amounts are proven in cents (Az összegeket centben bizonyítjuk).</summary>
        public const decimal AmountScale = 100m;

        /// <summary>A leaf balance must be below 2^BalanceBits, the STARK range-checks it (A STARK tartományellenőrzi).</summary>
        public const int BalanceBits = 40;

        /// <summary>The tree always has at least 2^MinDepth slots, empty ones are padded (A fának legalább 2^MinDepth helye van).</summary>
        public const int MinDepth = 6;

        public const int MaxDepth = 20;

        private static readonly ZkLeaf EmptyLeaf = new ZkLeaf(new ulong[4], new ulong[4], 0);

        /// <summary>The assets and the liabilities difference are range-checked to this many bits (Az eszközök és a különbség bitszáma).</summary>
        public const int AssetBits = 62;

        /// <summary>Converts an amount to the smallest unit; it must be a whole number of cents in [0, 2^bits) (Összeg átváltása a legkisebb egységre).</summary>
        public static ulong ToSmallestUnit(decimal amount, int bits = BalanceBits)
        {
            decimal scaled = amount * AmountScale;
            if (scaled != decimal.Truncate(scaled))
            {
                throw new InvalidOperationException($"A(z) {amount} összeg nem fejezhető ki egész centben.");
            }

            if (scaled < 0 || scaled >= (decimal)(1UL << bits))
            {
                throw new InvalidOperationException(
                    $"A(z) {amount} összeg kívül esik a bizonyítható tartományon (0 és {(decimal)(1UL << bits) / AmountScale} között kell lennie).");
            }

            return (ulong)scaled;
        }

        /// <summary>The leaf of an account (Egy számla levele).</summary>
        public static ZkLeaf ToLeaf(Account account) => new ZkLeaf(
            Poseidon2Goldilocks.GuidToElements(account.Id),
            Poseidon2Goldilocks.SaltToElements(account.MerkleLeafSalt ?? Array.Empty<byte>()),
            ToSmallestUnit(account.Balance));

        /// <summary>The smallest depth that fits <paramref name="leafCount"/> leaves, never below <see cref="MinDepth"/> (A legkisebb megfelelő mélység).</summary>
        public static int DepthFor(int leafCount)
        {
            int needed = 0;
            while ((1L << needed) < Math.Max(leafCount, 1))
            {
                needed++;
            }

            return Math.Max(needed, MinDepth);
        }

        public static SumNode HashLeaf(ZkLeaf leaf) =>
            new SumNode(Poseidon2Goldilocks.HashLeaf(leaf.Id, leaf.Salt, leaf.Balance), leaf.Balance);

        public static SumNode HashParent(SumNode left, SumNode right) => new SumNode(
            Poseidon2Goldilocks.HashNode(left.Digest, right.Digest, left.Sum, right.Sum),
            Poseidon2Goldilocks.Add(left.Sum, right.Sum));

        /// <summary>Builds the full tree; the leaf index is the position in <paramref name="leaves"/> (A teljes fa felépítése).</summary>
        public static SumTree Build(IReadOnlyList<ZkLeaf> leaves)
        {
            if (leaves == null || leaves.Count == 0)
            {
                throw new ArgumentException("A Merkle-fa felépítéséhez legalább egy ügyfélszámla szükséges.", nameof(leaves));
            }

            int depth = DepthFor(leaves.Count);
            if (depth > MaxDepth)
            {
                throw new InvalidOperationException($"Legfeljebb {1 << MaxDepth} számla bizonyítható egy fában.");
            }

            SumNode empty = HashLeaf(EmptyLeaf);
            SumNode[] level = new SumNode[1 << depth];
            for (int i = 0; i < level.Length; i++)
            {
                level[i] = i < leaves.Count ? HashLeaf(leaves[i]) : empty;
            }

            List<SumNode[]> levels = new List<SumNode[]> { level };
            while (level.Length > 1)
            {
                SumNode[] parents = new SumNode[level.Length / 2];
                for (int i = 0; i < parents.Length; i++)
                {
                    parents[i] = HashParent(level[2 * i], level[2 * i + 1]);
                }

                levels.Add(parents);
                level = parents;
            }

            return new SumTree(depth, levels);
        }

        public static SumTree Build(IEnumerable<Account> accounts) => Build(accounts.Select(ToLeaf).ToList());

        /// <summary>
        /// Build the inclusion proof of one account: the sibling nodes from its leaf up to the root.
        /// The accounts must be in the same order as when the root was built (Egy számla beillesztési bizonyítéka; a sorrend azonos kell legyen).
        /// </summary>
        public static MerkleInclusionProof BuildInclusionProof(IReadOnlyList<Account> accounts, Guid accountId)
        {
            int index = accounts.ToList().FindIndex(account => account.Id == accountId);
            if (index < 0)
            {
                throw new ArgumentException($"A(z) {accountId} számla nem szerepel a Merkle-fában.", nameof(accountId));
            }

            SumTree tree = Build(accounts);
            return new MerkleInclusionProof(tree.Levels[0][index], tree.Path(index), tree.Root);
        }

        /// <summary>Recompute the root from a leaf and its sibling path (A gyökér újraszámolása a levélből és az útvonalból).</summary>
        public static SumNode ComputeRootFromPath(SumNode leaf, IEnumerable<MerkleProofStep> path)
        {
            SumNode current = leaf;
            foreach (MerkleProofStep step in path)
            {
                current = step.IsRightChild ? HashParent(step.Sibling, current) : HashParent(current, step.Sibling);
            }

            return current;
        }

        /// <summary>Check the path against an expected root digest, for example the one read from the blockchain (Az útvonal ellenőrzése a blokkláncról olvasott gyökérrel).</summary>
        public static bool VerifyInclusionProof(SumNode leaf, IEnumerable<MerkleProofStep> path, byte[] expectedRootDigest)
        {
            byte[] computed = Poseidon2Goldilocks.DigestToBytes(ComputeRootFromPath(leaf, path).Digest);
            return CryptographicOperations.FixedTimeEquals(computed, expectedRootDigest);
        }

        /// <summary>The root digest as the 32 bytes that go on-chain (A gyökér digestje a láncra kerülő 32 bájtként).</summary>
        public static byte[] RootBytes(SumTree tree) => Poseidon2Goldilocks.DigestToBytes(tree.Root.Digest);
    }
}
