using System;
using System.Collections.Generic;
using MSCCoop.Net;
using Steamworks;

namespace MSCCoop;

sealed class SteamTransport : ITransport
{
    const int ChannelCount = 3;
    const double CloseDelay = 1;

    readonly Callback<P2PSessionRequest_t> sessionRequest;
    readonly Callback<P2PSessionConnectFail_t> connectFail;
    readonly byte[] discard = new byte[Protocol.MaxReliableBytes];
    readonly List<KeyValuePair<ulong, double>> pendingClose = new();
    int nextChannel;
    double now;

    public SteamTransport(Func<ulong, bool> isAllowed)
    {
        LocalId = SteamUser.GetSteamID().m_SteamID;
        sessionRequest = Callback<P2PSessionRequest_t>.Create(r =>
        {
            if (isAllowed(r.m_steamIDRemote.m_SteamID))
                SteamNetworking.AcceptP2PSessionWithUser(r.m_steamIDRemote);
        });
        connectFail = Callback<P2PSessionConnectFail_t>.Create(r => PeerLost?.Invoke(r.m_steamIDRemote.m_SteamID));
    }

    public ulong LocalId { get; }
    public event Action<ulong> PeerLost;

    public bool Send(ulong peer, Channel channel, byte[] data, int length)
    {
        var type = channel == Channel.State ? EP2PSend.k_EP2PSendUnreliable : EP2PSend.k_EP2PSendReliable;
        return SteamNetworking.SendP2PPacket(new CSteamID(peer), data, (uint)length, type, (int)channel);
    }

    public bool Receive(byte[] buffer, out ulong peer, out Channel channel, out int length)
    {
        for (var n = 0; n < ChannelCount; n++)
        {
            var ch = (nextChannel + n) % ChannelCount;
            while (SteamNetworking.IsP2PPacketAvailable(out var size, ch))
            {
                var target = size <= buffer.Length ? buffer : discard;
                if (!SteamNetworking.ReadP2PPacket(target, (uint)target.Length, out size, out var remote, ch))
                    break;
                if (target == discard)
                    continue;

                nextChannel = (ch + 1) % ChannelCount;
                peer = remote.m_SteamID;
                channel = (Channel)ch;
                length = (int)size;
                return true;
            }
        }
        peer = 0;
        channel = 0;
        length = 0;
        return false;
    }

    // Closing right away would discard a Disconnect message still queued for that peer.
    public void Close(ulong peer) => pendingClose.Add(new KeyValuePair<ulong, double>(peer, now + CloseDelay));

    public void Update(double time)
    {
        now = time;
        for (var i = pendingClose.Count - 1; i >= 0; i--)
        {
            if (pendingClose[i].Value > now)
                continue;
            SteamNetworking.CloseP2PSessionWithUser(new CSteamID(pendingClose[i].Key));
            pendingClose.RemoveAt(i);
        }
    }

    public void Dispose()
    {
        sessionRequest.Dispose();
        connectFail.Dispose();
    }
}
