using System;
using System.Security.Cryptography;
using System.Text;

namespace BetterMouse
{
    internal static class Crypto
    {
        const int KdfIterations = 100_000;
        static readonly byte[] KdfSalt = Encoding.ASCII.GetBytes("BetterMouseWithoutBorders/v1");
        static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        /// <summary>Stretches the shared security key; done once per settings change.</summary>
        public static byte[] DeriveMasterKey(string securityKey)
        {
            var password = Encoding.UTF8.GetBytes(securityKey.Trim());
            using (var kdf = new Rfc2898DeriveBytes(password, KdfSalt, KdfIterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(32);
        }

        public static byte[] Hmac(byte[] key, params byte[][] parts)
        {
            using (var h = new HMACSHA256(key))
            {
                foreach (var p in parts) h.TransformBlock(p, 0, p.Length, null, 0);
                h.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return h.Hash;
            }
        }

        public static byte[] Label(string text) => Encoding.ASCII.GetBytes(text);

        public static byte[] RandomBytes(int count)
        {
            var b = new byte[count];
            lock (Rng) Rng.GetBytes(b);
            return b;
        }

        public static bool FixedTimeEquals(byte[] a, int aOff, byte[] b, int bOff, int count)
        {
            int diff = 0;
            for (int i = 0; i < count; i++) diff |= a[aOff + i] ^ b[bOff + i];
            return diff == 0;
        }

        /// <summary>Human-friendly random key, e.g. "K7QP-3XZM-WD9R-H2TF".</summary>
        public static string GenerateSecurityKey()
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I
            var bytes = RandomBytes(16);
            var sb = new StringBuilder();
            for (int i = 0; i < 16; i++)
            {
                if (i > 0 && i % 4 == 0) sb.Append('-');
                sb.Append(alphabet[bytes[i] % alphabet.Length]);
            }
            return sb.ToString();
        }
    }

    /// <summary>AES-256 in counter mode, as one continuous key stream per direction.</summary>
    internal sealed class CtrCipher : IDisposable
    {
        const int BatchBlocks = 64;
        readonly Aes aes;
        readonly ICryptoTransform ecb;
        readonly byte[] counterBlocks = new byte[BatchBlocks * 16];
        readonly byte[] keyStream = new byte[BatchBlocks * 16];
        int position;
        ulong counter;

        public CtrCipher(byte[] key)
        {
            aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            ecb = aes.CreateEncryptor();
            position = keyStream.Length;
        }

        public void Process(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (position == keyStream.Length) Refill();
                buffer[offset + i] ^= keyStream[position++];
            }
        }

        void Refill()
        {
            for (int b = 0; b < BatchBlocks; b++)
            {
                ulong c = counter++;
                int o = b * 16 + 8;
                for (int i = 7; i >= 0; i--) { counterBlocks[o + i] = (byte)c; c >>= 8; }
            }
            ecb.TransformBlock(counterBlocks, 0, counterBlocks.Length, keyStream, 0);
            position = 0;
        }

        public void Dispose()
        {
            ecb.Dispose();
            aes.Dispose();
        }
    }

    /// <summary>
    /// Encrypt-then-MAC framing. Wire format per frame:
    /// [uint32 length][ciphertext][16-byte HMAC-SHA256(seq || length || ciphertext)].
    /// The implicit sequence number stops replay, reordering and dropping of frames.
    /// </summary>
    internal sealed class SecureChannel : IDisposable
    {
        public const int TagSize = 16;
        readonly CtrCipher sendCipher, receiveCipher;
        readonly HMACSHA256 sendMac, receiveMac;
        ulong sendSeq, receiveSeq;

        public SecureChannel(byte[] sendEncKey, byte[] sendMacKey, byte[] receiveEncKey, byte[] receiveMacKey)
        {
            sendCipher = new CtrCipher(sendEncKey);
            receiveCipher = new CtrCipher(receiveEncKey);
            sendMac = new HMACSHA256(sendMacKey);
            receiveMac = new HMACSHA256(receiveMacKey);
        }

        /// <summary>Not thread-safe: call from a single writer thread.</summary>
        public byte[] Seal(byte[] plain)
        {
            var frame = new byte[4 + plain.Length + TagSize];
            WriteUInt32(frame, 0, (uint)plain.Length);
            Buffer.BlockCopy(plain, 0, frame, 4, plain.Length);
            sendCipher.Process(frame, 4, plain.Length);
            var tag = ComputeTag(sendMac, sendSeq++, frame, 4, plain.Length);
            Buffer.BlockCopy(tag, 0, frame, 4 + plain.Length, TagSize);
            return frame;
        }

        /// <summary>
        /// Verifies and decrypts in place. <paramref name="buffer"/> holds the ciphertext at
        /// [offset, offset+length) followed by the tag. Not thread-safe: single reader thread.
        /// </summary>
        public bool Open(byte[] buffer, int offset, int length)
        {
            var tag = ComputeTag(receiveMac, receiveSeq, buffer, offset, length);
            if (!Crypto.FixedTimeEquals(tag, 0, buffer, offset + length, TagSize)) return false;
            receiveSeq++;
            receiveCipher.Process(buffer, offset, length);
            return true;
        }

        byte[] ComputeTag(HMACSHA256 mac, ulong seq, byte[] buffer, int offset, int length)
        {
            lock (mac)
            {
                var h = new byte[12];
                for (int i = 0; i < 8; i++) h[i] = (byte)(seq >> (8 * i));
                WriteUInt32(h, 8, (uint)length);
                mac.Initialize();
                mac.TransformBlock(h, 0, h.Length, null, 0);
                mac.TransformFinalBlock(buffer, offset, length);
                return mac.Hash;
            }
        }

        public static void WriteUInt32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        public static uint ReadUInt32(byte[] b, int o) =>
            (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);

        public void Dispose()
        {
            sendCipher.Dispose();
            receiveCipher.Dispose();
            sendMac.Dispose();
            receiveMac.Dispose();
        }
    }
}
