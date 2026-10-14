using System;

namespace MSCCoop.Net;

[Flags]
public enum PlayerFlags : byte
{
    None = 0,
    Crouching = 1,
    InVehicle = 2,
}

public struct PlayerState
{
    const int FlagBits = 2;
    const int YawBits = 10;
    const int PitchBits = 8;

    public byte Slot;
    public double Time;
    public Vec3 Position;
    public Vec3 Velocity;
    public float Yaw;
    public float Pitch;
    public PlayerFlags Flags;

    // Non-zero while seated: position and yaw are then relative to that vehicle, so the avatar
    // stays in its seat instead of following a second, separately interpolated stream.
    public uint Vehicle;

    public void Write(BitWriter w)
    {
        w.Write(Slot, 2);
        w.WriteUInt((uint)(long)(Time * 1000));
        Quantize.WritePosition(w, Position);
        Quantize.WriteVector(w, Velocity, Quantize.VelocityRange, Quantize.VelocityBits);
        Quantize.WriteRanged(w, Repeat(Yaw), 0, 360, YawBits);
        Quantize.WriteRanged(w, Pitch, -90, 90, PitchBits);
        w.Write((uint)Flags, FlagBits);
        w.WriteBool(Vehicle != 0);
        if (Vehicle != 0)
            w.WriteUInt(Vehicle);
    }

    public static PlayerState Read(BitReader r)
    {
        var s = new PlayerState
        {
            Slot = (byte)r.Read(2),
            Time = r.ReadUInt() / 1000.0,
            Position = Quantize.ReadPosition(r),
            Velocity = Quantize.ReadVector(r, Quantize.VelocityRange, Quantize.VelocityBits),
            Yaw = Quantize.ReadRanged(r, 0, 360, YawBits),
            Pitch = Quantize.ReadRanged(r, -90, 90, PitchBits),
            Flags = (PlayerFlags)r.Read(FlagBits),
        };
        if (r.ReadBool())
            s.Vehicle = r.ReadUInt();
        return s;
    }

    public static Quat YawRotation(float degrees)
    {
        var half = degrees * Math.PI / 360;
        return new Quat(0, (float)Math.Sin(half), 0, (float)Math.Cos(half));
    }

    static float Repeat(float degrees)
    {
        degrees %= 360;
        return degrees < 0 ? degrees + 360 : degrees;
    }
}
