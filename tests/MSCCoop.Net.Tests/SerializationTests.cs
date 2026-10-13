using System;
using MSCCoop.Net;

namespace MSCCoop.Net.Tests;

public class SerializationTests
{
    [Fact]
    public void RandomBitFieldsRoundTrip()
    {
        var random = new Random(42);
        var buffer = new byte[4096];
        var writer = new BitWriter(buffer);
        var values = new (uint value, int bits)[500];
        for (var i = 0; i < values.Length; i++)
        {
            var bits = random.Next(1, 33);
            var value = (uint)random.NextInt64(0, 1L << bits);
            values[i] = (value, bits);
            writer.Write(value, bits);
        }
        Assert.False(writer.Overflow);

        var reader = new BitReader(buffer, writer.Length);
        foreach (var (value, bits) in values)
            Assert.Equal(value, reader.Read(bits));
        Assert.False(reader.Overflow);
    }

    [Fact]
    public void TypedValuesRoundTrip()
    {
        var buffer = new byte[256];
        var writer = new BitWriter(buffer);
        writer.WriteBool(true);
        writer.WriteULong(76561198000000001UL);
        writer.WriteFloat(-123.456f);
        writer.WriteString("kubi ğüş", 32);
        writer.WriteUShort(65535);

        var reader = new BitReader(buffer, writer.Length);
        Assert.True(reader.ReadBool());
        Assert.Equal(76561198000000001UL, reader.ReadULong());
        Assert.Equal(-123.456f, reader.ReadFloat());
        Assert.Equal("kubi ğüş", reader.ReadString(32));
        Assert.Equal(65535, reader.ReadUShort());
        Assert.False(reader.Overflow);
    }

    [Fact]
    public void WriterFlagsOverflowInsteadOfThrowing()
    {
        var writer = new BitWriter(new byte[2]);
        writer.Write(0xFFFF, 16);
        writer.Write(1, 1);
        Assert.True(writer.Overflow);
        Assert.Equal(2, writer.Length);
    }

    [Fact]
    public void ReaderFlagsTruncatedInput()
    {
        var buffer = new byte[] { 0xAB };
        var reader = new BitReader(buffer, 1);
        reader.Read(8);
        Assert.Equal(0u, reader.Read(1));
        Assert.True(reader.Overflow);
    }

    [Fact]
    public void StringLongerThanLimitIsRejected()
    {
        var buffer = new byte[256];
        var writer = new BitWriter(buffer);
        writer.WriteString("abcdef", 32);
        var reader = new BitReader(buffer, writer.Length);
        Assert.Equal(string.Empty, reader.ReadString(3));
        Assert.True(reader.Overflow);
    }

    [Theory]
    [InlineData(0, 65535, true)]
    [InlineData(65535, 0, false)]
    [InlineData(100, 99, true)]
    [InlineData(99, 100, false)]
    [InlineData(5, 5, false)]
    [InlineData(32767, 0, true)]
    [InlineData(32768, 0, false)]
    public void SequenceComparisonWraps(int a, int b, bool newer) =>
        Assert.Equal(newer, Seq.IsNewer((ushort)a, (ushort)b));
}
