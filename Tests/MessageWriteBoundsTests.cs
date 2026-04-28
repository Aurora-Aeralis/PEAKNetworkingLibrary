using System.IO;
using NetworkingLibrary.Modules;
using Xunit;

namespace NetworkingLibrary.Tests;

public class MessageWriteBoundsTests
{
    private static Message NewMessage() => new(1u, "method", 0);

    [Fact]
    public void WriteBytes_Rejects_Payload_That_Exceeds_Logical_Message_Cap()
    {
        using var message = NewMessage();
        var remaining = Message.MaxLogicalSize - message.Length() - sizeof(int);
        Assert.True(remaining > 0);

        message.WriteBytes(new byte[remaining]);
        var lengthBeforeOverflowWrite = message.Length();

        var ex = Assert.Throws<InvalidDataException>(() => message.WriteBytes(new byte[1]));
        Assert.Contains("WriteBytes exceeds max message size", ex.Message);
        Assert.Equal(lengthBeforeOverflowWrite, message.Length());
    }

    [Fact]
    public void WriteString_Rejects_Payload_That_Exceeds_Logical_Message_Cap()
    {
        using var message = NewMessage();
        var remaining = Message.MaxLogicalSize - message.Length() - sizeof(int);
        Assert.True(remaining > 0);

        message.WriteString(new string('a', remaining));
        var lengthBeforeOverflowWrite = message.Length();

        var ex = Assert.Throws<InvalidDataException>(() => message.WriteString("b"));
        Assert.Contains("WriteString exceeds max message size", ex.Message);
        Assert.Equal(lengthBeforeOverflowWrite, message.Length());
    }

    [Fact]
    public void ReadByte_Rejects_Cursor_Beyond_Buffer_Without_Indexing()
    {
        using var message = NewMessage();
        message.readPos = int.MaxValue;

        var ex = Assert.Throws<InvalidDataException>(() => message.ReadByte());
        Assert.Contains("ReadByte out of range", ex.Message);
    }
}
