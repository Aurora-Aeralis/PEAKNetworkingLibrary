using System.Reflection;
using NetworkingLibrary.Services;
using Steamworks;
using Xunit;

namespace NetworkingLibrary.Tests;

public class RpcInfoLoopbackCompatibilityTests
{
    static readonly MethodInfo OfflineCreateRpcInfoInstance = typeof(OfflineNetworkingService).GetMethod("CreateRpcInfoInstance", BindingFlags.Instance | BindingFlags.NonPublic)!;
    static readonly MethodInfo SteamCreateRpcInfoInstance = typeof(SteamNetworkingService).GetMethod("CreateRpcInfoInstance", BindingFlags.Instance | BindingFlags.NonPublic)!;

    sealed class ParamlessLoopbackFieldRpcInfo
    {
        public ulong SteamId64;
        public bool IsLocalLoopback;
    }

    sealed class ParamlessLoopbackPropertyRpcInfo
    {
        public string SteamIdString { get; set; } = string.Empty;
        public bool? IsLocalLoopback { get; set; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OfflineCreateRpcInfoInstance_ParamlessFallback_AssignsLoopbackField(bool isLocalLoopback)
    {
        var obj = OfflineCreateRpcInfoInstance.Invoke(new OfflineNetworkingService(), new object?[] { typeof(ParamlessLoopbackFieldRpcInfo), 1234UL, isLocalLoopback });

        var info = Assert.IsType<ParamlessLoopbackFieldRpcInfo>(obj);
        Assert.Equal(1234UL, info.SteamId64);
        Assert.Equal(isLocalLoopback, info.IsLocalLoopback);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SteamCreateRpcInfoInstance_ParamlessFallback_AssignsLoopbackProperty(bool isLocalLoopback)
    {
        var sender = new CSteamID(5678UL);
        var obj = SteamCreateRpcInfoInstance.Invoke(new SteamNetworkingService(), new object?[] { typeof(ParamlessLoopbackPropertyRpcInfo), sender, isLocalLoopback });

        var info = Assert.IsType<ParamlessLoopbackPropertyRpcInfo>(obj);
        Assert.Equal(sender.ToString(), info.SteamIdString);
        Assert.Equal(isLocalLoopback, info.IsLocalLoopback);
    }
}
