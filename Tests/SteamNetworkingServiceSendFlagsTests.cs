using NetworkingLibrary.Services;
using Steamworks;
using System;
using System.ComponentModel;
using System.Reflection;
using Xunit;

namespace NetworkingLibrary.Tests;

public class SteamNetworkingServiceSendFlagsTests
{
    [Theory]
    [InlineData(ReliableType.Unreliable, Constants.k_nSteamNetworkingSend_Unreliable)]
    [InlineData(ReliableType.Reliable, Constants.k_nSteamNetworkingSend_Reliable)]
    [InlineData(ReliableType.UnreliableNoDelay, Constants.k_nSteamNetworkingSend_UnreliableNoDelay)]
    public void ResolveSendModeFlag_MapsAllReliableTypes(ReliableType reliableType, int expectedFlag)
    {
        var actual = InvokeResolveSendModeFlag(reliableType);
        Assert.Equal(expectedFlag, actual);
    }

    [Fact]
    public void ResolveSendModeFlag_InvalidReliableType_IsVisible()
    {
        const ReliableType invalidValue = (ReliableType)999;

#if DEBUG
        Assert.Throws<InvalidEnumArgumentException>(() => InvokeResolveSendModeFlag(invalidValue));
#else
        var actual = InvokeResolveSendModeFlag(invalidValue);
        Assert.Equal(Constants.k_nSteamNetworkingSend_Reliable, actual);
#endif
    }

    static int InvokeResolveSendModeFlag(ReliableType reliableType)
    {
        var method = typeof(SteamNetworkingService).GetMethod("ResolveSendModeFlag", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (int)method.Invoke(null, new object[] { reliableType })!;
    }
}
