using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SolvencyProof.Models;
//using ProofOfSolvencyDbContext.Data;

namespace SolvencyProof.Services
{
    public class MerkleTreeBuilder
    {
        /// <summary>
        /// Egyetlen levélcsomópont generálása egy ügyfél számlájából
        /// Hash= SHA256( Id + Balance + Salt )
        /// </summary>
        
        public static byte[] GenerateLeafHash(Account account)
        {
            using (var sha256 = SHA256.Create())
            {
                //Az adatok sztringgé fűzése
                string baseData = $"{account.Id}{account.Balance.ToString(CultureInfo.InvariantCulture)}";
                byte[] baseBytes = Encoding.UTF8.GetBytes(baseData);

                //A véletlen só hozzáfűzése a brute-force támadás kivédésére
                byte[] combinedBytes = new byte[baseBytes.Length + account.MerkleLeafSalt.Length];
                Buffer.BlockCopy(baseBytes, 0, combinedBytes, 0, baseBytes.Length);
                Buffer.BlockCopy(account.MerkleLeafSalt, 0, combinedBytes, baseBytes.Length, account.MerkleLeafSalt.Length);

                // A levél végleges hashelése
                return sha256.ComputeHash(combinedBytes);
            }
        }

        /// <summary>
        /// A teljes Merkle-fa felépítése alulról felfelé, és a gyökér visszaadása
        /// </summary>
        
        public static byte[] BuildTreeAndGetRoot(List<Account> accounts)
        {
            if (accounts == null || accounts.Count == 0)
                throw new ArgumentException("A Merkle-fa felépítése legalább egy ügyfélszámla szükséges.", nameof(accounts));

            //Levelek legenerálása az összes ügyfélre
            List<byte[]> currentLayer = accounts.Select(GenerateLeafHash).ToList();

            using (var sha256 = SHA256.Create())
            {
                //Felfelé építjük a Merkle-fát, amíg csak egy elem (a gyökér) marad
                while (currentLayer.Count > 1)
                {
                    List<byte[]> nextLayer = new List<byte[]>();

                    //Kettesével lépkedünk a csomópontokon
                    for (int i = 0; i < currentLayer.Count; i += 2)
                    {
                        byte[] left = currentLayer[i];

                        //Ha páratlan számú elem van az adott rétegben, az utolsó elemet önmagával állítjuk párba
                        byte[] right = i + 1 < currentLayer.Count ? currentLayer[i + 1] : left;

                        //Szülő csomópont: Hash(Left + Right)
                        byte[] combined = new byte[left.Length + right.Length];
                        Buffer.BlockCopy(left, 0, combined, 0, left.Length);
                        Buffer.BlockCopy(right, 0, combined, left.Length, right.Length);

                        nextLayer.Add(sha256.ComputeHash(combined));
                    }
                    //a most kiszámolt szülő réteg lesz a következő iterációban a kiinduló rétege
                    currentLayer = nextLayer;
                }
                //A legutlsó megmaradt elem a Merkle Root, amit visszaadunk
                return currentLayer[0];
            }
        }
    }
}
