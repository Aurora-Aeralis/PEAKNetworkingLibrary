using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using NetworkingLibrary.Modules;
using Xunit;

namespace NetworkingLibrary.Tests;

public class MessageSerializerConcurrencyTests
{
    private sealed class Payload
    {
        public int Value { get; set; }
    }

    [Fact]
    public async Task Concurrent_RegisterUnregister_With_Active_ReadWrite_DoesNotThrow()
    {
        var errors = new ConcurrentQueue<Exception>();
        var runtime = TimeSpan.FromMilliseconds(500);
        var until = DateTime.UtcNow + runtime;

        var registerTask = Task.Run(() =>
        {
            try
            {
                while (DateTime.UtcNow < until)
                {
                    Message.RegisterSerializer<Payload>(
                        (m, payload) => m.WriteInt(payload.Value),
                        m => new Payload { Value = m.ReadInt() });
                    Message.UnregisterSerializer<Payload>();
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        });

        var writeReadTask = Task.Run(() =>
        {
            try
            {
                while (DateTime.UtcNow < until)
                {
                    var write = new Message(1u, "method", 0);
                    write.WriteObject(typeof(int), 42);
                    write.WriteObject(typeof(string), "ok");

                    var read = new Message(write.ToArray());
                    Assert.Equal(42, read.ReadObject(typeof(int)));
                    Assert.Equal("ok", read.ReadObject(typeof(string)));
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        });

        var setBytesTask = Task.Run(() =>
        {
            try
            {
                while (DateTime.UtcNow < until)
                {
                    var source = new Message(2u, "setbytes", 1);
                    source.WriteInt(77);
                    source.WriteString("cache");
                    var payload = source.ToArray();

                    var target = new Message(0u, "placeholder", 0);
                    target.SetBytes(payload);
                    Assert.Equal(77, target.ReadInt());
                    Assert.Equal("cache", target.ReadString());
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        });

        await Task.WhenAll(registerTask, writeReadTask, setBytesTask);
        Assert.Empty(errors);
    }

    [Fact]
    public void Final_Registered_Serializer_IsUsed_Deterministically()
    {
        Message.RegisterSerializer<Payload>(
            (m, payload) => m.WriteInt(payload.Value + 1),
            m => new Payload { Value = m.ReadInt() - 1 });

        Message.RegisterSerializer<Payload>(
            (m, payload) => m.WriteInt(payload.Value + 1000),
            m => new Payload { Value = m.ReadInt() - 1000 });

        var write = new Message(1u, "method", 0);
        write.WriteObject(typeof(Payload), new Payload { Value = 7 });

        var read = new Message(write.ToArray());
        var payload = (Payload)read.ReadObject(typeof(Payload));
        Assert.Equal(7, payload.Value);

        Message.UnregisterSerializer<Payload>();
    }
}
