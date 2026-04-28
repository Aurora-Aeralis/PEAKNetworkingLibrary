using NetworkingLibrary.Modules;
using NetworkingLibrary.Services;
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;
using Xunit;

namespace NetworkingLibrary.Tests;

public class SteamCallbackPumpTests : IDisposable
{
    sealed class DummyComponent : MonoBehaviour { }

    readonly Func<bool> originalIsSteamReady = SteamCallbackPump.IsSteamReady;
    readonly Func<float> originalTimeProvider = SteamCallbackPump.TimeProvider;
    readonly Action originalRunCallbacks = SteamCallbackPump.RunCallbacks;

    public void Dispose()
    {
        SteamCallbackPump.IsSteamReady = originalIsSteamReady;
        SteamCallbackPump.TimeProvider = originalTimeProvider;
        SteamCallbackPump.RunCallbacks = originalRunCallbacks;
        SteamCallbackPump.DisablePumping();
        NetLog.ResetForTests();
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

    [Fact]
    public void Update_WhenIsSteamReadyHookThrows_ThrottlesRepeatedErrorEmissionByPump()
    {
        var pump = (SteamCallbackPump)FormatterServices.GetUninitializedObject(typeof(SteamCallbackPump));
        var simulatedTime = 50f;
        SteamCallbackPump.TimeProvider = () => simulatedTime;
        SteamCallbackPump.IsSteamReady = () => throw new InvalidOperationException("hook failed");
        SteamCallbackPump.EnablePumping();

        InvokeUpdate(pump);
        simulatedTime += 0.25f;
        InvokeUpdate(pump);

        var cooldownKey = $"SteamCallbackPump.TryIsSteamReady:{pump.GetHashCode()}";
        Assert.False(NetLog.TryEnterCooldown(cooldownKey, 2d, simulatedTime));
        Assert.True(NetLog.TryEnterCooldown(cooldownKey, 2d, simulatedTime + 2.1f));
    }

    [Fact]
    public void Update_WhenIsSteamReadyAndTimeProviderThrow_DoesNotThrow()
    {
        var pump = (SteamCallbackPump)FormatterServices.GetUninitializedObject(typeof(SteamCallbackPump));
        SteamCallbackPump.TimeProvider = () => throw new InvalidOperationException("time failed");
        SteamCallbackPump.IsSteamReady = () => throw new InvalidOperationException("hook failed");
        SteamCallbackPump.EnablePumping();

        var exception = Record.Exception(() => InvokeUpdate(pump));

        Assert.Null(exception);
        Assert.False(ReadPrivateBool(pump, "runCallbacksFaulted"));
    }

    [Fact]
    public void Update_WhenRunCallbacksThrows_ThrottlesRepeatedErrorEmission()
    {
        var pump = (SteamCallbackPump)FormatterServices.GetUninitializedObject(typeof(SteamCallbackPump));
        var simulatedTime = 25f;
        SteamCallbackPump.TimeProvider = () => simulatedTime;
        SteamCallbackPump.IsSteamReady = () => true;
        SteamCallbackPump.RunCallbacks = () => throw new InvalidOperationException("callback failed");
        SteamCallbackPump.EnablePumping();

        var exception = Record.Exception(() => InvokeUpdate(pump));
        simulatedTime += 0.25f;
        var repeatedException = Record.Exception(() => InvokeUpdate(pump));

        Assert.Null(exception);
        Assert.Null(repeatedException);
        Assert.True(ReadPrivateBool(pump, "runCallbacksFaulted"));
        Assert.False(NetLog.TryEnterCooldown("SteamCallbackPump.RunCallbacks", 2d, simulatedTime));
        Assert.True(NetLog.TryEnterCooldown("SteamCallbackPump.RunCallbacks", 2d, simulatedTime + 2.1f));
    }

    [Fact]
    public void Update_WhenRunCallbacksRecovers_ClearsFaultState()
    {
        var pump = (SteamCallbackPump)FormatterServices.GetUninitializedObject(typeof(SteamCallbackPump));
        SteamCallbackPump.TimeProvider = () => 10f;
        SteamCallbackPump.IsSteamReady = () => true;
        SteamCallbackPump.RunCallbacks = () => throw new InvalidOperationException("callback failed");
        SteamCallbackPump.EnablePumping();

        InvokeUpdate(pump);
        SteamCallbackPump.RunCallbacks = () => { };
        InvokeUpdate(pump);

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

    [UnityRuntimeFact]
    public void PrepareCanonicalSteamCallbackPump_NormalizesDuplicates_AndKeepsSingleActiveEnabledPump()
    {
        var canonicalObject = new GameObject("canonical");
        var duplicateOnlyObject = new GameObject("duplicate-only");
        var duplicateWithExtraObject = new GameObject("duplicate-extra");
        var canonicalPump = canonicalObject.AddComponent<SteamCallbackPump>();
        var duplicateOnlyPump = duplicateOnlyObject.AddComponent<SteamCallbackPump>();
        var duplicateWithExtraPump = duplicateWithExtraObject.AddComponent<SteamCallbackPump>();
        duplicateWithExtraObject.AddComponent<DummyComponent>();
        canonicalObject.SetActive(false);
        canonicalPump.enabled = false;

        try
        {
            var prepareMethod = typeof(SteamNetworkingService).GetMethod("PrepareCanonicalSteamCallbackPump", BindingFlags.Static | BindingFlags.NonPublic)!;
            var args = new object?[] { null, null };
            var selectedPump = (SteamCallbackPump)prepareMethod.Invoke(null, args)!;

            Assert.Same(canonicalPump, selectedPump);
            Assert.True(canonicalObject.activeSelf);
            Assert.True(canonicalPump.enabled);
            Assert.False(duplicateOnlyObject);
            Assert.False(duplicateWithExtraPump);
            Assert.True(duplicateWithExtraObject);

            var allPumps = UnityEngine.Object.FindObjectsByType<SteamCallbackPump>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var alivePumps = allPumps.Where(p => p != null).ToArray();
            Assert.Single(alivePumps);
            Assert.Same(canonicalPump, alivePumps[0]);
        }
        finally
        {
            if (canonicalObject) UnityEngine.Object.DestroyImmediate(canonicalObject);
            if (duplicateOnlyObject) UnityEngine.Object.DestroyImmediate(duplicateOnlyObject);
            if (duplicateWithExtraObject) UnityEngine.Object.DestroyImmediate(duplicateWithExtraObject);
        }
    }

    [UnityRuntimeFact]
    public void PrepareCanonicalSteamCallbackPump_SelectsDeterministically_WhenMultiplePumpsAreActive()
    {
        var firstObject = new GameObject("first-active");
        var secondObject = new GameObject("second-active");
        var firstPump = firstObject.AddComponent<SteamCallbackPump>();
        var secondPump = secondObject.AddComponent<SteamCallbackPump>();

        try
        {
            var prepareMethod = typeof(SteamNetworkingService).GetMethod("PrepareCanonicalSteamCallbackPump", BindingFlags.Static | BindingFlags.NonPublic)!;
            var args = new object?[] { null, null };
            var selectedPump = (SteamCallbackPump)prepareMethod.Invoke(null, args)!;

            var expectedPump = firstPump.GetInstanceID() < secondPump.GetInstanceID() ? firstPump : secondPump;
            Assert.Same(expectedPump, selectedPump);
        }
        finally
        {
            if (firstObject) UnityEngine.Object.DestroyImmediate(firstObject);
            if (secondObject) UnityEngine.Object.DestroyImmediate(secondObject);
        }
    }

    [UnityRuntimeFact]
    public void PrepareCanonicalSteamCallbackPump_PromotesCanonicalPumpToActiveHierarchy_WhenParentIsInactive()
    {
        var inactiveParent = new GameObject("inactive-parent");
        var canonicalObject = new GameObject("canonical-under-inactive-parent");
        canonicalObject.transform.SetParent(inactiveParent.transform, false);
        var canonicalPump = canonicalObject.AddComponent<SteamCallbackPump>();
        inactiveParent.SetActive(false);

        try
        {
            var prepareMethod = typeof(SteamNetworkingService).GetMethod("PrepareCanonicalSteamCallbackPump", BindingFlags.Static | BindingFlags.NonPublic)!;
            var args = new object?[] { null, null };
            var selectedPump = (SteamCallbackPump)prepareMethod.Invoke(null, args)!;

            Assert.Same(canonicalPump, selectedPump);
            Assert.True(canonicalObject.activeInHierarchy);
            Assert.Null(canonicalObject.transform.parent);
        }
        finally
        {
            if (canonicalObject) UnityEngine.Object.DestroyImmediate(canonicalObject);
            if (inactiveParent) UnityEngine.Object.DestroyImmediate(inactiveParent);
        }
    }
}
