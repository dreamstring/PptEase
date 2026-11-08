using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace PptEase.Shader
{
    // Shared by the Framework COM host and the out-of-process renderer.
    // Office's COM Tags may uppercase values: never store raw source or base64.
    public static class ShaderPayload
    {
        public const int MaximumBytes = 4 * 1024 * 1024;
        public const int ChunkCharacters = 200;
        public const string ManifestKey = "PPTEASE_SHADER";

        public static void Write(string json, Func<string, string> read, Action<string, string> write, Action<string> remove = null)
        {
            string previous = read(ManifestKey);
            byte[] plain = new UTF8Encoding(false, true).GetBytes(json);
            if (plain.Length > MaximumBytes) throw new InvalidDataException("Shader component exceeds 4 MiB.");
            string encoded;
            using (var memory = new MemoryStream())
            {
                using (var zip = new DeflateStream(memory, CompressionMode.Compress, true)) zip.Write(plain, 0, plain.Length);
                encoded = Hex(memory.ToArray());
            }
            string generation = Guid.NewGuid().ToString("N").ToUpperInvariant();
            int count = (encoded.Length + ChunkCharacters - 1) / ChunkCharacters;
            for (int i = 0; i < count; i++)
            {
                string key = Key(generation, i);
                string value = encoded.Substring(i * ChunkCharacters, Math.Min(ChunkCharacters, encoded.Length - i * ChunkCharacters));
                write(key, value);
                if (!String.Equals(read(key), value, StringComparison.Ordinal)) throw new IOException("Shader tag write did not round-trip.");
            }
            // Publish only after all chunks verify; a failed update keeps the previous manifest usable.
            string digest;
            using (var sha = SHA256.Create()) digest = Hex(sha.ComputeHash(plain));
            string manifest = "1:" + generation + ":" + count + ":" + digest;
            write(ManifestKey, manifest);
            if (read(ManifestKey) != manifest) throw new IOException("Shader manifest write did not round-trip.");
            if (remove != null && !String.IsNullOrEmpty(previous))
            {
                string[] old = previous.Split(':'); int oldCount; Guid oldGeneration;
                if (old.Length == 4 && old[0] == "1" && Guid.TryParseExact(old[1], "N", out oldGeneration) && Int32.TryParse(old[2], out oldCount) && oldCount > 0 && oldCount <= MaximumBytes * 2 / ChunkCharacters + 1024)
                    for (int i = 0; i < oldCount; i++) { try { remove(Key(old[1], i)); } catch { } }
            }
        }

        public static string Read(Func<string, string> read)
        {
            string manifest = read(ManifestKey);
            if (String.IsNullOrEmpty(manifest)) return null;
            string[] parts = manifest.Split(':');
            int count;
            Guid generation;
            if (parts.Length != 4 || parts[0] != "1" || !Guid.TryParseExact(parts[1], "N", out generation) ||
                !Int32.TryParse(parts[2], out count) || count < 1 || count > (MaximumBytes * 2 / ChunkCharacters + 1024) || parts[3].Length != 64)
                throw new InvalidDataException("Unsupported or damaged Shader manifest.");
            var encoded = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                string chunk = read(Key(parts[1], i));
                if (String.IsNullOrEmpty(chunk) || chunk.Length > ChunkCharacters || (i < count - 1 && chunk.Length != ChunkCharacters))
                    throw new InvalidDataException("Missing or damaged Shader data.");
                encoded.Append(chunk);
            }
            string hex = encoded.ToString();
            if ((hex.Length & 1) != 0) throw new InvalidDataException("Invalid Shader encoding.");
            byte[] compressed = new byte[hex.Length / 2];
            for (int i = 0; i < compressed.Length; i++) compressed[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            byte[] plain;
            using (var source = new MemoryStream(compressed))
            using (var zip = new DeflateStream(source, CompressionMode.Decompress))
            using (var result = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int length;
                while ((length = zip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (result.Length + length > MaximumBytes) throw new InvalidDataException("Shader data exceeds size limit.");
                    result.Write(buffer, 0, length);
                }
                plain = result.ToArray();
            }
            using (var sha = SHA256.Create())
                if (!String.Equals(Hex(sha.ComputeHash(plain)), parts[3], StringComparison.Ordinal)) throw new InvalidDataException("Shader checksum mismatch.");
            return new UTF8Encoding(false, true).GetString(plain);
        }

        private static string Key(string generation, int index) { return "PES_" + generation + "_" + index.ToString("D5"); }
        private static string Hex(byte[] value) { return BitConverter.ToString(value).Replace("-", ""); }
    }
}
