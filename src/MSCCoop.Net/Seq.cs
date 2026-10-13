namespace MSCCoop.Net;

public static class Seq
{
    public static bool IsNewer(ushort a, ushort b)
    {
        var diff = (ushort)(a - b);
        return diff != 0 && diff < 32768;
    }
}
