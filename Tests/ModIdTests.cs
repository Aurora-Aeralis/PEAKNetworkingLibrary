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

    [Fact]
    public void FromGuid_NormalizesEquivalentGuidRepresentations()
    {
        const string canonical = "123e4567-e89b-12d3-a456-426614174000";
        const string uppercase = "123E4567-E89B-12D3-A456-426614174000";
        const string noHyphens = "123e4567e89b12d3a456426614174000";
        const string braced = "{123e4567-e89b-12d3-a456-426614174000}";

        var canonicalId = ModId.FromGuid(canonical);
        var uppercaseId = ModId.FromGuid(uppercase);
        var noHyphensId = ModId.FromGuid(noHyphens);
        var bracedId = ModId.FromGuid(braced);

        Assert.Equal(canonicalId, uppercaseId);
        Assert.Equal(canonicalId, noHyphensId);
        Assert.Equal(canonicalId, bracedId);
    }

    [Fact]
    public void FromGuid_ThrowsForInvalidGuidFormat()
    {
        var ex = Assert.Throws<ArgumentException>(() => ModId.FromGuid("not-a-guid"));

        Assert.Equal("guid", ex.ParamName);
        Assert.Contains("valid GUID string", ex.Message);
    }
}
