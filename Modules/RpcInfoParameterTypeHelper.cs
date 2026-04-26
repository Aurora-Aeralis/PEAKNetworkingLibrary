using System;
using System.Collections.Generic;

namespace NetworkingLibrary.Modules
{
    internal static class RpcInfoParameterTypeHelper
    {
        static readonly HashSet<string> CompatibleTypeNames = new(StringComparer.Ordinal)
        {
            "NetworkingLibrary.Modules.RPCInfo"
        };

        internal static bool IsRpcInfoParameterType(Type parameterType)
        {
            if (parameterType == typeof(RPCInfo)) return true;
            var fullName = parameterType.FullName;
            return fullName != null && CompatibleTypeNames.Contains(fullName);
        }
    }
}
