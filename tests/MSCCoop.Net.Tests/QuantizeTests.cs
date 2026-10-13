using System;
using MSCCoop.Net;

namespace MSCCoop.Net.Tests;

public class QuantizeTests
{
    static Quat RandomRotation(Random r)
    {
        var q = new Quat((float)(r.NextDouble() * 2 - 1), (float)(r.NextDouble() * 2 - 1),
            (float)(r.NextDouble() * 2 - 1), (float)(r.NextDouble() * 2 - 1));
        return q.Normalized;
    }

    [Fact]
    public void PositionErrorStaysUnderTwoMillimetres()
    {
        var random = new Random(7);
        var buffer = new byte[64];
        for (var i = 0; i < 10000; i++)
        {
            var p = new Vec3((float)(random.NextDouble() * 6000 - 3000), (float)(random.NextDouble() * 400 - 50),
                (float)(random.NextDouble() * 6000 - 3000));
            var writer = new BitWriter(buffer);
            Quantize.WritePosition(writer, p);
            var q = Quantize.ReadPosition(new BitReader(buffer, writer.Length));
            Assert.True((p - q).Magnitude < 0.002f, $"{p} -> {q}");
        }
    }

    [Theory]
    [InlineData(10, 0.25f)]
    [InlineData(11, 0.12f)]
    public void RotationErrorIsBounded(int bits, float maxDegrees)
    {
        var random = new Random(3);
        var buffer = new byte[16];
        for (var i = 0; i < 10000; i++)
        {
            var rotation = RandomRotation(random);
            var writer = new BitWriter(buffer);
            Quantize.WriteRotation(writer, rotation, bits);
            Assert.Equal(2 + 3 * bits, writer.BitPosition);
            var decoded = Quantize.ReadRotation(new BitReader(buffer, writer.Length), bits);
            Assert.True(Quat.AngleDegrees(rotation, decoded) < maxDegrees);
        }
    }

    [Fact]
    public void NegatedQuaternionDecodesToSameRotation()
    {
        var q = new Quat(0.3f, -0.5f, 0.1f, -0.8f).Normalized;
        var neg = new Quat(-q.X, -q.Y, -q.Z, -q.W);
        var buffer = new byte[16];
        var writer = new BitWriter(buffer);
        Quantize.WriteRotation(writer, neg, 11);
        var decoded = Quantize.ReadRotation(new BitReader(buffer, writer.Length), 11);
        Assert.True(Quat.AngleDegrees(q, decoded) < 0.12f);
    }

    [Fact]
    public void OutOfRangeValuesClamp()
    {
        var buffer = new byte[16];
        var writer = new BitWriter(buffer);
        Quantize.WriteVector(writer, new Vec3(1000, -1000, 0), Quantize.VelocityRange, Quantize.VelocityBits);
        var v = Quantize.ReadVector(new BitReader(buffer, writer.Length), Quantize.VelocityRange, Quantize.VelocityBits);
        Assert.Equal(Quantize.VelocityRange, v.X, 3);
        Assert.Equal(-Quantize.VelocityRange, v.Y, 3);
        Assert.Equal(0, v.Z, 1);
    }
}
