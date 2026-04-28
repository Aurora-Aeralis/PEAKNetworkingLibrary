using System;
using System.Globalization;

namespace NetworkingLibrary.Modules
{
    internal static class DataValueConverter
    {
        public static T ConvertTo<T>(string value)
        {
            var targetType = typeof(T);
            if (targetType == typeof(string)) return (T)(object)value;

            var valueType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            var converted = valueType.IsEnum
                ? Enum.Parse(valueType, value, ignoreCase: true)
                : Convert.ChangeType(value, valueType, CultureInfo.InvariantCulture);
            return (T)converted;
        }
    }
}
