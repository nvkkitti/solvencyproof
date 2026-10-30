using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using SolvencyProof.Models;

namespace SolvencyProof.Services
{
    public class MerkleTreeBuilder
    {
        /// <summary>
        /// Generate a single leaf node from a customer account (Egyetlen levélcsomópont generálása egy ügyfél számlájából)
        /// Hash= SHA256( Id bytes + Balance bytes + Salt )
        /// </summary>
        
        public static byte[] GenerateLeafHash(Account account)
        {
            byte[] idBytes = account.Id.ToByteArray();
            int[] balanceBits = decimal.GetBits(account.Balance);
            byte[] balanceBytes = new byte[sizeof(int) * balanceBits.Length];
            Buffer.BlockCopy(balanceBits, 0, balanceBytes, 0, balanceBytes.Length);
            byte[] salt = account.MerkleLeafSalt ?? Array.Empty<byte>();
            byte[] combinedBytes = new byte[idBytes.Length + balanceBytes.Length + salt.Length];

            Buffer.BlockCopy(idBytes, 0, combinedBytes, 0, idBytes.Length);
            Buffer.BlockCopy(balanceBytes, 0, combinedBytes, idBytes.Length, balanceBytes.Length);
            Buffer.BlockCopy(salt, 0, combinedBytes, idBytes.Length + balanceBytes.Length, salt.Length);

            return SHA256.HashData(combinedBytes);
        }

        /// <summary>
        /// Build the complete Merkle tree from the bottom up and return the root (A teljes Merkle-fa felépítése alulról felfelé, és a gyökér visszaadása)
        /// </summary>
        
        public static byte[] BuildTreeAndGetRoot(List<Account> accounts)
        {
            if (accounts == null || accounts.Count == 0)
                throw new ArgumentException("A Merkle-fa felépítése legalább egy ügyfélszámla szükséges.", nameof(accounts));

            // Generate leaves for all customers (Levelek legenerálása az összes ügyfélre)
            List<byte[]> currentLayer = accounts.Select(GenerateLeafHash).ToList();

            using (var sha256 = SHA256.Create())
            {
                // Build the Merkle tree upward until only one element (the root) remains (Felfelé építjük a Merkle-fát, amíg csak egy elem (a gyökér) marad)
                while (currentLayer.Count > 1)
                {
                    List<byte[]> nextLayer = new List<byte[]>();

                    // Process the nodes in pairs (Kettesével lépkedünk a csomópontokon)
                    for (int i = 0; i < currentLayer.Count; i += 2)
                    {
                        byte[] left = currentLayer[i];

                        // If the current layer has an odd number of elements, pair the last element with itself (Ha páratlan számú elem van az adott rétegben, az utolsó elemet önmagával állítjuk párba)
                        byte[] right = i + 1 < currentLayer.Count ? currentLayer[i + 1] : left;

                        // Parent node: Hash(Left + Right) (Szülő csomópont: Hash(Left + Right))
                        byte[] combined = new byte[left.Length + right.Length];
                        Buffer.BlockCopy(left, 0, combined, 0, left.Length);
                        Buffer.BlockCopy(right, 0, combined, left.Length, right.Length);

                        nextLayer.Add(sha256.ComputeHash(combined));
                    }
                    // The parent layer just calculated becomes the starting layer for the next iteration (a most kiszámolt szülő réteg lesz a következő iterációban a kiinduló rétege)
                    currentLayer = nextLayer;
                }
                // The last remaining element is the Merkle root, which we return (A legutlsó megmaradt elem a Merkle Root, amit visszaadunk)
                return currentLayer[0];
            }
        }
    }
}
