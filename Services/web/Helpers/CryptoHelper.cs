using System;
using System.Security.Cryptography;

namespace SolvencyProof.Helpers
{
    public static class CryptoHelper
    {
        public static byte[] GenerateRandomBytes(int length)
        {
            var bytes = new byte[length];
            RandomNumberGenerator.Fill(bytes);
            return bytes;
        }
    }
}