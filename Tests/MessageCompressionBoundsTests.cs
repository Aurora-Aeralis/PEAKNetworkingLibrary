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

    [Fact]
    public void DecompressPayload_DefaultLimit_Allows_Data_Within_Logical_Message_Cap()
    {
        var original = new byte[Message.MaxSize + 1];
        for (var i = 0; i < original.Length; i++) original[i] = (byte)(i % 13);

        using var source = new Message(1u, "compress", 0);
        source.WriteBytes(original);
        var compressed = source.CompressPayload();

        var decompressed = Message.DecompressPayload(compressed);
        Assert.Equal(source.ToArray(), decompressed);
    }

    [Fact]
    public void CompressPayload_Is_Stateless_And_Does_Not_Mutate_Message_Data()
    {
        using var source = new Message(7u, "compress", 3);
        source.WriteString("alpha");
        source.WriteInt(1234);
        var beforeCompression = source.ToArray();

        var compressed = source.CompressPayload();
        var afterCompression = source.ToArray();
        var decompressed = Message.DecompressPayload(compressed);

        Assert.Equal(beforeCompression, afterCompression);
        Assert.Equal(beforeCompression, decompressed);
    }

    [Fact]
    public void CompressPayload_ByteArray_Overload_Matches_Message_Overload_And_Roundtrips()
    {
        using var source = new Message(11u, "compress", 4);
        source.WriteString("roundtrip");
        source.WriteBytes(new byte[] { 1, 5, 9, 13, 17 });
        var payload = source.ToArray();

        var compressedFromMessage = source.CompressPayload();
        var compressedFromPayload = source.CompressPayload(payload);

        Assert.Equal(compressedFromMessage, compressedFromPayload);
        Assert.Equal(payload, Message.DecompressPayload(compressedFromPayload));
    }

    [Fact]
    public void Decompressed_Payload_Can_Be_Reused_With_SetBytes_And_Recompressed()
    {
        using var source = new Message(21u, "compress", 2);
        source.WriteString("repeat");
        source.WriteInt(31415);
        source.WriteBytes(new byte[] { 2, 4, 6, 8 });

        var compressed = source.CompressPayload();
        var decompressed = Message.DecompressPayload(compressed);

        using var rebuilt = new Message(0u, "placeholder", 0);
        rebuilt.SetBytes(decompressed);

        Assert.Equal(source.ToArray(), rebuilt.ToArray());
        Assert.Equal(compressed, rebuilt.CompressPayload());
    }
}
