using System;
using UnityEngine;
using Xunit;

namespace NetworkingLibrary.Tests;

public sealed class UnityRuntimeFactAttribute : FactAttribute
{
    static readonly Lazy<bool> RuntimeAvailable = new(IsUnityRuntimeAvailable);

    public UnityRuntimeFactAttribute()
    {
        if (!RuntimeAvailable.Value) Skip = "Requires Unity runtime GameObject ECalls.";
    }

    static bool IsUnityRuntimeAvailable()
    {
        GameObject? probe = null;
        try
        {
            probe = new GameObject("UnityRuntimeFactProbe");
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (probe != null)
            {
                try { UnityEngine.Object.DestroyImmediate(probe); }
                catch { }
            }
        }
    }
}
