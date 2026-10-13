namespace MSCCoop.Net;

public sealed class TrafficStats
{
    long bytesInWindow;
    long bytesOutWindow;
    double windowStart = -1;

    public long BytesIn { get; private set; }
    public long BytesOut { get; private set; }
    public float KbpsIn { get; private set; }
    public float KbpsOut { get; private set; }

    public void Sent(int bytes)
    {
        BytesOut += bytes;
        bytesOutWindow += bytes;
    }

    public void Received(int bytes)
    {
        BytesIn += bytes;
        bytesInWindow += bytes;
    }

    public void Update(double now)
    {
        if (windowStart < 0)
            windowStart = now;
        var elapsed = now - windowStart;
        if (elapsed < 1)
            return;
        KbpsIn = (float)(bytesInWindow * 8 / 1000.0 / elapsed);
        KbpsOut = (float)(bytesOutWindow * 8 / 1000.0 / elapsed);
        bytesInWindow = 0;
        bytesOutWindow = 0;
        windowStart = now;
    }
}
