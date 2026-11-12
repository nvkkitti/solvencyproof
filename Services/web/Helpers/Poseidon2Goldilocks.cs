using System;
using System.Numerics;
using System.Security.Cryptography;

namespace SolvencyProof.Helpers
{
    /// <summary>
    /// Poseidon2 over the Goldilocks field (width 12), a port of the Rust prover's hash (calc/src/hash.rs).
    /// Both sides must produce byte-identical digests: the prover recomputes the Merkle root and the
    /// assets commitment and refuses to prove if they differ from the ones computed here.
    /// (A Rust prover hash-ének portja; a két oldalnak bájtra azonos digestet kell adnia.)
    /// </summary>
    public static class Poseidon2Goldilocks
    {
        /// <summary>The Goldilocks prime 2^64 - 2^32 + 1 (A Goldilocks prímszám).</summary>
        public const ulong Modulus = 0xFFFFFFFF00000001UL;

        public const int Width = 12;
        public const int DigestElements = 4;
        public const int DigestBytes = 32;

        // Domain separation tags (Tartomány-elválasztó címkék), identical to the Rust side.
        private const ulong TagLeaf = 1;
        private const ulong TagNode = 2;
        private const ulong TagCommit = 3;

        // ---- field arithmetic (modulo Modulus) ----

        public static ulong Add(ulong a, ulong b)
        {
            ulong sum = unchecked(a + b);
            // 2^64 = Modulus + (2^32 - 1), so a wrapped sum is corrected by 2^32 - 1 (túlcsordulás esetén)
            if (sum < a)
            {
                return unchecked(sum + 0xFFFFFFFFUL);
            }

            return sum >= Modulus ? sum - Modulus : sum;
        }

        private static ulong Sub(ulong a, ulong b) => a >= b ? a - b : Modulus - (b - a);

        public static ulong Mul(ulong a, ulong b) => (ulong)((UInt128)a * b % Modulus);

        private static ulong Pow7(ulong x)
        {
            ulong x2 = Mul(x, x);
            ulong x3 = Mul(x2, x);
            ulong x4 = Mul(x2, x2);
            return Mul(x3, x4);
        }

        /// <summary>Reduces any 64-bit value into the field (Bármely 64 bites érték redukálása a testbe).</summary>
        public static ulong Reduce(ulong value) => value >= Modulus ? value - Modulus : value;

        // ---- the permutation ----

        /// <summary>The Poseidon2 permutation, in place (A Poseidon2 permutáció, helyben).</summary>
        public static void Permute(ulong[] state)
        {
            if (state.Length != Width)
            {
                throw new ArgumentException($"A Poseidon2 állapot hossza {Width} kell legyen.", nameof(state));
            }

            MdsLight(state);
            foreach (ulong[] constants in Poseidon2GoldilocksConstants.ExternalInitial)
            {
                ExternalRound(state, constants);
            }

            foreach (ulong constant in Poseidon2GoldilocksConstants.Internal)
            {
                state[0] = Pow7(Add(state[0], constant));
                InternalLinearLayer(state);
            }

            foreach (ulong[] constants in Poseidon2GoldilocksConstants.ExternalFinal)
            {
                ExternalRound(state, constants);
            }
        }

        private static void ExternalRound(ulong[] state, ulong[] constants)
        {
            for (int i = 0; i < Width; i++)
            {
                state[i] = Pow7(Add(state[i], constants[i]));
            }

            MdsLight(state);
        }

        // The 4x4 block matrix [[2 3 1 1] [1 2 3 1] [1 1 2 3] [3 1 1 2]], then the sum of every column class is added back
        // (a 4x4 blokkmátrix, majd az oszloposztályok összegének hozzáadása)
        private static void MdsLight(ulong[] state)
        {
            for (int chunk = 0; chunk < Width; chunk += 4)
            {
                ulong x0 = state[chunk];
                ulong x1 = state[chunk + 1];
                ulong x2 = state[chunk + 2];
                ulong x3 = state[chunk + 3];

                ulong t01 = Add(x0, x1);
                ulong t23 = Add(x2, x3);
                ulong t0123 = Add(t01, t23);
                ulong t01123 = Add(t0123, x1);
                ulong t01233 = Add(t0123, x3);

                state[chunk + 3] = Add(t01233, Add(x0, x0));
                state[chunk + 1] = Add(t01123, Add(x2, x2));
                state[chunk] = Add(t01123, t01);
                state[chunk + 2] = Add(t01233, t23);
            }

            Span<ulong> sums = stackalloc ulong[4];
            for (int k = 0; k < 4; k++)
            {
                sums[k] = Add(Add(state[k], state[k + 4]), state[k + 8]);
            }

            for (int i = 0; i < Width; i++)
            {
                state[i] = Add(state[i], sums[i % 4]);
            }
        }

