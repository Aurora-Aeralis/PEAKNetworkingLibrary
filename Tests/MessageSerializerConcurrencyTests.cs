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

                    Assert.Equal((byte)2, target.ReadByte());
                    Assert.Equal(2u, target.ReadUInt());
                    Assert.Equal("setbytes", target.ReadString());
                    Assert.Equal(1, target.ReadInt());

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
    public void Primitive_WireBytes_AreStable_Across_Repeated_Writes()
    {
        var expectedInt = BitConverter.GetBytes(unchecked((int)0x89ABCDEF));
        var expectedUInt = BitConverter.GetBytes(0x01234567u);
        var expectedLong = BitConverter.GetBytes(unchecked((long)0x0123456789ABCDEFl));
        var expectedULong = BitConverter.GetBytes(0xFEDCBA9876543210ul);
        var expectedFloat = BitConverter.GetBytes(123.25f);

        for (var attempt = 0; attempt < 256; attempt++)
        {
            var message = new Message(1u, "method", 0);
            message.WriteInt(unchecked((int)0x89ABCDEF));
            message.WriteUInt(0x01234567u);
            message.WriteLong(unchecked((long)0x0123456789ABCDEFl));
            message.WriteULong(0xFEDCBA9876543210ul);
            message.WriteFloat(123.25f);

            var bytes = message.ToArray();
            var read = new Message(bytes);

            Assert.Equal(unchecked((int)0x89ABCDEF), read.ReadInt());
            Assert.Equal(0x01234567u, read.ReadUInt());
            Assert.Equal(unchecked((long)0x0123456789ABCDEFl), read.ReadLong());
            Assert.Equal(0xFEDCBA9876543210ul, read.ReadULong());
            Assert.Equal(123.25f, read.ReadFloat());

            var payloadOffset = bytes.Length - (4 + 4 + 8 + 8 + 4);
            Assert.Equal(expectedInt, bytes.AsSpan(payloadOffset, 4).ToArray());
            payloadOffset += 4;
            Assert.Equal(expectedUInt, bytes.AsSpan(payloadOffset, 4).ToArray());
            payloadOffset += 4;
            Assert.Equal(expectedLong, bytes.AsSpan(payloadOffset, 8).ToArray());
            payloadOffset += 8;
            Assert.Equal(expectedULong, bytes.AsSpan(payloadOffset, 8).ToArray());
            payloadOffset += 8;
            Assert.Equal(expectedFloat, bytes.AsSpan(payloadOffset, 4).ToArray());
        }
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
