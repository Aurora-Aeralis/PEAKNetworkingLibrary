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
            if (string.IsNullOrWhiteSpace(guid)) throw new ArgumentException("ModId.FromGuid requires a non-empty GUID string (for example, \"123e4567-e89b-12d3-a456-426614174000\").", nameof(guid));
            using var md5 = MD5.Create();
            var normalizedGuid = guid.Trim().ToLowerInvariant();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(normalizedGuid));
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }
    }
}
