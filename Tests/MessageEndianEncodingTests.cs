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
}
