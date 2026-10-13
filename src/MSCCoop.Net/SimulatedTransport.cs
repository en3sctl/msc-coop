using System;
using System.Collections.Generic;

namespace MSCCoop.Net;

public sealed class SimulatedTransport : ITransport
{
    struct Pending
    {
        public double Due;
        public ulong Peer;
        public Channel Channel;
        public byte[] Data;
        public int Length;
    }

    readonly ITransport inner;
    readonly Random random;
    readonly List<Pending> pending = new();
    readonly Stack<byte[]> pool = new();
    double now;
    double lastReliableDue;

    public SimulatedTransport(ITransport inner, int seed = 1)
    {
        this.inner = inner;
        random = new Random(seed);
        inner.PeerLost += peer => PeerLost?.Invoke(peer);
    }

    public double Latency { get; set; }
    public double Jitter { get; set; }
    public double Loss { get; set; }
    public double Duplicate { get; set; }
    public bool Enabled => Latency > 0 || Jitter > 0 || Loss > 0 || Duplicate > 0;

    public ulong LocalId => inner.LocalId;
    public event Action<ulong> PeerLost;

    public bool Send(ulong peer, Channel channel, byte[] data, int length)
    {
        if (!Enabled && pending.Count == 0)
            return inner.Send(peer, channel, data, length);

        if (channel == Channel.State)
        {
            if (random.NextDouble() < Loss)
                return true;
            Enqueue(now + Latency + random.NextDouble() * Jitter, peer, channel, data, length);
            if (random.NextDouble() < Duplicate)
                Enqueue(now + Latency + random.NextDouble() * Jitter, peer, channel, data, length);
            return true;
        }

        lastReliableDue = Math.Max(now + Latency, lastReliableDue);
        Enqueue(lastReliableDue, peer, channel, data, length);
        return true;
    }

    public bool Receive(byte[] buffer, out ulong peer, out Channel channel, out int length) =>
        inner.Receive(buffer, out peer, out channel, out length);

    public void Close(ulong peer) => inner.Close(peer);

    public void Update(double time)
    {
        now = time;
        var kept = 0;
        for (var i = 0; i < pending.Count; i++)
        {
            var p = pending[i];
            if (p.Due <= now)
            {
                inner.Send(p.Peer, p.Channel, p.Data, p.Length);
                pool.Push(p.Data);
            }
            else
            {
                pending[kept++] = p;
            }
        }
        pending.RemoveRange(kept, pending.Count - kept);
        inner.Update(time);
    }

    void Enqueue(double due, ulong peer, Channel channel, byte[] data, int length)
    {
        var copy = pool.Count > 0 ? pool.Pop() : new byte[Protocol.MaxReliableBytes];
        if (copy.Length < length)
            copy = new byte[length];
        System.Buffer.BlockCopy(data, 0, copy, 0, length);
        pending.Add(new Pending { Due = due, Peer = peer, Channel = channel, Data = copy, Length = length });
    }
}
