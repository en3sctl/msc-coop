using System;

namespace MSCCoop.Net;

public interface ITransport
{
    ulong LocalId { get; }

    event Action<ulong> PeerLost;

    bool Send(ulong peer, Channel channel, byte[] data, int length);
    bool Receive(byte[] buffer, out ulong peer, out Channel channel, out int length);
    void Close(ulong peer);
    void Update(double now);
}
