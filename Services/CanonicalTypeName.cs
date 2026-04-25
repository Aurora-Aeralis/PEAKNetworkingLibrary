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
                var definitionName = genericDefinition.FullName ?? genericDefinition.Name;
                var tickIndex = definitionName.IndexOf('`');
                if (tickIndex >= 0) definitionName = definitionName.Substring(0, tickIndex);
                return $"{definitionName}<{string.Join(",", type.GetGenericArguments().Select(For))}>";
            }

            return type.FullName ?? type.Name;
        }
    }
}
