using System;

namespace MSCCoop.Net;

public sealed class BitWriter
{
    byte[] buffer;
    int bitPosition;
    int capacityBits;

    public BitWriter(byte[] buffer) => Reset(buffer);

    public byte[] Buffer => buffer;
    public int BitPosition => bitPosition;
    public int Length => (bitPosition + 7) >> 3;
    public int RemainingBits => capacityBits - bitPosition;
    public bool Overflow { get; private set; }

    public void Reset(byte[] target)
    {
        buffer = target;
        capacityBits = target.Length * 8;
        bitPosition = 0;
        Overflow = false;
    }

    public void Reset() => Reset(buffer);

    public void Write(uint value, int bits)
    {
        if (bits <= 0)
            return;
        if (bitPosition + bits > capacityBits)
        {
            Overflow = true;
            return;
        }

        while (bits > 0)
        {
            var byteIndex = bitPosition >> 3;
            var bitOffset = bitPosition & 7;
            if (bitOffset == 0)
                buffer[byteIndex] = 0;
            var take = Math.Min(8 - bitOffset, bits);
            buffer[byteIndex] |= (byte)((value & ((1u << take) - 1)) << bitOffset);
            value >>= take;
            bits -= take;
            bitPosition += take;
        }
    }

    // Fills in a field written earlier, typically a count that is only known after the entries.
    public void Patch(int position, uint value, int bits)
    {
        for (var bit = 0; bit < bits; bit++)
        {
            var at = position + bit;
            var mask = (byte)(1 << (at & 7));
            if ((value >> bit & 1) != 0)
                buffer[at >> 3] |= mask;
            else
                buffer[at >> 3] &= (byte)~mask;
        }
    }

    public void WriteBool(bool value) => Write(value ? 1u : 0u, 1);
    public void WriteByte(byte value) => Write(value, 8);
    public void WriteUShort(ushort value) => Write(value, 16);
    public void WriteUInt(uint value) => Write(value, 32);

    public void WriteULong(ulong value)
    {
        Write((uint)value, 32);
        Write((uint)(value >> 32), 32);
    }

    public void WriteFloat(float value) => Write(new FloatBits { Float = value }.UInt, 32);

    public void WriteString(string value, int maxChars)
    {
        var length = value == null ? 0 : Math.Min(value.Length, maxChars);
        Write((uint)length, 8);
        for (var i = 0; i < length; i++)
            Write(value[i], 16);
    }

    public void WriteBytes(byte[] source, int offset, int count)
    {
        for (var i = 0; i < count; i++)
            Write(source[offset + i], 8);
    }
}
