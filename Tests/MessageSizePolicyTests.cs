using NetworkingLibrary.Modules;
using Xunit;

namespace NetworkingLibrary.Tests;

public class MessageSizePolicyTests
{
    [Fact]
    public void Message_DefaultSizePolicy_RemainsStableAcrossInstances()
    {
        using var first = new Message(1u, "a", 0);
        using var second = new Message(2u, "b", 1);

        Assert.Equal(Message.MaxSize, first.SizeLimit);
        Assert.Equal(Message.MaxSize, second.SizeLimit);
        Assert.Equal(Message.MaxLogicalSize, first.LogicalSizeLimit);
        Assert.Equal(Message.MaxLogicalSize, second.LogicalSizeLimit);
    }

    [Fact]
    public void Message_PerInstanceSizeOverride_DoesNotMutateDefaultPolicy()
    {
        const int overrideSize = 2 * 1024;
        using var scoped = new Message(1u, "scoped", 0, sizeLimitOverride: overrideSize);
        using var baseline = new Message(1u, "baseline", 0);

        Assert.Equal(overrideSize, scoped.SizeLimit);
        Assert.Equal(overrideSize * 16, scoped.LogicalSizeLimit);
        Assert.Equal(Message.MaxSize, baseline.SizeLimit);
        Assert.Equal(Message.MaxLogicalSize, baseline.LogicalSizeLimit);
        Assert.Equal(64 * 1024, Message.MaxSize);
    }
}
