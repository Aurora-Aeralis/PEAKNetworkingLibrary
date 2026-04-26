using NetworkingLibrary.Modules;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace NetworkingLibrary.Tests;

public class MessageDefaultSizePolicyCompatibilityTests
{
    [Fact]
    public void GetMaxSize_And_MaxLogicalSize_Track_SetMaxSize_Deterministically()
    {
        Message.SetMaxSize(Message.DefaultMaxSize);
        try
        {
            Message.SetMaxSize(4096);

            Assert.Equal(4096, Message.GetMaxSize());
            Assert.Equal(4096 * 16, Message.MaxLogicalSize);
            Assert.Equal(4096, Message.DefaultSizePolicy.MaxSize);
            Assert.Equal(4096 * 16, Message.DefaultSizePolicy.MaxLogicalSize);

            using var message = new Message(1u, "policy", 0);
            Assert.Equal(4096, message.SizePolicy.MaxSize);
        }
        finally
        {
            Message.SetMaxSize(Message.DefaultMaxSize);
        }
    }

    [Fact]
    public void Legacy_MaxSize_Field_Write_Is_Synchronized_On_Effective_Reads()
    {
        Message.SetMaxSize(Message.DefaultMaxSize);
        try
        {
            Message.MaxSize = 8192;

            Assert.Equal(8192, Message.GetMaxSize());
            Assert.Equal(8192 * 16, Message.MaxLogicalSize);
            Assert.Equal(8192, Message.DefaultSizePolicy.MaxSize);

            using var message = new Message(1u, "legacy", 0);
            Assert.Equal(8192, message.SizePolicy.MaxSize);
        }
        finally
        {
            Message.SetMaxSize(Message.DefaultMaxSize);
        }
    }

    [Fact]
    public async Task Concurrent_SetMaxSize_And_MessageReads_Maintain_Consistent_MaxLogicalSize()
    {
        var low = 2048;
        var high = 8192;
        var oversizedForLow = new byte[(low * 16) + 1];
        var errors = new ConcurrentQueue<Exception>();
        var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(800);

        Message.SetMaxSize(high);
        try
        {
            var writer = Task.Run(() =>
            {
                try
                {
                    while (DateTime.UtcNow < until)
                    {
                        Message.SetMaxSize(low);
                        Message.SetMaxSize(high);
                        Message.MaxSize = low;
                        _ = Message.GetMaxSize();
                        Message.MaxSize = high;
                        _ = Message.GetMaxSize();
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            });

            var reader = Task.Run(() =>
            {
                try
                {
                    while (DateTime.UtcNow < until)
                    {
                        var maxSize = Message.GetMaxSize();
                        var maxLogicalSize = Message.MaxLogicalSize;
                        Assert.Equal(maxSize * 16, maxLogicalSize);

                        using var write = new Message(7u, "size", 0);
                        Assert.Equal(write.SizePolicy.MaxSize * 16, write.SizePolicy.MaxLogicalSize);

                        try
                        {
                            using var read = new Message(oversizedForLow);
                            Assert.True(read.SizePolicy.MaxLogicalSize >= oversizedForLow.Length);
                        }
                        catch (InvalidDataException)
                        {
                            // Low-policy reads are expected to reject this payload.
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            });

            await Task.WhenAll(writer, reader);
            Assert.Empty(errors);
        }
        finally
        {
            Message.SetMaxSize(Message.DefaultMaxSize);
        }
    }
}
