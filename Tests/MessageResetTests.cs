using System.Reflection;
using NetworkingLibrary.Modules;
using Xunit;

namespace NetworkingLibrary.Tests;

public class MessageResetTests
{
    private static bool ReadReadableBufferDirty(Message message) =>
        (bool)(typeof(Message).GetField("readableBufferDirty", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(message)
            ?? throw new MissingFieldException(typeof(Message).FullName, "readableBufferDirty"));

    [Fact]
    public void Reset_Clears_Buffer_Read_State_And_Marks_ReadableBuffer_Dirty()
    {
        var message = new Message(1u, "method", 0);
        message.WriteInt(123);

        _ = message.ReadByte();
        Assert.NotEqual(0, message.readPos);
        Assert.False(ReadReadableBufferDirty(message));
        Assert.NotEmpty(message.readableBuffer);
        Assert.NotEmpty(message.ToArray());

        message.Reset();

        Assert.Empty(message.ToArray());
        Assert.Empty(message.readableBuffer);
        Assert.Equal(0, message.readPos);
        Assert.True(ReadReadableBufferDirty(message));
    }

    [Fact]
    public void Reset_Bool_Compatibility_Overload_Also_Always_Clears_State()
    {
#pragma warning disable CS0618
        var message = new Message(1u, "method", 0);
        message.WriteInt(456);

        _ = message.ReadByte();
        Assert.NotEqual(0, message.readPos);

        message.Reset(false);
#pragma warning restore CS0618

        Assert.Empty(message.ToArray());
        Assert.Empty(message.readableBuffer);
        Assert.Equal(0, message.readPos);
        Assert.True(ReadReadableBufferDirty(message));
    }
}
