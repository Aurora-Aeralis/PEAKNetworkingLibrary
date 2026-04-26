using System;
using NetworkingLibrary.Modules;
using Xunit;

namespace NetworkingLibrary.Tests;

public class NetworkingPhotonExtensionsTests
{
    [Fact]
    public void ShouldEmitUnresolvedMappingWarning_Throttles_Repeated_Issue_Within_Cooldown()
    {
        NetworkingPhotonExtensions.ResetUnresolvedMappingWarningThrottleForTests();

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(12, "no stable ID candidate matched lobby members", t0));
        Assert.False(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(12, "no stable ID candidate matched lobby members", t0.AddSeconds(3)));
        Assert.True(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(12, "no stable ID candidate matched lobby members", t0.AddSeconds(8)));
    }

    [Fact]
    public void ShouldEmitUnresolvedMappingWarning_Prunes_Old_Entries()
    {
        NetworkingPhotonExtensions.ResetUnresolvedMappingWarningThrottleForTests();

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(1, "old issue", t0));
        Assert.True(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(2, "active issue", t0.AddSeconds(1)));
        Assert.Equal(2, NetworkingPhotonExtensions.GetUnresolvedMappingWarningThrottleCountForTests());

        Assert.True(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(3, "trigger prune", t0.AddSeconds(29)));
        Assert.Equal(2, NetworkingPhotonExtensions.GetUnresolvedMappingWarningThrottleCountForTests());
    }

    [Fact]
    public void ShouldEmitUnresolvedMappingWarning_Caps_Size_Under_Repeated_Unique_Keys()
    {
        NetworkingPhotonExtensions.ResetUnresolvedMappingWarningThrottleForTests();

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 3000; i++) Assert.True(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(i, "issue_" + i, t0.AddSeconds(i)));

        Assert.True(NetworkingPhotonExtensions.GetUnresolvedMappingWarningThrottleCountForTests() <= 2048);
    }

    [Fact]
    public void ResetUnresolvedMappingWarningThrottleForTests_Resets_Prune_Scheduling_State()
    {
        NetworkingPhotonExtensions.ResetUnresolvedMappingWarningThrottleForTests();

        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.True(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(1, "old issue", t0));

        NetworkingPhotonExtensions.ResetUnresolvedMappingWarningThrottleForTests();
        Assert.True(NetworkingPhotonExtensions.ShouldEmitUnresolvedMappingWarning(2, "new issue", t0.AddSeconds(29)));
        Assert.Equal(1, NetworkingPhotonExtensions.GetUnresolvedMappingWarningThrottleCountForTests());
    }
}
