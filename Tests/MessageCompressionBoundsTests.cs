using System.IO;
using NetworkingLibrary.Modules;
using Xunit;

namespace NetworkingLibrary.Tests;

public class MessageCompressionBoundsTests
{
    [Fact]
    public void DecompressPayload_Allows_Data_Within_Max_Output_Size()
    {
        var message = new Message(1u, "compress", 0);
        message.WriteBytes(new byte[1024]);
        var compressed = message.CompressPayload();

        var decompressed = Message.DecompressPayload(compressed, message.Length());

        Assert.Equal(message.ToArray(), decompressed);
    }

    [Fact]
    public void DecompressPayload_Rejects_Data_Exceeding_Max_Output_Size()
    {
        var original = new byte[Message.MaxSize + 1];
        for (var i = 0; i < original.Length; i++) original[i] = (byte)(i % 7);

        using var source = new Message(1u, "compress", 0);
        source.WriteBytes(original);
        var compressed = source.CompressPayload();

        var ex = Assert.Throws<InvalidDataException>(() => Message.DecompressPayload(compressed, Message.MaxSize));
        Assert.Contains("exceeds max allowed size", ex.Message);
    }

    [Fact]
    public void DecompressPayload_Allows_Data_Within_Logical_Message_Cap()
    {
        var original = new byte[Message.MaxSize + 1];
        for (var i = 0; i < original.Length; i++) original[i] = (byte)(i % 11);

        using var source = new Message(1u, "compress", 0);
        source.WriteBytes(original);
        var compressed = source.CompressPayload();

        var decompressed = Message.DecompressPayload(compressed, Message.MaxLogicalSize);
        Assert.Equal(source.ToArray(), decompressed);
    }
}
