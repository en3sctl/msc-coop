using System.Runtime.InteropServices;

namespace MSCCoop.Net;

[StructLayout(LayoutKind.Explicit)]
struct FloatBits
{
    [FieldOffset(0)] public float Float;
    [FieldOffset(0)] public uint UInt;
}
