using System;
using System.Collections.Generic;
using MSCCoop.Net;

namespace MSCCoop.Net.Tests;

sealed class MemoryHub
{
    readonly Dictionary<ulong, Queue<(ulong from, Channel channel, byte[] data)>> inboxes = new();

    public MemoryTransport Create(ulong id)
    {
        inboxes[id] = new Queue<(ulong, Channel, byte[])>();
        return new MemoryTransport(this, id);
    }

    public void Deliver(ulong from, ulong to, Channel channel, byte[] data, int length)
    {
        if (inboxes.TryGetValue(to, out var inbox))
            inbox.Enqueue((from, channel, data.AsSpan(0, length).ToArray()));
    }

    public bool Take(ulong id, out (ulong from, Channel channel, byte[] data) packet) => inboxes[id].TryDequeue(out packet);

    public void Disconnect(ulong id) => inboxes.Remove(id);
}

sealed class MemoryTransport(MemoryHub hub, ulong id) : ITransport
{
    public ulong LocalId => id;
    public event Action<ulong>? PeerLost;

    public bool Send(ulong peer, Channel channel, byte[] data, int length)
    {
        hub.Deliver(id, peer, channel, data, length);
        return true;
    }

    public bool Receive(byte[] buffer, out ulong peer, out Channel channel, out int length)
    {
        if (!hub.Take(id, out var p))
        {
            peer = 0;
            channel = 0;
            length = 0;
            return false;
        }
        p.data.CopyTo(buffer, 0);
        peer = p.from;
        channel = p.channel;
        length = p.data.Length;
        return true;
    }

    public void Close(ulong peer) { }

    public void Update(double now) { }

    public void RaiseLost(ulong peer) => PeerLost?.Invoke(peer);
}
