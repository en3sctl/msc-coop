using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using MSCCoop.Net;

namespace MSCCoop;

// Development transport for two game instances on one machine or a LAN.
// Reliable channels go over TCP framed as [u16 length][u8 channel][payload],
// the state channel over UDP as [u64 sender][u8 channel][payload].
sealed class LanTransport : ITransport
{
    public const int DefaultPort = 27250;
    public const ulong HostId = 1;

    const byte IdentChannel = 255;
    const int TcpHeader = 3;
    const int UdpHeader = 9;
    const int InboxSize = 4 * Protocol.MaxReliableBytes;

    sealed class Peer
    {
        public ulong Id;
        public Socket Tcp;
        public EndPoint Udp;
        public readonly byte[] Inbox = new byte[InboxSize];
        public int InboxCount;
    }

    readonly bool isHost;
    readonly Socket udp;
    readonly Socket listener;
    readonly List<Peer> peers = new();
    readonly byte[] frame = new byte[Protocol.MaxReliableBytes + UdpHeader];
    EndPoint hostUdp;
    IAsyncResult pendingConnect;
    Socket connecting;
    EndPoint receiveFrom = new IPEndPoint(IPAddress.Any, 0);

    LanTransport(ulong localId, bool isHost)
    {
        LocalId = localId;
        this.isHost = isHost;
        udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // Without this, an ICMP port-unreachable from a closed peer makes every later ReceiveFrom throw on Windows.
            udp.IOControl(-1744830452, new byte[] { 0 }, null);
        }
        catch (Exception)
        {
        }
        if (isHost)
        {
            listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }
    }

    public ulong LocalId { get; }
    public event Action<ulong> PeerLost;

    public static LanTransport Listen(int port)
    {
        var t = new LanTransport(HostId, true);
        t.listener.Bind(new IPEndPoint(IPAddress.Any, port));
        t.listener.Listen(8);
        t.udp.Bind(new IPEndPoint(IPAddress.Any, port));
        return t;
    }

    public static LanTransport Connect(IPAddress address, int port)
    {
        var random = new Random();
        var id = ((ulong)(uint)random.Next() << 32 | (uint)random.Next()) | 2;
        var t = new LanTransport(id, false);
        t.udp.Bind(new IPEndPoint(IPAddress.Any, 0));
        t.hostUdp = new IPEndPoint(address, port);
        t.connecting = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        t.pendingConnect = t.connecting.BeginConnect(new IPEndPoint(address, port), null, null);
        return t;
    }

    public bool Send(ulong peerId, Channel channel, byte[] data, int length)
    {
        if (channel == Channel.State)
        {
            var target = isHost ? Find(peerId)?.Udp : hostUdp;
            if (target == null)
                return false;
            WriteULong(frame, 0, LocalId);
            frame[8] = (byte)channel;
            Buffer.BlockCopy(data, 0, frame, UdpHeader, length);
            try
            {
                udp.SendTo(frame, length + UdpHeader, SocketFlags.None, target);
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        var peer = Find(peerId);
        if (peer == null)
            return false;
        return SendFrame(peer, (byte)channel, data, length);
    }

    public bool Receive(byte[] buffer, out ulong peer, out Channel channel, out int length)
    {
        while (udp.Available > 0)
        {
            int size;
            try
            {
                size = udp.ReceiveFrom(frame, ref receiveFrom);
            }
            catch (SocketException)
            {
                continue;
            }
            if (size < UdpHeader || size - UdpHeader > buffer.Length)
                continue;
            var sender = ReadULong(frame, 0);
            var p = isHost ? Find(sender) : (sender == HostId ? Find(HostId) : null);
            if (p == null)
                continue;
            if (isHost)
                p.Udp = receiveFrom;
            Buffer.BlockCopy(frame, UdpHeader, buffer, 0, size - UdpHeader);
            peer = sender;
            channel = (Channel)frame[8];
            length = size - UdpHeader;
            return true;
        }

        for (var i = peers.Count - 1; i >= 0; i--)
        {
            var p = peers[i];
            if (!Pump(p))
            {
                Lose(p);
                continue;
            }
            while (PopFrame(p, buffer, out var ch, out length))
            {
                if (ch == IdentChannel)
                {
                    if (isHost && length == 8 && p.Id == 0)
                        p.Id = ReadULong(buffer, 0);
                    continue;
                }
                if (p.Id == 0)
                    continue;
                peer = p.Id;
                channel = (Channel)ch;
                return true;
            }
        }

        peer = 0;
        channel = 0;
        length = 0;
        return false;
    }

    public void Close(ulong peerId)
    {
        var p = Find(peerId);
        if (p == null)
            return;
        p.Tcp.Close();
        peers.Remove(p);
    }

    public void Update(double now)
    {
        if (isHost)
        {
            while (listener.Poll(0, SelectMode.SelectRead))
            {
                var socket = listener.Accept();
                socket.NoDelay = true;
                peers.Add(new Peer { Tcp = socket });
            }
            return;
        }

        if (pendingConnect == null || !pendingConnect.IsCompleted)
            return;
        try
        {
            connecting.EndConnect(pendingConnect);
            var host = new Peer { Id = HostId, Tcp = connecting };
            peers.Add(host);
            var ident = new byte[8];
            WriteULong(ident, 0, LocalId);
            SendFrame(host, IdentChannel, ident, 8);
        }
        catch (SocketException)
        {
            connecting.Close();
        }
        pendingConnect = null;
        connecting = null;
    }

    public void Dispose()
    {
        foreach (var p in peers)
            p.Tcp.Close();
        peers.Clear();
        connecting?.Close();
        listener?.Close();
        udp.Close();
    }

    bool SendFrame(Peer peer, byte channel, byte[] data, int length)
    {
        frame[0] = (byte)length;
        frame[1] = (byte)(length >> 8);
        frame[2] = channel;
        Buffer.BlockCopy(data, 0, frame, TcpHeader, length);
        try
        {
            peer.Tcp.Send(frame, length + TcpHeader, SocketFlags.None);
            return true;
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        Lose(peer);
        return false;
    }

    bool Pump(Peer p)
    {
        try
        {
            var available = p.Tcp.Available;
            if (available == 0)
            {
                // Readable with nothing to read means closed, but data may have arrived between the two calls.
                if (!p.Tcp.Poll(0, SelectMode.SelectRead))
                    return true;
                available = p.Tcp.Available;
                if (available == 0)
                    return false;
            }
            var space = InboxSize - p.InboxCount;
            if (space == 0)
                return false;
            p.InboxCount += p.Tcp.Receive(p.Inbox, p.InboxCount, Math.Min(available, space), SocketFlags.None);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    static bool PopFrame(Peer p, byte[] buffer, out byte channel, out int length)
    {
        channel = 0;
        length = 0;
        if (p.InboxCount < TcpHeader)
            return false;
        var size = p.Inbox[0] | p.Inbox[1] << 8;
        if (p.InboxCount < TcpHeader + size)
            return false;
        channel = p.Inbox[2];
        length = Math.Min(size, buffer.Length);
        Buffer.BlockCopy(p.Inbox, TcpHeader, buffer, 0, length);
        p.InboxCount -= TcpHeader + size;
        Buffer.BlockCopy(p.Inbox, TcpHeader + size, p.Inbox, 0, p.InboxCount);
        return true;
    }

    void Lose(Peer p)
    {
        p.Tcp.Close();
        peers.Remove(p);
        if (p.Id != 0)
            PeerLost?.Invoke(p.Id);
    }

    Peer Find(ulong id)
    {
        for (var i = 0; i < peers.Count; i++)
        {
            if (peers[i].Id == id)
                return peers[i];
        }
        return null;
    }

    static void WriteULong(byte[] b, int offset, ulong v)
    {
        for (var i = 0; i < 8; i++)
            b[offset + i] = (byte)(v >> (8 * i));
    }

    static ulong ReadULong(byte[] b, int offset)
    {
        ulong v = 0;
        for (var i = 0; i < 8; i++)
            v |= (ulong)b[offset + i] << (8 * i);
        return v;
    }
}
