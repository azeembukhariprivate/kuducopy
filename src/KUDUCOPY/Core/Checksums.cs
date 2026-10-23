using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Kuducopy.Core
{
    /// <summary>File checksums for "Verify copies" and "Save checksums".</summary>
    public static class Checksums
    {
        public static string Name(HashKind kind)
        {
            switch (kind)
            {
                case HashKind.Sha1: return "SHA-1";
                case HashKind.Sha256: return "SHA-256";
                case HashKind.Crc32: return "CRC32";
                default: return "MD5";
            }
        }

        public static string Extension(HashKind kind)
        {
            switch (kind)
            {
                case HashKind.Sha1: return ".sha1";
                case HashKind.Sha256: return ".sha256";
                case HashKind.Crc32: return ".sfv";
                default: return ".md5";
            }
        }

        public static HashAlgorithm Create(HashKind kind)
        {
            switch (kind)
            {
                case HashKind.Sha1: return SHA1.Create();
                case HashKind.Sha256: return SHA256.Create();
                case HashKind.Crc32: return new Crc32();
                default: return MD5.Create();
            }
        }

        /// <summary>Hashes a file, reporting each chunk read (for progress) and honouring pause and cancel.</summary>
        public static string HashFile(string path, HashKind kind, Action<long> read = null, CancellationToken cancel = default(CancellationToken),
            Func<bool> paused = null)
        {
            using (var algorithm = Create(kind))
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            {
                var buffer = new byte[1 << 20];
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    while (paused != null && paused()) Thread.Sleep(150);
                    cancel.ThrowIfCancellationRequested();
                    algorithm.TransformBlock(buffer, 0, n, null, 0);
                    read?.Invoke(n);
                }
                algorithm.TransformFinalBlock(new byte[0], 0, 0);
                return Hex(algorithm.Hash);
            }
        }

        public static string HashBytes(byte[] data, HashKind kind)
        {
            using (var algorithm = Create(kind)) return Hex(algorithm.ComputeHash(data));
        }

        private static string Hex(byte[] hash)
        {
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// Writes a checksum file next to the copies: "hash *relative\path" lines (md5sum style),
        /// or "relative\path HASH" lines for CRC32 (.sfv).
        /// </summary>
        public static string Write(string folder, HashKind kind, IEnumerable<KeyValuePair<string, string>> hashes, string fileName = null)
        {
            var path = Path.Combine(folder, (fileName ?? "KUDUCOPY checksums " + DateTime.Now.ToString("yyyy-MM-dd HHmmss")) + Extension(kind));
            var sb = new StringBuilder();
            sb.Append(kind == HashKind.Crc32 ? "; " : "# ").Append("Created by KUDUCOPY on ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\r\n");
            var root = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var pair in hashes)
            {
                var rel = pair.Key.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? pair.Key.Substring(root.Length) : pair.Key;
                if (kind == HashKind.Crc32) sb.Append(rel).Append(' ').Append(pair.Value.ToUpperInvariant()).Append("\r\n");
                else sb.Append(pair.Value).Append(" *").Append(rel).Append("\r\n");
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }
    }

    /// <summary>CRC-32 (IEEE 802.3), the checksum used in .sfv files.</summary>
    public sealed class Crc32 : HashAlgorithm
    {
        private static readonly uint[] Table = BuildTable();
        private uint _crc = 0xFFFFFFFF;

        public Crc32()
        {
            HashSizeValue = 32;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        public override void Initialize() => _crc = 0xFFFFFFFF;

        protected override void HashCore(byte[] array, int ibStart, int cbSize)
        {
            var crc = _crc;
            for (var i = ibStart; i < ibStart + cbSize; i++) crc = Table[(crc ^ array[i]) & 0xFF] ^ (crc >> 8);
            _crc = crc;
        }

        protected override byte[] HashFinal()
        {
            var value = ~_crc;
            return new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        }
    }
}
