using System;

namespace MSCCoop.Net;

public sealed class BitReader
{
    byte[] buffer;
    int bitPosition;
    int lengthBits;

    public BitReader(byte[] buffer, int length) => Reset(buffer, length);

    public int BitPosition => bitPosition;
    public int RemainingBits => lengthBits - bitPosition;
    public bool Overflow { get; private set; }

    public void Reset(byte[] source, int length)
    {
        buffer = source;
        lengthBits = length * 8;
        bitPosition = 0;
        Overflow = false;
    }

    public uint Read(int bits)
    {
        if (bits <= 0)
            return 0;
        if (bitPosition + bits > lengthBits)
        {
            Overflow = true;
            bitPosition = lengthBits;
            return 0;
        }

        uint value = 0;
        var shift = 0;
        while (bits > 0)
        {
            var bitOffset = bitPosition & 7;
            var take = Math.Min(8 - bitOffset, bits);
            var chunk = (uint)(buffer[bitPosition >> 3] >> bitOffset) & ((1u << take) - 1);
            value |= chunk << shift;
            shift += take;
            bits -= take;
            bitPosition += take;
        }
        return value;
    }

    public void Skip(int bits)
    {
        if (bitPosition + bits > lengthBits)
        {
            Overflow = true;
            bitPosition = lengthBits;
            return;
        }
        bitPosition += bits;
    }

    public bool ReadBool() => Read(1) != 0;
    public byte ReadByte() => (byte)Read(8);
    public ushort ReadUShort() => (ushort)Read(16);
    public uint ReadUInt() => Read(32);

    public ulong ReadULong()
    {
        ulong low = Read(32);
        ulong high = Read(32);
        return low | (high << 32);
    }

    public float ReadFloat() => new FloatBits { UInt = Read(32) }.Float;

    public string ReadString(int maxChars)
    {
        var length = (int)Read(8);
        if (length > maxChars)
        {
            Overflow = true;
            return string.Empty;
        }
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = (char)Read(16);
        return Overflow ? string.Empty : new string(chars);
    }

    public void ReadBytes(byte[] target, int offset, int count)
    {
        for (var i = 0; i < count; i++)
            target[offset + i] = (byte)Read(8);
    }
}
