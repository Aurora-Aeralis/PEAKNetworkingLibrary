using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NetworkingLibrary.Modules
{
    public static class ModId
    {
        public static uint FromGuid(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) throw new ArgumentException("ModId requires a non-empty mod identifier.", nameof(guid));
            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(guid.Trim().ToLowerInvariant()));
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }

        public static uint FromGuidV2(string guid)
        {
            var guidValue = ParseGuid(guid);
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(guidValue.ToString("D")));
            return FoldHashToUInt32(hash);
        }

        private static Guid ParseGuid(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) throw new ArgumentException("ModId requires a non-empty GUID string (for example, \"123e4567-e89b-12d3-a456-426614174000\").", nameof(guid));
            if (!Guid.TryParse(guid, out var guidValue)) throw new ArgumentException("ModId requires a valid GUID string in a recognized format.", nameof(guid));
            return guidValue;
        }

        private static uint FoldHashToUInt32(ReadOnlySpan<byte> hashBytes)
        {
            if (hashBytes.Length == 0 || hashBytes.Length % sizeof(uint) != 0) throw new ArgumentException("Hash bytes must be non-empty and divisible by four.", nameof(hashBytes));
            var folded = 0u;
            for (var index = 0; index < hashBytes.Length; index += sizeof(uint)) folded ^= BinaryPrimitives.ReadUInt32LittleEndian(hashBytes.Slice(index, sizeof(uint)));
            return folded;
        }
    }
}
