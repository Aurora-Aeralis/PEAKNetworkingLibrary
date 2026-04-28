using NetworkingLibrary.Modules;
using Xunit;

namespace NetworkingLibrary.Tests;

public class MessageIntegerSerializationTests
{
    enum SByteEnum : sbyte { Low = -8, High = 12 }
    enum ShortEnum : short { Low = -300, High = 300 }
    enum UShortEnum : ushort { Low = 7, High = 65000 }

    [Fact]
    public void ShortSized_Primitives_Roundtrip()
    {
        using var write = new Message(1u, "ints", 0);
        write.WriteSByte(-7);
        write.WriteShort(-1234);
        write.WriteUShort(54321);

        using var read = new Message(write.ToArray());
        Assert.Equal((sbyte)-7, read.ReadSByte());
        Assert.Equal((short)-1234, read.ReadShort());
        Assert.Equal((ushort)54321, read.ReadUShort());
    }

    [Fact]
    public void Enum_Backings_SByte_Short_And_UShort_Roundtrip()
    {
        using var write = new Message(1u, "enums", 0);
        write.WriteObject(typeof(SByteEnum), SByteEnum.Low);
        write.WriteObject(typeof(ShortEnum), ShortEnum.High);
        write.WriteObject(typeof(UShortEnum), UShortEnum.High);

        using var read = new Message(write.ToArray());
        Assert.Equal(SByteEnum.Low, (SByteEnum)read.ReadObject(typeof(SByteEnum)));
        Assert.Equal(ShortEnum.High, (ShortEnum)read.ReadObject(typeof(ShortEnum)));
        Assert.Equal(UShortEnum.High, (UShortEnum)read.ReadObject(typeof(UShortEnum)));
    }

    [Fact]
    public void ShortSized_Enum_Array_Uses_Element_Size_For_Bounds_Checking()
    {
        using var write = new Message(1u, "enum-array", 0);
        write.WriteObject(typeof(UShortEnum[]), new[] { UShortEnum.Low, UShortEnum.High });

        using var read = new Message(write.ToArray());
        Assert.Equal(new[] { UShortEnum.Low, UShortEnum.High }, (UShortEnum[])read.ReadObject(typeof(UShortEnum[])));
    }
}
