namespace MSCCoop.Net;

public struct ObjectState
{
    public uint Id;
    public Vec3 Position;
    public Quat Rotation;
    public Vec3 Velocity;
    public Vec3 AngularVelocity;

    public void Write(BitWriter w)
    {
        w.WriteUInt(Id);
        Quantize.WritePosition(w, Position);
        Quantize.WriteRotation(w, Rotation, ObjectMessages.RotationBits);
        Quantize.WriteVector(w, Velocity, Quantize.VelocityRange, Quantize.VelocityBits);
        Quantize.WriteVector(w, AngularVelocity, Quantize.AngularVelocityRange, Quantize.AngularVelocityBits);
    }

    public static ObjectState Read(BitReader r) => new()
    {
        Id = r.ReadUInt(),
        Position = Quantize.ReadPosition(r),
        Rotation = Quantize.ReadRotation(r, ObjectMessages.RotationBits),
        Velocity = Quantize.ReadVector(r, Quantize.VelocityRange, Quantize.VelocityBits),
        AngularVelocity = Quantize.ReadVector(r, Quantize.AngularVelocityRange, Quantize.AngularVelocityBits),
    };
}

public static class ObjectMessages
{
    public const int RotationBits = 10;
    public const int SlotBits = 3;
    public const int CountBits = 8;
    public const int MaxStateBits = 32 + 66 + 32 + 36 + 30;

    public static void WriteOwner(BitWriter w, uint id, OwnerInfo info)
    {
        w.WriteUInt(id);
        w.Write(info.Owner, SlotBits);
        w.WriteUShort(info.Seq);
        w.WriteBool(info.Held);
    }

    public static OwnerInfo ReadOwner(BitReader r, out uint id)
    {
        id = r.ReadUInt();
        return new OwnerInfo { Owner = (byte)r.Read(SlotBits), Seq = r.ReadUShort(), Held = r.ReadBool() };
    }

    public static void WritePose(BitWriter w, Vec3 position, Quat rotation)
    {
        Quantize.WritePosition(w, position);
        Quantize.WriteRotation(w, rotation, RotationBits);
    }

    public static void ReadPose(BitReader r, out Vec3 position, out Quat rotation)
    {
        position = Quantize.ReadPosition(r);
        rotation = Quantize.ReadRotation(r, RotationBits);
    }
}
