using System;

namespace MSCCoop.Net;

// Offset is taken from the lowest-RTT sample in the window: queueing delay only ever adds latency,
// so the fastest round trip is the least skewed one.
public sealed class ClockSync
{
    const int Window = 16;
    const double SnapThreshold = 1.0;
    const double Smoothing = 0.1;

    readonly double[] rtts = new double[Window];
    readonly double[] offsets = new double[Window];
    int count;
    int next;

    public bool HasSample { get; private set; }
    public double Rtt { get; private set; }
    public double Offset { get; private set; }

    public void AddSample(double sentLocal, double remoteTime, double receivedLocal)
    {
        var rtt = receivedLocal - sentLocal;
        if (rtt < 0)
            return;

        var offset = remoteTime + rtt / 2 - receivedLocal;
        if (HasSample && Math.Abs(offset - Offset) > SnapThreshold)
        {
            count = 0;
            HasSample = false;
        }

        rtts[next] = rtt;
        offsets[next] = offset;
        next = (next + 1) % Window;
        if (count < Window)
            count++;

        var best = (next + Window - 1) % Window;
        for (var n = 0; n < count; n++)
        {
            var i = (next + Window - 1 - n) % Window;
            if (rtts[i] < rtts[best])
                best = i;
        }

        var target = offsets[best];
        if (!HasSample)
        {
            Offset = target;
            Rtt = rtt;
            HasSample = true;
            return;
        }
        Offset += (target - Offset) * Smoothing;
        Rtt += (rtt - Rtt) * Smoothing;
    }

    public double RemoteToLocal(double remoteTime) => remoteTime - Offset;
    public double LocalToRemote(double localTime) => localTime + Offset;
}
