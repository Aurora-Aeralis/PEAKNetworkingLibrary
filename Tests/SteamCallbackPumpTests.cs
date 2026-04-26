using NetworkingLibrary.Modules;
using System;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace NetworkingLibrary.Tests;

public class SteamCallbackPumpTests : IDisposable
{
    readonly Func<bool> originalIsSteamReady = SteamCallbackPump.IsSteamReady;
    readonly Func<float> originalTimeProvider = SteamCallbackPump.TimeProvider;

    public void Dispose()
    {
        SteamCallbackPump.IsSteamReady = originalIsSteamReady;
        SteamCallbackPump.TimeProvider = originalTimeProvider;
        SteamCallbackPump.DisablePumping();
    }

    [Fact]
    public void Update_WhenIsSteamReadyHookThrows_DoesNotThrow_AndPreservesFallbackBehavior()
    {
        var pump = (SteamCallbackPump)FormatterServices.GetUninitializedObject(typeof(SteamCallbackPump));
        var simulatedTime = 100f;
        SteamCallbackPump.TimeProvider = () => simulatedTime;
        SteamCallbackPump.IsSteamReady = () => throw new InvalidOperationException("hook failed");
        SteamCallbackPump.EnablePumping();

        var exception = Record.Exception(() => InvokeUpdate(pump));

        Assert.Null(exception);
        Assert.False(ReadPrivateBool(pump, "runCallbacksFaulted"));

        simulatedTime += 0.1f;
        exception = Record.Exception(() => InvokeUpdate(pump));
        Assert.Null(exception);
        Assert.False(ReadPrivateBool(pump, "runCallbacksFaulted"));
    }

    static void InvokeUpdate(SteamCallbackPump pump)
    {
        typeof(SteamCallbackPump).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pump, null);
    }

    static bool ReadPrivateBool(SteamCallbackPump pump, string fieldName)
    {
        return (bool)(typeof(SteamCallbackPump).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pump) ?? false);
    }
}
