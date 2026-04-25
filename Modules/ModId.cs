using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NetworkingLibrary.Modules
{
    public static class ModId
    {
        /// <summary>
        /// Derives a 32-bit mod id from a GUID using the legacy MD5-based strategy.
        /// </summary>
        /// <remarks>
        /// This method is kept for wire compatibility with existing deployments.
        /// Use <see cref="FromGuidV2"/> for new systems that can safely migrate to the SHA-256-based derivation.
        /// </remarks>
        public static uint FromGuid(string guid)
        {
            var guidValue = ParseGuid(guid);
            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(guidValue.ToString("D")));
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }

        /// <summary>
        /// Derives a 32-bit mod id from a GUID using SHA-256 over the normalized GUID text and XOR-folding to 32 bits.
        /// </summary>
        /// <remarks>
        /// Migration guidance:
        /// <list type="bullet">
        /// <item><description>Keep using <see cref="FromGuid"/> when peers still exchange legacy ids on the wire.</description></item>
        /// <item><description>Adopt <see cref="FromGuidV2"/> only after all producers/consumers agree on the v2 derivation strategy.</description></item>
        /// </list>
        /// </remarks>
        public static uint FromGuidV2(string guid)
        {
            var guidValue = ParseGuid(guid);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(guidValue.ToString("D")));
            return FoldHashToUInt32(hash);
        }

        private static Guid ParseGuid(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) throw new ArgumentException("ModId.FromGuid requires a non-empty GUID string (for example, \"123e4567-e89b-12d3-a456-426614174000\").", nameof(guid));
            if (!Guid.TryParse(guid, out var guidValue)) throw new ArgumentException("ModId.FromGuid requires a valid GUID string in a recognized format.", nameof(guid));
            return guidValue;
        }

        private static uint FoldHashToUInt32(byte[] hashBytes)
        {
            if (hashBytes == null || hashBytes.Length == 0 || hashBytes.Length % sizeof(uint) != 0) throw new ArgumentException("Hash bytes must be non-empty and divisible by four.", nameof(hashBytes));
            var folded = 0u;
            for (var index = 0; index < hashBytes.Length; index += sizeof(uint)) folded ^= BinaryPrimitives.ReadUInt32LittleEndian(hashBytes.AsSpan(index, sizeof(uint)));
            return folded;
        }
    }
}
