using System;

namespace MSCCoop.Net;

public struct Vec3(float x, float y, float z)
{
    public float X = x, Y = y, Z = z;

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);

    public float Magnitude => (float)Math.Sqrt(X * X + Y * Y + Z * Z);

    public override string ToString() => "(" + X + ", " + Y + ", " + Z + ")";
}

public struct Quat(float x, float y, float z, float w)
{
    public float X = x, Y = y, Z = z, W = w;

    public static readonly Quat Identity = new(0, 0, 0, 1);

    public static float Dot(Quat a, Quat b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

    public Quat Normalized
    {
        get
        {
            var m = (float)Math.Sqrt(Dot(this, this));
            return m < 1e-6f ? Identity : new Quat(X / m, Y / m, Z / m, W / m);
        }
    }

    public static Quat Slerp(Quat a, Quat b, float t)
    {
        var dot = Dot(a, b);
        if (dot < 0)
        {
            b = new Quat(-b.X, -b.Y, -b.Z, -b.W);
            dot = -dot;
        }

        float wa, wb;
        if (dot > 0.9995f)
        {
            wa = 1 - t;
            wb = t;
        }
        else
        {
            var theta = Math.Acos(dot);
            var sin = Math.Sin(theta);
            wa = (float)(Math.Sin((1 - t) * theta) / sin);
            wb = (float)(Math.Sin(t * theta) / sin);
        }
        return new Quat(a.X * wa + b.X * wb, a.Y * wa + b.Y * wb, a.Z * wa + b.Z * wb, a.W * wa + b.W * wb).Normalized;
    }

    public static float AngleDegrees(Quat a, Quat b)
    {
        var dot = Math.Min(Math.Abs(Dot(a.Normalized, b.Normalized)), 1f);
        return (float)(2 * Math.Acos(dot) * 180 / Math.PI);
    }

    public static Quat FromAngularVelocity(Vec3 w, float dt)
    {
        var angle = w.Magnitude * dt;
        if (angle < 1e-6f)
            return Identity;
        var axis = w * (1f / w.Magnitude);
        var s = (float)Math.Sin(angle / 2);
        return new Quat(axis.X * s, axis.Y * s, axis.Z * s, (float)Math.Cos(angle / 2));
    }

    public static Quat operator *(Quat a, Quat b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
}

static class MathUtil
{
    public static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
    public static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
}
