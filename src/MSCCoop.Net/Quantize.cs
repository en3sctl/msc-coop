using System;

namespace MSCCoop.Net;

public static class Quantize
{
    public const float PositionRangeXZ = 8192f;
    public const float PositionRangeY = 1024f;
    public const int PositionBitsXZ = 23;
    public const int PositionBitsY = 20;
    public const float VelocityRange = 64f;
    public const int VelocityBits = 12;
    public const float AngularVelocityRange = 16f;
    public const int AngularVelocityBits = 10;

    const float SmallestThreeRange = 0.70710678f;

    // Double math: at 23 bits a float quotient alone loses about half a step.
    public static void WriteRanged(BitWriter w, float value, float min, float max, int bits)
    {
        double steps = (1u << bits) - 1;
        var t = ((double)MathUtil.Clamp(value, min, max) - min) / ((double)max - min);
        w.Write((uint)Math.Round(t * steps), bits);
    }

    public static float ReadRanged(BitReader r, float min, float max, int bits)
    {
        double steps = (1u << bits) - 1;
        return (float)(min + r.Read(bits) / steps * ((double)max - min));
    }

    public static void WritePosition(BitWriter w, Vec3 p)
    {
        WriteRanged(w, p.X, -PositionRangeXZ, PositionRangeXZ, PositionBitsXZ);
        WriteRanged(w, p.Y, -PositionRangeY, PositionRangeY, PositionBitsY);
        WriteRanged(w, p.Z, -PositionRangeXZ, PositionRangeXZ, PositionBitsXZ);
    }

    public static Vec3 ReadPosition(BitReader r) => new(
        ReadRanged(r, -PositionRangeXZ, PositionRangeXZ, PositionBitsXZ),
        ReadRanged(r, -PositionRangeY, PositionRangeY, PositionBitsY),
        ReadRanged(r, -PositionRangeXZ, PositionRangeXZ, PositionBitsXZ));

    public static void WriteVector(BitWriter w, Vec3 v, float range, int bits)
    {
        WriteRanged(w, v.X, -range, range, bits);
        WriteRanged(w, v.Y, -range, range, bits);
        WriteRanged(w, v.Z, -range, range, bits);
    }

    public static Vec3 ReadVector(BitReader r, float range, int bits) => new(
        ReadRanged(r, -range, range, bits),
        ReadRanged(r, -range, range, bits),
        ReadRanged(r, -range, range, bits));

    // Smallest-three: drop the largest component (recoverable from unit length), sign-flip so it is positive.
    public static void WriteRotation(BitWriter w, Quat q, int bitsPerComponent)
    {
        q = q.Normalized;
        var largest = 0;
        var max = Math.Abs(q.X);
        if (Math.Abs(q.Y) > max) { largest = 1; max = Math.Abs(q.Y); }
        if (Math.Abs(q.Z) > max) { largest = 2; max = Math.Abs(q.Z); }
        if (Math.Abs(q.W) > max) largest = 3;

        var sign = Component(q, largest) < 0 ? -1f : 1f;
        w.Write((uint)largest, 2);
        for (var i = 0; i < 4; i++)
        {
            if (i != largest)
                WriteRanged(w, Component(q, i) * sign, -SmallestThreeRange, SmallestThreeRange, bitsPerComponent);
        }
    }

    static float Component(Quat q, int index) => index switch
    {
        0 => q.X,
        1 => q.Y,
        2 => q.Z,
        _ => q.W,
    };

    public static Quat ReadRotation(BitReader r, int bitsPerComponent)
    {
        var largest = (int)r.Read(2);
        float a = 0, b = 0, c = 0, d = 0;
        float sum = 0;
        for (var i = 0; i < 4; i++)
        {
            if (i == largest)
                continue;
            var v = ReadRanged(r, -SmallestThreeRange, SmallestThreeRange, bitsPerComponent);
            sum += v * v;
            switch (i)
            {
                case 0: a = v; break;
                case 1: b = v; break;
                case 2: c = v; break;
                default: d = v; break;
            }
        }
        var rest = (float)Math.Sqrt(Math.Max(0f, 1f - sum));
        switch (largest)
        {
            case 0: a = rest; break;
            case 1: b = rest; break;
            case 2: c = rest; break;
            default: d = rest; break;
        }
        return new Quat(a, b, c, d).Normalized;
    }
}
