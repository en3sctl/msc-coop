namespace MSCCoop.Net;

public static class Fnv
{
    public const uint Offset32 = 2166136261;
    public const ulong Offset64 = 14695981039346656037;

    public static uint Hash32(byte[] data, int offset, int count, uint hash = Offset32)
    {
        for (var i = 0; i < count; i++)
        {
            hash ^= data[offset + i];
            hash *= 16777619;
        }
        return hash;
    }

    public static uint Hash32(string text, uint hash = Offset32)
    {
        for (var i = 0; i < text.Length; i++)
        {
            hash ^= text[i];
            hash *= 16777619;
        }
        return hash;
    }

    public static ulong Hash64(string text, ulong hash = Offset64)
    {
        for (var i = 0; i < text.Length; i++)
        {
            hash ^= text[i];
            hash *= 1099511628211;
        }
        return hash;
    }
}
