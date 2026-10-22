using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using SolvencyProof.Models;

namespace SolvencyProof.Helpers
{
    public static class CryptoHelper
    {
        public static byte[] BuildMerkleRoot(List<Account> accounts)
        {
            if (!accounts.Any()) return new byte[32];

            using var sha256 = SHA256.Create();
            var leaves = new List<byte[]>();

            foreach (var account in accounts)
            {
                var idBytes = account.Id.ToByteArray();

                var balanceBits = decimal.GetBits(account.Balance);
                var balanceBytes = new byte[16];
                Buffer.BlockCopy(balanceBits, 0, balanceBytes, 0, 16);

                var salt = account.MerkleLeafSalt ?? Array.Empty<byte>();

                var combined = new byte[idBytes.Length + balanceBytes.Length + salt.Length];
                Buffer.BlockCopy(idBytes, 0, combined, 0, idBytes.Length);
                Buffer.BlockCopy(balanceBytes, 0, combined, idBytes.Length, balanceBytes.Length);
                Buffer.BlockCopy(salt, 0, combined, idBytes.Length + balanceBytes.Length, salt.Length);

                leaves.Add(sha256.ComputeHash(combined));
            }

            while (leaves.Count > 1)
            {
                var nextLevel = new List<byte[]>();
                for (int i = 0; i < leaves.Count; i += 2)
                {
                    var left = leaves[i];
                    var right = (i + 1 < leaves.Count) ? leaves[i + 1] : left;

                    var combined = new byte[left.Length + right.Length];
                    Buffer.BlockCopy(left, 0, combined, 0, left.Length);
                    Buffer.BlockCopy(right, 0, combined, left.Length, right.Length);

                    nextLevel.Add(sha256.ComputeHash(combined));
                }
                leaves = nextLevel;
            }

            return leaves[0];
        }

        public static byte[] GenerateRandomBytes(int length)
        {
            var bytes = new byte[length];
            RandomNumberGenerator.Fill(bytes);
            return bytes;
        }
    }
}