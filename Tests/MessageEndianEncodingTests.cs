using NetworkingLibrary.Modules;
using Xunit;

namespace NetworkingLibrary.Tests;

public class MessageEndianEncodingTests
{
    private static Message NewMessage() => new(1u, "method", 0);

    [Fact]
    public void PrimitiveWrites_Use_Deterministic_LittleEndian_Layout()
    {
        var message = NewMessage();
        message.WriteInt(unchecked((int)0x89ABCDEF));
        message.WriteUInt(0x01234567u);
        message.WriteLong(unchecked((long)0x0102030405060708UL));
        message.WriteULong(0x8899AABBCCDDEEFFUL);
        message.WriteFloat(1.0f);
        message.WriteBool(true);
        message.WriteBool(false);

        var bytes = message.ToArray();
        var expected = new byte[]
        {
            0x02,
            0x01, 0x00, 0x00, 0x00,
            0x06, 0x00, 0x00, 0x00,
            0x6D, 0x65, 0x74, 0x68, 0x6F, 0x64,
            0x00, 0x00, 0x00, 0x00,
            0xEF, 0xCD, 0xAB, 0x89,
            0x67, 0x45, 0x23, 0x01,
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
            0xFF, 0xEE, 0xDD, 0xCC, 0xBB, 0xAA, 0x99, 0x88,
            0x00, 0x00, 0x80, 0x3F,
            0x01,
            0x00,
        };

        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void PrimitiveReads_RoundTrip_Boundary_Values()
    {
        var write = NewMessage();
        write.WriteInt(int.MinValue);
        write.WriteInt(int.MaxValue);
        write.WriteUInt(uint.MinValue);
        write.WriteUInt(uint.MaxValue);
        write.WriteLong(long.MinValue);
        write.WriteLong(long.MaxValue);
        write.WriteULong(ulong.MinValue);
        write.WriteULong(ulong.MaxValue);
        write.WriteFloat(float.MinValue);
        write.WriteFloat(float.MaxValue);
        write.WriteBool(true);
        write.WriteBool(false);

        var read = new Message(write.ToArray());
        Assert.Equal(int.MinValue, read.ReadInt());
        Assert.Equal(int.MaxValue, read.ReadInt());
        Assert.Equal(uint.MinValue, read.ReadUInt());
        Assert.Equal(uint.MaxValue, read.ReadUInt());
        Assert.Equal(long.MinValue, read.ReadLong());
        Assert.Equal(long.MaxValue, read.ReadLong());
        Assert.Equal(ulong.MinValue, read.ReadULong());
        Assert.Equal(ulong.MaxValue, read.ReadULong());
        Assert.Equal(float.MinValue, read.ReadFloat());
        Assert.Equal(float.MaxValue, read.ReadFloat());
        Assert.True(read.ReadBool());
        Assert.False(read.ReadBool());
    }

    [Fact]
    public void Int32_And_UInt32_Writes_Append_Exactly_Four_Bytes_Each()
    {
        var message = NewMessage();
        var headerLength = message.Length();

        message.WriteInt(123456789);
        message.WriteUInt(3456789012u);

        Assert.Equal(headerLength + 8, message.Length());
    }

    [Fact]
    public void Protocol_Header_Encoding_Remains_Compatible_For_V2_And_V3()
    {
        var v2 = new Message(0xA1B2C3D4u, "method", 0x11223344);
        var v3 = new Message(0xA1B2C3D4u, "method", 0x11223344, "shared(System.Int32)");

        var v2Bytes = v2.ToArray();
        var v3Bytes = v3.ToArray();

        var sharedPrefix = new byte[]
        {
            0x02,
            0xD4, 0xC3, 0xB2, 0xA1,
            0x06, 0x00, 0x00, 0x00,
            0x6D, 0x65, 0x74, 0x68, 0x6F, 0x64,
            0x44, 0x33, 0x22, 0x11,
        };

        Assert.Equal(sharedPrefix, v2Bytes);
        Assert.Equal((byte)0x03, v3Bytes[0]);
        Assert.Equal(sharedPrefix[1..], v3Bytes[1..sharedPrefix.Length]);
        Assert.Equal(0x01, v3Bytes[sharedPrefix.Length]);

        var readV2 = new Message(v2Bytes);
        var readV3 = new Message(v3Bytes);

        Assert.Equal((byte)2, readV2.ProtocolVersion);
        Assert.Null(readV2.OverloadKey);
        Assert.Equal((byte)3, readV3.ProtocolVersion);
        Assert.Equal("shared(System.Int32)", readV3.OverloadKey);
    }
}
