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
}
