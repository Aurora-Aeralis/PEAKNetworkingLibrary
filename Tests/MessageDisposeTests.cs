using System;
using NetworkingLibrary.Modules;
using Xunit;

namespace NetworkingLibrary.Tests;

public class MessageDisposeTests
{
    private static Message NewMessage() => new(1u, "method", 0);

    [Fact]
    public void ToArray_Throws_ObjectDisposedException_After_Dispose()
    {
        var message = NewMessage();
        message.Dispose();
        Assert.Throws<ObjectDisposedException>(() => message.ToArray());
    }

    [Fact]
    public void Write_Methods_Throw_ObjectDisposedException_After_Dispose()
    {
        var message = NewMessage();
        message.Dispose();
        Assert.Throws<ObjectDisposedException>(() => message.WriteInt(7));
        Assert.Throws<ObjectDisposedException>(() => message.WriteObject(typeof(int), 7));
    }

    [Fact]
    public void Read_Methods_Throw_ObjectDisposedException_After_Dispose()
    {
        var message = NewMessage();
        message.WriteInt(42);
        var bytes = message.ToArray();
        var read = new Message(bytes);
        read.Dispose();
        Assert.Throws<ObjectDisposedException>(() => read.ReadInt());
        Assert.Throws<ObjectDisposedException>(() => read.ReadObject(typeof(int)));
    }

    [Fact]
    public void Reset_Throws_ObjectDisposedException_After_Dispose()
    {
        var message = NewMessage();
        message.Dispose();
        Assert.Throws<ObjectDisposedException>(() => message.Reset());
    }

    [Fact]
    public void Length_And_UnreadLength_Throw_ObjectDisposedException_After_Dispose()
    {
        var message = NewMessage();
        message.Dispose();
        Assert.Throws<ObjectDisposedException>(() => message.Length());
        Assert.Throws<ObjectDisposedException>(() => message.UnreadLength());
    }

    [Fact]
    public void ToArray_Returns_Copy_And_Does_Not_Leak_Internal_Buffer()
    {
        var message = NewMessage();
        message.WriteInt(99);

        var bytes = message.ToArray();
        var original = bytes[bytes.Length - 1];
        bytes[bytes.Length - 1] = (byte)(original ^ 0xFF);

        var afterMutation = message.ToArray();
        Assert.Equal(original, afterMutation[afterMutation.Length - 1]);
    }
}
