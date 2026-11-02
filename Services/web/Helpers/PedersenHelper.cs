using System;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;

namespace SolvencyProof.Helpers
{
    public static class PedersenHelper
    {
        private static readonly ECCurve s_curve;
        private static readonly ECPoint s_generatorPoint;
        private static readonly ECPoint s_blindingPoint;
        private static readonly Org.BouncyCastle.Asn1.X9.X9ECParameters s_curveParameters;

        static PedersenHelper()
        {
            // Initialize the Secp256k1 curve (Secp256k1 görbe inicializálása)
            s_curveParameters = SecNamedCurves.GetByName("secp256k1");
            s_curve = s_curveParameters.Curve;
            s_generatorPoint = s_curveParameters.G;

            //'H' must be a point on the curve whose discrete logarithm relative to G is unknown to anyone ('H' pontnak egy olyan pontnak kell lennire a görbén, aminek a G-hez viszonyított diszkrét logaritmusát senki sem ismeri)
            // Use a standard, known, and secure H point here (Itt egy sztenderd, ismert és biztonságos H pontot használok)
            byte[] hBytes = Convert.FromHexString("0250929b74c1a04954b78b4b6035e97a5e078a5a0f28ec96d547bfee9ace803ac0");
            s_blindingPoint = s_curve.DecodePoint(hBytes);
        }

        // C_T = T * G (Deterministic commitment to liabilities; Kötelezettségek determinisztikus elkötelezettségje)
        public static byte[] CommitDeterministic(long amountInSmallestUnit)
        {
            BigInteger amount = new BigInteger(amountInSmallestUnit.ToString());

            // Point multiplication (Pont szorzás)
            ECPoint point = s_generatorPoint.Multiply(amount);

            // Return the 33-byte compressed format (33 bájtos tömörített formátum visszaadása)
            return point.GetEncoded(compressed: true);
        }

        // C_V = v * G + r * H (Commitment to assets with a blinding factor; Fedezet elkötelezettsége vakító tényezővel)
        public static byte[] Commit(long amountInSmallestUnit, byte[] blindingFactor)
        {
            BigInteger amount = new BigInteger(amountInSmallestUnit.ToString());

            // The '1' ensures that BigInteger is positive because the blindingFactor byte array may begin with zero (Az '1' biztosítja, hogy a BigInteger pozitív legyen, mivel a blindingFactor byte tömb lehet nullával kezdődő)
            BigInteger r = new BigInteger(1, blindingFactor);
            ECPoint assetAmountPoint = s_generatorPoint.Multiply(amount);
            ECPoint blindingFactorPoint = s_blindingPoint.Multiply(r);

            // Point addition (Pontok összeadása)
            ECPoint commitment = assetAmountPoint.Add(blindingFactorPoint);
            return commitment.GetEncoded(compressed: true);
        }

        // C_Δ = C_V - C_T (Homomorphic subtraction; Homomorf kivonás)
        public static byte[] SubtractCommitments(byte[] assetCommitmentBytes, byte[] liabilityCommitmentBytes)
        {
            ECPoint assetCommitment = s_curve.DecodePoint(assetCommitmentBytes);
            ECPoint liabilityCommitment = s_curve.DecodePoint(liabilityCommitmentBytes);

            // Subtraction on the curve: add the negation of the point being subtracted (Kivonás a görbén: Hozzáadjuk a kivonandó pont negáltját (Invertált Y koordináta))
            ECPoint negativeLiabilityCommitment = liabilityCommitment.Negate();
            ECPoint differenceCommitment = assetCommitment.Add(negativeLiabilityCommitment);

            return differenceCommitment.GetEncoded(compressed: true);
        }
    }
}
