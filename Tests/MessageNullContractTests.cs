using System;
using System.Collections.Generic;
using NetworkingLibrary.Modules;
using Xunit;

namespace NetworkingLibrary.Tests;

public class MessageNullContractTests
{
    private sealed class UnsupportedRef
    {
        public int Value { get; set; }
    }

    private static Message NewMessage() => new(1u, "method", 0);

    private static Message Roundtrip(Message message) => new(message.ToArray());

    [Fact]
    public void String_RoundTrips_Null_And_Value()
    {
        var write = NewMessage();
        write.WriteObject(typeof(string), null!);
        write.WriteObject(typeof(string), "fox");

        var read = Roundtrip(write);
        Assert.Null(read.ReadObject(typeof(string)));
        Assert.Equal("fox", read.ReadObject(typeof(string)));
    }

    [Fact]
    public void ByteArray_RoundTrips_Null_And_Value()
    {
        var write = NewMessage();
        write.WriteObject(typeof(byte[]), null!);
        write.WriteObject(typeof(byte[]), new byte[] { 1, 2, 3 });

        var read = Roundtrip(write);
        Assert.Null(read.ReadObject(typeof(byte[])));
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])read.ReadObject(typeof(byte[])));
    }

    [Fact]
    public void List_RoundTrips_Null_And_Value()
    {
        var write = NewMessage();
        write.WriteObject(typeof(List<int>), null!);
        write.WriteObject(typeof(List<int>), new List<int> { 4, 5, 6 });

        var read = Roundtrip(write);
        Assert.Null(read.ReadObject(typeof(List<int>)));
        Assert.Equal(new List<int> { 4, 5, 6 }, (List<int>)read.ReadObject(typeof(List<int>)));
    }

    [Fact]
    public void Array_RoundTrips_Null_And_Value()
    {
        var write = NewMessage();
        write.WriteObject(typeof(int[]), null!);
        write.WriteObject(typeof(int[]), new[] { 7, 8 });

        var read = Roundtrip(write);
        Assert.Null(read.ReadObject(typeof(int[])));
        Assert.Equal(new[] { 7, 8 }, (int[])read.ReadObject(typeof(int[])));
    }

    [Fact]
    public void NullableValueType_RoundTrips_Null_And_Value()
    {
        var write = NewMessage();
        write.WriteObject(typeof(int?), null!);
        write.WriteObject(typeof(int?), 42);

        var read = Roundtrip(write);
        Assert.Null(read.ReadObject(typeof(int?)));
        Assert.Equal(42, (int?)read.ReadObject(typeof(int?)));
    }

    [Fact]
    public void UnsupportedCustomRef_Allows_Null_But_Rejects_NonNull()
    {
        var write = NewMessage();
        write.WriteObject(typeof(UnsupportedRef), null!);

        var read = Roundtrip(write);
        Assert.Null(read.ReadObject(typeof(UnsupportedRef)));

        var ex = Assert.Throws<Exception>(() => NewMessage().WriteObject(typeof(UnsupportedRef), new UnsupportedRef { Value = 1 }));
        Assert.Contains("Null handling", ex.Message);
        Assert.Contains("RegisterSerializer", ex.Message);
    }

    [Fact]
    public void ReadObject_Version1_String_DoesNotExpect_PresenceFlag()
    {
        var legacyData = BuildLegacyMessageData((m) => m.WriteString("legacy"));

        var read = new Message(legacyData);
        Assert.Equal((byte)1, read.ProtocolVersion);
        Assert.Equal("legacy", read.ReadObject(typeof(string)));
    }

    [Fact]
    public void ReadObject_Version1_List_DoesNotExpect_PresenceFlag()
    {
        var legacyData = BuildLegacyMessageData((m) =>
        {
            var values = new List<int> { 10, 11, 12 };
            m.WriteInt(values.Count);
            foreach (var value in values)
            {
                m.WriteObject(typeof(int), value);
            }
        });

        var read = new Message(legacyData);
        Assert.Equal((byte)1, read.ProtocolVersion);
        Assert.Equal(new List<int> { 10, 11, 12 }, (List<int>)read.ReadObject(typeof(List<int>)));
    }

    [Fact]
    public void Header_WithoutOverloadKey_UsesVersion2Layout()
    {
        var message = NewMessage();
        message.WriteBool(true);
        message.WriteInt(42);

        var read = Roundtrip(message);
        Assert.Equal((byte)2, read.ProtocolVersion);
        Assert.Null(read.OverloadKey);
        Assert.True(read.ReadBool());
        Assert.Equal(42, read.ReadInt());
    }

    [Fact]
    public void Header_WithOverloadKey_UsesVersion3Layout()
    {
        var message = new Message(1u, "method", 0, "shared(System.String)");
        message.WriteString("value");

        var read = Roundtrip(message);
        Assert.Equal((byte)3, read.ProtocolVersion);
        Assert.Equal("shared(System.String)", read.OverloadKey);
        Assert.Equal("value", read.ReadString());
    }


    [Fact]
    public void ReadString_Rejects_Excessive_Length()
    {
        var malformed = NewMessage();
        malformed.WriteInt(Message.MaxSize + 1);

        var read = Roundtrip(malformed);
        var ex = Assert.Throws<Exception>(() => read.ReadString());
        Assert.Contains("length exceeds max", ex.Message);
    }

    [Fact]
    public void ReadObject_ByteArray_Rejects_Negative_Length()
    {
        var malformed = NewMessage();
        malformed.WriteBool(true);
        malformed.WriteInt(-1);

        var read = Roundtrip(malformed);
        var ex = Assert.Throws<Exception>(() => read.ReadObject(typeof(byte[])));
        Assert.Contains("length out of range", ex.Message);
    }

    [Fact]
    public void ReadObject_List_Rejects_Excessive_Length()
    {
        var malformed = NewMessage();
        malformed.WriteBool(true);
        malformed.WriteInt(Message.MaxSize + 1);

        var read = Roundtrip(malformed);
        var ex = Assert.Throws<Exception>(() => read.ReadObject(typeof(List<int>)));
        Assert.Contains("length exceeds max", ex.Message);
    }

    private static byte[] BuildLegacyMessageData(Action<Message> writePayload)
    {
        var legacy = NewMessage();
        writePayload(legacy);
        var bytes = legacy.ToArray();
        bytes[0] = 1; // simulate older sender protocol that did not include reference presence flags.
        return bytes;
    }
}
