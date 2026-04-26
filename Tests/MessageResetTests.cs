using NetworkingLibrary.Modules;
using Xunit;

namespace NetworkingLibrary.Tests;

public class MessageResetTests
{
    private static Message NewMessage() => new(1u, "method", 0);

    [Fact]
    public void Reset_Clears_Message_And_Rewinds_Read_Cursor_Without_Zeroing()
    {
        using var message = NewMessage();
        message.WriteInt(42);
        Assert.True(message.Length() > 0);

        message.Reset();

        Assert.Equal(0, message.Length());
        Assert.Equal(0, message.UnreadLength());

        message.WriteInt(7);
        var reread = new Message(message.ToArray());
        Assert.Equal(7, reread.ReadInt());
    }

    [Fact]
    public void ClearAndZero_Clears_Message_And_Rewinds_Read_Cursor()
    {
        using var message = NewMessage();
        message.WriteString("payload");
        Assert.True(message.Length() > 0);

        message.ClearAndZero();

        Assert.Equal(0, message.Length());
        Assert.Equal(0, message.UnreadLength());

        message.WriteBool(true);
        var reread = new Message(message.ToArray());
        Assert.True(reread.ReadBool());
    }

#pragma warning disable CS0618
    [Fact]
    public void Reset_With_Boolean_Overload_Remains_Backward_Compatible()
    {
        using var message = NewMessage();
        message.WriteInt(99);

        message.Reset(zero: false);
        Assert.Equal(0, message.Length());

        message.WriteInt(100);
        message.Reset(zero: true);
        Assert.Equal(0, message.Length());
    }
#pragma warning restore CS0618
}
