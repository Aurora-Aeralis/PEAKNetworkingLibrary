using NetworkingLibrary.Modules;
using Xunit;

namespace NetworkingLibrary.Tests;

public class ModIdTests
{
    [Fact]
    public void FromGuid_Legacy_IsStable_ForKnownGuid()
    {
        var modId = ModId.FromGuid("123e4567-e89b-12d3-a456-426614174000");

        Assert.Equal(3702665787u, modId);
    }

    [Fact]
    public void FromGuidV2_IsDeterministic_ForKnownGuidVectors()
    {
        Assert.Equal(4203402889u, ModId.FromGuidV2("123e4567-e89b-12d3-a456-426614174000"));
        Assert.Equal(1128267358u, ModId.FromGuidV2("00000000-0000-0000-0000-000000000001"));
        Assert.Equal(2851418380u, ModId.FromGuidV2("ffffffff-ffff-ffff-ffff-ffffffffffff"));
    }

    [Fact]
    public void FromGuidV2_NormalizesEquivalentGuidRepresentations()
    {
        const string canonical = "123e4567-e89b-12d3-a456-426614174000";
        const string uppercase = "123E4567-E89B-12D3-A456-426614174000";
        const string noHyphens = "123e4567e89b12d3a456426614174000";
        const string braced = "{123e4567-e89b-12d3-a456-426614174000}";

        var canonicalId = ModId.FromGuidV2(canonical);

        Assert.Equal(canonicalId, ModId.FromGuidV2(uppercase));
        Assert.Equal(canonicalId, ModId.FromGuidV2(noHyphens));
        Assert.Equal(canonicalId, ModId.FromGuidV2(braced));
    }

    [Fact]
    public void FromGuidV2_TrimsWhitespace_BeforeHashing()
    {
        var guid = "123e4567-e89b-12d3-a456-426614174000";

        Assert.Equal(ModId.FromGuidV2(guid), ModId.FromGuidV2($"  {guid}  "));
    }

    [Fact]
    public void FromGuidV2_ThrowsForInvalidGuidFormat()
    {
        var ex = Assert.Throws<ArgumentException>(() => ModId.FromGuidV2("not-a-guid"));

        Assert.Equal("guid", ex.ParamName);
        Assert.Contains("valid GUID string", ex.Message);
    }
}
