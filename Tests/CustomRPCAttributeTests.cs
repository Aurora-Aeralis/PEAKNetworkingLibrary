using System;
using System.Reflection;
using NetworkingLibrary.Modules;
using Xunit;

namespace NetworkingLibrary.Tests;

public class CustomRPCAttributeTests
{
    [Fact]
    public void AttributeUsage_IsExplicitMethodMarker()
    {
        var usage = typeof(CustomRPCAttribute).GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Method, usage!.ValidOn);
        Assert.False(usage.AllowMultiple);
        Assert.False(usage.Inherited);
    }

    [Fact]
    public void Attribute_DoesNotFlowToOverrides()
    {
        var inheritedAttributes = typeof(OverrideRpcHandler)
            .GetMethod(nameof(OverrideRpcHandler.Rpc))!
            .GetCustomAttributes(typeof(CustomRPCAttribute), inherit: true);

        Assert.Empty(inheritedAttributes);
    }

    class BaseRpcHandler
    {
        [CustomRPC]
        public virtual void Rpc() { }
    }

    class OverrideRpcHandler : BaseRpcHandler
    {
        public override void Rpc() { }
    }
}
