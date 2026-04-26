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

    [Fact]
    public void TryParseSteamId_Accepts_Valid_Ulong_Long_And_Numeric_String()
    {
        const ulong validSteam64 = 76561198000000000UL;

        Assert.True(NetworkingPhotonExtensions.TryParseSteamIdForTests(validSteam64, out var fromUlong));
        Assert.Equal(validSteam64, fromUlong);

        Assert.True(NetworkingPhotonExtensions.TryParseSteamIdForTests((long)validSteam64, out var fromLong));
        Assert.Equal(validSteam64, fromLong);

        Assert.True(NetworkingPhotonExtensions.TryParseSteamIdForTests(validSteam64.ToString(), out var fromString));
        Assert.Equal(validSteam64, fromString);
    }

    [Fact]
    public void TryParseSteamId_Rejects_Small_Numeric_Types()
    {
        Assert.False(NetworkingPhotonExtensions.TryParseSteamIdForTests(uint.MaxValue, out _));
        Assert.False(NetworkingPhotonExtensions.TryParseSteamIdForTests(765611980, out _));
    }

    [Fact]
    public void BuildPersonaLookupIndex_Preserves_Ambiguous_Name_Grouping()
    {
        NetworkingPhotonExtensions.ResetPersonaNameCacheForTests();
        NetworkingPhotonExtensions.UtcNow = () => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        NetworkingPhotonExtensions.PersonaNameLookup = sid => sid switch
        {
            76561198000000001UL => "Aurora",
            76561198000000002UL => "Aurora",
            76561198000000003UL => "Comet",
            _ => string.Empty
        };

        var lobbyIds = new[]
        {
            76561198000000001UL,
            76561198000000002UL,
            76561198000000003UL
        };

        var map = NetworkingPhotonExtensions.BuildPersonaLookupIndexForTests(lobbyIds);
        Assert.Equal(2, map["Aurora"].Count);
        Assert.Contains(76561198000000001UL, map["Aurora"]);
        Assert.Contains(76561198000000002UL, map["Aurora"]);
        Assert.Single(map["Comet"]);
        Assert.Contains(76561198000000003UL, map["Comet"]);
    }

    [Fact]
    public void BuildPersonaLookupIndex_Uses_Cache_Within_Ttl_And_Refreshes_After_Expiry()
    {
        NetworkingPhotonExtensions.ResetPersonaNameCacheForTests();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        NetworkingPhotonExtensions.UtcNow = () => now;

        var calls = 0;
        NetworkingPhotonExtensions.PersonaNameLookup = sid =>
        {
            calls++;
            return sid == 76561198000000001UL ? "Aurora" : "Comet";
        };

        var lobbyIds = new[]
        {
            76561198000000001UL,
            76561198000000002UL
        };

        var first = NetworkingPhotonExtensions.BuildPersonaLookupIndexForTests(lobbyIds);
        Assert.Equal(2, calls);
        Assert.Single(first["Aurora"]);
        Assert.Single(first["Comet"]);

        now = now.AddSeconds(2);
        NetworkingPhotonExtensions.BuildPersonaLookupIndexForTests(lobbyIds);
        Assert.Equal(2, calls);

        now = now.AddSeconds(4);
        NetworkingPhotonExtensions.BuildPersonaLookupIndexForTests(lobbyIds);
        Assert.Equal(4, calls);
    }

}
