using System;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Math.EC;

namespace SolvencyProof.Helpers
{
    public static class PedersenHelper
    {
        private static readonly ECCurve Curve;
        private static readonly ECPoint G;
        private static readonly ECPoint H;
        private static readonly Org.BouncyCastle.Asn1.X9.X9ECParameters CurveParams;

        static PedersenHelper()
        {
            //Secp256k1 görbe inicializálása
            CurveParams = SecNamedCurves.GetByName("secp256k1");
            Curve = CurveParams.Curve;
            G = CurveParams.G;

            //'H' pontnak egy olyan pontnak kell lennire a görbén, aminek a G-hez viszonyított diszkrét logaritmusát senki sem ismeri
            //Itt egy sztenderd, ismert és biztonságos H pontot használok.
            byte[] hBytes = Convert.FromHexString("0250929b74c1a04954b78b4b6035e97a5e078a5a0f28ec96d547bfee9ace803ac0");
            H = Curve.DecodePoint(hBytes);
        }

        //C_T = T * G (Kötelezettségek determinisztikus elkötelezettségje)
        public static byte[] CommitDeterministic(long amount)
        {
            BigInteger val = new BigInteger(amount.ToString());

            //Pont szorzás
            ECPoint point = G.Multiply(val);

            //33 bájtos tömörített formátum visszaadása
            return point.GetEncoded(compressed: true);
        }

        //C_V = v * G + r * H (Fedezet elkötelezettsége vakító tényezővel)
        public static byte[] Commit(long amount, byte[] blindingFactor)
        {
            BigInteger val = new BigInteger(amount.ToString());

            //Az '1' biztosítja, hogy a BigInteger pozitív legyen, mivel a blindingFactor byte tömb lehet nullával kezdődő
            BigInteger r = new BigInteger(1, blindingFactor);
            ECPoint vG = G.Multiply(val);
            ECPoint rH = H.Multiply(r);

            //Pontok összeadása
            ECPoint commitment = vG.Add(rH);
            return commitment.GetEncoded(compressed: true);
        }

        //C_Δ = C_V - C_T (Homomorf kivonás)
        public static byte[] SubtractCommitments(byte[] cVBytes, byte[] cTBytes)
        {
            ECPoint cV = Curve.DecodePoint(cVBytes);
            ECPoint cT = Curve.DecodePoint(cTBytes);

            //Kivonás a görbén: Hozzáadjuk a kivonandó pont negáltját (Invertált Y koordináta)
            ECPoint negCT = cT.Negate();
            ECPoint cDelta = cV.Add(negCT);

            return cDelta.GetEncoded(compressed: true);
        }
    }
}
