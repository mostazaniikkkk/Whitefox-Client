// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace WhiteFox.Core.Network
{
    /// <summary>Login cryptography of online mode (vanilla Crypt): the server hash and the server's RSA public key.</summary>
    public static class Crypto
    {
        /// <summary>
        /// SHA-1 of server id, shared secret and public key as Java's <c>new BigInteger(digest).toString(16)</c>: a signed
        /// two's complement number in hex without leading zeros, negative values with a minus sign.
        /// </summary>
        public static string ServerHash(string serverId, byte[] sharedSecret, byte[] publicKey)
        {
            byte[] digest;
            using (var sha = SHA1.Create())
            {
                var id = Encoding.ASCII.GetBytes(serverId);
                sha.TransformBlock(id, 0, id.Length, null, 0);
                sha.TransformBlock(sharedSecret, 0, sharedSecret.Length, null, 0);
                sha.TransformFinalBlock(publicKey, 0, publicKey.Length);
                digest = sha.Hash;
            }
            return JavaHex(digest);
        }

        public static string JavaHex(byte[] bigEndian)
        {
            var little = (byte[])bigEndian.Clone();
            Array.Reverse(little);
            var value = new BigInteger(little);
            var negative = value.Sign < 0;
            var hex = BigInteger.Abs(value).ToString("x").TrimStart('0');
            if (hex.Length == 0) hex = "0";
            return negative ? "-" + hex : hex;
        }

        /// <summary>RSA key from the X.509 SubjectPublicKeyInfo (DER) the server sends.</summary>
        public static RSA PublicKey(byte[] der)
        {
            var reader = new DerReader(der);
            reader.Enter(0x30); // SubjectPublicKeyInfo
            reader.Skip(0x30); // AlgorithmIdentifier (rsaEncryption)
            var bits = reader.Read(0x03);
            // the bit string starts with the number of unused bits (0) and holds RSAPublicKey
            var key = new DerReader(bits, 1);
            key.Enter(0x30);
            var modulus = TrimSign(key.Read(0x02));
            var exponent = TrimSign(key.Read(0x02));
            var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
            return rsa;
        }

        private static byte[] TrimSign(byte[] integer)
        {
            var start = 0;
            while (start < integer.Length - 1 && integer[start] == 0) start++;
            var result = new byte[integer.Length - start];
            Array.Copy(integer, start, result, 0, result.Length);
            return result;
        }

        private sealed class DerReader
        {
            private readonly byte[] data;
            private int position;

            public DerReader(byte[] data, int position = 0)
            {
                this.data = data;
                this.position = position;
            }

            private int Header(int tag)
            {
                if (position >= data.Length || data[position] != tag) throw new InvalidDataException("Invalid public key (expected DER tag " + tag + ")");
                position++;
                int length = data[position++];
                if ((length & 0x80) != 0)
                {
                    var count = length & 0x7F;
                    length = 0;
                    for (var i = 0; i < count; i++) length = (length << 8) | data[position++];
                }
                return length;
            }

            public void Enter(int tag) => Header(tag);

            public void Skip(int tag)
            {
                var length = Header(tag);
                position += length;
            }

            public byte[] Read(int tag)
            {
                var length = Header(tag);
                var result = new byte[length];
                Array.Copy(data, position, result, 0, length);
                position += length;
                return result;
            }
        }
    }
}
