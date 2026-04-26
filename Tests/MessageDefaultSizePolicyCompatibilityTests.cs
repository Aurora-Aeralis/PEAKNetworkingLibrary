using NetworkingLibrary.Modules;
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
}
