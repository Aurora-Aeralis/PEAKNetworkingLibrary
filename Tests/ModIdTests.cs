using NetworkingLibrary.Modules;
using Xunit;

namespace NetworkingLibrary.Tests;

public class ModIdTests
{
    [Fact]
    public void FromGuid_IsStable_ForKnownGuid()
    {
        var guid = "123e4567-e89b-12d3-a456-426614174000";

        var modId = ModId.FromGuid(guid);

        Assert.Equal(3702665787u, modId);
    }

    [Fact]
    public void FromGuid_TrimsWhitespace_BeforeHashing()
    {
        var guid = "123e4567-e89b-12d3-a456-426614174000";

        var clean = ModId.FromGuid(guid);
        var padded = ModId.FromGuid($"  {guid}  ");

        Assert.Equal(clean, padded);
    }
}
