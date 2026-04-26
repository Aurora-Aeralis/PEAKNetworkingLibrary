using System.IO;
using System.Buffers.Binary;
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
    public void ReadObject_IntArray_Rejects_Element_Count_That_Exceeds_Materializable_Bounds()
    {
        var policy = new MessageSizePolicy(1024);
        using var write = new Message(1u, "method", 0, policy);
        write.WriteObject(typeof(int[]), System.Array.Empty<int>());
        var payload = write.ToArray();
        var oversizedLength = (policy.MaxLogicalSize / sizeof(int)) + 1;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(payload.Length - sizeof(int), sizeof(int)), oversizedLength);

        using var read = new Message(payload, policy);
        var ex = Assert.Throws<InvalidDataException>(() => read.ReadObject(typeof(int[])));
        Assert.Contains("materializable element count", ex.Message);
    }
}
