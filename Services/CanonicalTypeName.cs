using System;
using System.Linq;

namespace NetworkingLibrary.Services
{
    static class CanonicalTypeName
    {
        public static string For(Type type)
        {
            if (type.IsByRef) return $"{For(type.GetElementType()!)}&";

            var nullableUnderlying = Nullable.GetUnderlyingType(type);
            if (nullableUnderlying != null) return $"{For(nullableUnderlying)}?";

            if (type.IsArray)
            {
                var rank = type.GetArrayRank();
                var suffix = rank == 1 ? "[]" : $"[{new string(',', rank - 1)}]";
                return $"{For(type.GetElementType()!)}{suffix}";
            }

            if (type.IsGenericType)
            {
                var genericDefinition = type.GetGenericTypeDefinition();
                var definitionName = StripGenericArityMarkers(genericDefinition.FullName ?? genericDefinition.Name);
                return $"{definitionName}<{string.Join(",", type.GetGenericArguments().Select(For))}>";
            }

            return type.FullName ?? type.Name;
        }

        static string StripGenericArityMarkers(string typeName)
        {
            var chars = typeName.ToCharArray();
            var write = 0;

            for (var read = 0; read < chars.Length; read++)
            {
                if (chars[read] != '`')
                {
                    chars[write++] = chars[read];
                    continue;
                }

                read++;
                while (read < chars.Length && char.IsDigit(chars[read])) read++;
                read--;
            }

            return new string(chars, 0, write);
        }
    }
}