        // state[i] = state[i] * diagonal[i] + (sum of the whole state)
        private static void InternalLinearLayer(ulong[] state)
        {
            ulong sum = 0;
            for (int i = 0; i < Width; i++)
            {
                sum = Add(sum, state[i]);
            }

            for (int i = 0; i < Width; i++)
            {
                state[i] = Add(Mul(state[i], Poseidon2GoldilocksConstants.InternalDiagonal[i]), sum);
            }
        }

        // ---- compression, leaves, nodes, commitments ----

        /// <summary>Permutes the 12 inputs and keeps the first 4 elements (Permutáció, majd az első 4 elem megtartása).</summary>
        public static ulong[] Compress(ulong[] input)
        {
            ulong[] state = (ulong[])input.Clone();
            Permute(state);
            return [state[0], state[1], state[2], state[3]];
        }

        /// <summary>The digest of one account leaf (Egy számla-levél digestje).</summary>
        public static ulong[] HashLeaf(ulong[] id, ulong[] salt, ulong balance)
        {
            ulong[] input = new ulong[Width];
            Array.Copy(id, 0, input, 0, 4);
            input[4] = balance;
            Array.Copy(salt, 0, input, 5, 4);
            input[Width - 1] = TagLeaf;
            return Compress(input);
        }

        /// <summary>The digest of an inner node, committing to both children and their sums (Belső csomópont digestje).</summary>
        public static ulong[] HashNode(ulong[] left, ulong[] right, ulong leftSum, ulong rightSum)
        {
            ulong[] input = new ulong[Width];
            Array.Copy(left, 0, input, 0, 4);
            Array.Copy(right, 0, input, 4, 4);
            input[8] = leftSum;
            input[9] = rightSum;
            input[Width - 1] = TagNode;
            return Compress(input);
        }

        /// <summary>
        /// Hiding and binding commitment to the assets, Poseidon2(assets, r) with a 256-bit blinding factor r
        /// (Az eszközök elrejtő és kötő elkötelezettsége).
        /// </summary>
        public static byte[] CommitAssets(ulong assets, ulong[] blinding)
        {
            ulong[] input = new ulong[Width];
            input[0] = assets;
            Array.Copy(blinding, 0, input, 1, 4);
            input[Width - 1] = TagCommit;
            return DigestToBytes(Compress(input));
        }

        // ---- encodings shared with the Rust prover ----

        /// <summary>32 bytes: four canonical 64-bit values, big-endian, concatenated (Négy kanonikus 64 bites érték, big-endian).</summary>
        public static byte[] DigestToBytes(ulong[] digest)
        {
            byte[] bytes = new byte[DigestBytes];
            for (int i = 0; i < DigestElements; i++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(i * 8, 8), digest[i]);
            }

            return bytes;
        }

        public static ulong[] DigestFromBytes(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != DigestBytes)
            {
                throw new ArgumentException($"A digest pontosan {DigestBytes} bájt.", nameof(bytes));
            }

            ulong[] digest = new ulong[DigestElements];
            for (int i = 0; i < DigestElements; i++)
            {
                digest[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(i * 8, 8));
            }

            return digest;
        }

        /// <summary>
        /// Derives four field elements from 32 bytes, each from 8 bytes reduced modulo the prime
        /// (Négy testelem származtatása 32 bájtból).
        /// </summary>
        public static ulong[] FieldElementsFromBytes(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != DigestBytes)
            {
                throw new ArgumentException($"Pontosan {DigestBytes} bájt szükséges.", nameof(bytes));
            }

            ulong[] elements = new ulong[4];
            for (int i = 0; i < 4; i++)
            {
                elements[i] = Reduce(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(i * 8, 8)));
            }

            return elements;
        }

        /// <summary>Four 32-bit limbs of a GUID, so the 128-bit id fits into four field elements (Egy GUID négy 32 bites darabja).</summary>
        public static ulong[] GuidToElements(Guid id)
        {
            byte[] bytes = id.ToByteArray(bigEndian: true);
            ulong[] elements = new ulong[4];
            for (int i = 0; i < 4; i++)
            {
                elements[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i * 4, 4));
            }

            return elements;
        }

        /// <summary>Four field elements from an arbitrary-length salt, via SHA-256 (Tetszőleges hosszú sóból négy testelem).</summary>
        public static ulong[] SaltToElements(byte[] salt) => FieldElementsFromBytes(SHA256.HashData(salt));
    }
}
