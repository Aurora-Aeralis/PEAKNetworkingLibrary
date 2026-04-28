using System;
using NetworkingLibrary.Modules;
using Xunit;

namespace NetworkingLibrary.Tests;

public class StringIdTests
{
    [Fact]
    public void Map_IsStable_ForKnownUtf8Vector()
    {
        Assert.Equal(1335831723u, StringId.Map("hello"));
    }

    [Fact]
    public void Map_MapsEmptyString_ToFnvOffset()
    {
        Assert.Equal(2166136261u, StringId.Map(string.Empty));
    }

    [Fact]
    public void Map_MapsWhitespace_AsUtf8Input()
    {
        Assert.Equal(621580159u, StringId.Map(" "));
    }

    [Fact]
    public void Map_MapsNonAscii_AsUtf8Input()
    {
        Assert.Equal(1252296000u, StringId.Map("h\u00e9llo"));
    }

    [Fact]
    public void Map_ThrowsForNull()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => StringId.Map(null!));

        Assert.Equal("s", ex.ParamName);
    }

    [Fact]
    public void Map_IsDeterministic_ForPooledBufferPath()
    {
        var input = new string('x', 513);

        Assert.Equal(StringId.Map(input), StringId.Map(input));
    }
}
