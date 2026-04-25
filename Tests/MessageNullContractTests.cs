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
}
