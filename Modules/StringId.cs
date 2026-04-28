using System;
using System.Buffers;
using System.Text;

namespace NetworkingLibrary.Modules
{
    public static class StringId
    {
        const uint FnvOffset = 2166136261u;
        const uint FnvPrime = 16777619u;

        public static uint Map(string s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            var hash = FnvOffset;
            for (var i = 0; i < s.Length; i++)
            {
                var ch = s[i];
                if (ch > 0x7f) return MapUtf8(s);
                hash ^= ch;
                unchecked { hash *= FnvPrime; }
            }
            return hash;
        }

        static uint MapUtf8(string s)
        {
            var hash = FnvOffset;
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
                    unchecked { hash *= FnvPrime; }
                }
                return hash;
            }
            finally
            {
                if (rented != null) ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
    }
}
