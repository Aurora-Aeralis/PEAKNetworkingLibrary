using System;
using System.Buffers;
using System.Text;

namespace NetworkingLibrary.Modules
{
    public static class StringId
    {
        public static uint Map(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) throw new ArgumentException("StringId.Map requires a non-empty, non-whitespace string to hash.", nameof(s));
            const uint FNV_OFFSET = 2166136261u;
            const uint FNV_PRIME = 16777619u;
            uint hash = FNV_OFFSET;
            var encoding = Encoding.UTF8;
            var byteCount = encoding.GetByteCount(s);
            byte[]? rented = null;
            Span<byte> data = byteCount <= 512 ? stackalloc byte[byteCount] : (rented = ArrayPool<byte>.Shared.Rent(byteCount));
            try
            {
                var encodedCount = encoding.GetBytes(s.AsSpan(), data);
                for (var i = 0; i < encodedCount; i++)
                {
                    hash ^= data[i];
                    hash *= FNV_PRIME;
                }
            }
            finally
            {
                if (rented != null) ArrayPool<byte>.Shared.Return(rented);
            }
            return hash;
        }
    }
}
