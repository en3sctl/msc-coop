using System;
using System.Collections.Generic;

namespace MSCCoop.Net;

public enum SessionState
{
    Idle,
    Connecting,
    Connected,
}

public enum EndReason
{
    Left,
    HostLeft,
    TimedOut,
    ConnectFailed,
    Rejected,
}

public sealed class SessionConfig
{
    public string ModVersion = "";
    public uint GameBuild;
    public string PlayerName = "";
    public double Timeout = 15;
    public double LoadingTimeout = 120;
    public double ConnectTimeout = 10;
    public double HelloInterval = 1;
    public double PingInterval = 0.5;
    public bool AcceptingJoins = true;
}

public sealed class PlayerInfo(byte slot, ulong id, string name)
{
    public readonly byte Slot = slot;
    public readonly ulong Id = id;
    public readonly string Name = name;
}

public sealed class Connection(ulong id)
{
    public readonly ulong Id = id;
    public readonly ClockSync Clock = new();
    public readonly TrafficStats Stats = new();
    public byte Slot;
    public bool Established;
    public double LastReceived;

    // Level loads block the main thread, so a peer that is loading goes silent for a long time.
    public bool Loading;
    internal double NextPing;
}

public sealed class Session
{
    readonly ITransport transport;
    readonly SessionConfig config;
    readonly byte[] sendBuffer = new byte[Protocol.MaxReliableBytes];
    readonly byte[] receiveBuffer = new byte[Protocol.MaxReliableBytes];
    readonly BitWriter writer;
    readonly BitReader reader;
    readonly List<Connection> connections = new();
    readonly PlayerInfo[] players = new PlayerInfo[Protocol.MaxPlayers];
    double now;
    double connectStarted;
    double nextHello;

    public Session(ITransport transport, SessionConfig config)
    {
        this.transport = transport;
        this.config = config;
        writer = new BitWriter(sendBuffer);
        reader = new BitReader(receiveBuffer, 0);
        transport.PeerLost += OnPeerLost;
    }

    public delegate void MessageHandler(Connection from, MessageType type, BitReader reader);

    public event Action<PlayerInfo> PlayerJoined;
    public event Action<PlayerInfo> PlayerLeft;
    public event Action<EndReason> Ended;
    public event MessageHandler MessageReceived;

    const int MaxHeldMessages = 20000;

    struct HeldMessage
    {
        public Connection From;
        public MessageType Type;
        public byte[] Data;
        public int Length;
        public int BitPosition;
    }

    readonly List<HeldMessage> held = new();
    bool holding;

    // While a level loads nothing can apply game messages, so they are kept and delivered in order
    // once everything is ready instead of being dropped as unknown.
    public bool HoldGameMessages
    {
        get => holding;
        set
        {
            holding = value;
            if (!value)
                ReleaseHeld();
        }
    }

    public SessionState State { get; private set; }
    public bool IsHost { get; private set; }
    public byte LocalSlot { get; private set; }
    public RejectReason LastRejectReason { get; private set; }
    public IList<Connection> Connections => connections;
    public PlayerInfo[] Players => players;
    public double Now => now;

    // Shared timeline for everything stamped by different machines: the host's clock.
    public double HostTime => IsHost || connections.Count == 0 ? now : now + connections[0].Clock.Offset;

    public void Host(double time)
    {
        Reset();
        now = time;
        IsHost = true;
        LocalSlot = 0;
        players[0] = new PlayerInfo(0, transport.LocalId, config.PlayerName);
        State = SessionState.Connected;
    }

    public void Connect(ulong hostId, double time)
    {
        Reset();
        now = time;
        IsHost = false;
        connections.Add(new Connection(hostId) { LastReceived = time });
        connectStarted = time;
        nextHello = time;
        State = SessionState.Connecting;
    }

    public void Leave()
    {
        if (State == SessionState.Idle)
            return;
        BeginMessage(MessageType.Disconnect);
        Broadcast(Channel.Control);
        End(EndReason.Left);
    }

    public void Update(double time)
    {
        now = time;
        transport.Update(time);
        while (State != SessionState.Idle && transport.Receive(receiveBuffer, out var peer, out var channel, out var length))
            HandlePacket(peer, channel, length);

        if (State == SessionState.Connecting)
        {
            if (now - connectStarted > config.ConnectTimeout)
            {
                End(EndReason.ConnectFailed);
                return;
            }
            if (now >= nextHello)
            {
                SendHello();
                nextHello = now + config.HelloInterval;
            }
        }

        for (var i = connections.Count - 1; i >= 0; i--)
        {
            var c = connections[i];
            if (!c.Established)
                continue;
            if (now - c.LastReceived > (c.Loading ? config.LoadingTimeout : config.Timeout))
            {
                Drop(c, EndReason.TimedOut);
                continue;
            }
            if (now >= c.NextPing)
            {
                c.NextPing = now + config.PingInterval;
                BeginMessage(MessageType.Ping).WriteUInt(TimeMs(now));
                Send(c, Channel.State);
            }
            c.Stats.Update(now);
        }
    }

    public BitWriter BeginMessage(MessageType type)
    {
        writer.Reset();
        writer.Write(Protocol.Magic, 16);
        writer.Write(Protocol.Version, 8);
        writer.WriteByte((byte)type);
        return writer;
    }

    public bool Send(Connection c, Channel channel)
    {
        var limit = channel == Channel.State ? Protocol.MaxUnreliableBytes : Protocol.MaxReliableBytes;
        if (writer.Overflow || writer.Length > limit)
            return false;
        c.Stats.Sent(writer.Length);
        return transport.Send(c.Id, channel, sendBuffer, writer.Length);
    }

    public void Broadcast(Channel channel, Connection except = null)
    {
        for (var i = 0; i < connections.Count; i++)
        {
            var c = connections[i];
            if (c != except && (c.Established || channel == Channel.Control))
                Send(c, channel);
        }
    }

    public Connection HostConnection => IsHost || connections.Count == 0 ? null : connections[0];

    void HandlePacket(ulong peer, Channel channel, int length)
    {
        reader.Reset(receiveBuffer, length);
        if (reader.Read(16) != Protocol.Magic)
            return;
        var version = reader.Read(8);
        var type = (MessageType)reader.ReadByte();
        if (reader.Overflow)
            return;

        var c = Find(peer);
        if (version != Protocol.Version)
        {
            if (IsHost && type == MessageType.Hello)
                SendReject(peer, RejectReason.ProtocolMismatch);
            else if (!IsHost && c != null && type == MessageType.Reject)
                HandleReject();
            return;
        }

        if (c == null)
        {
            if (IsHost && type == MessageType.Hello)
                HandleHello(peer);
            return;
        }

        c.LastReceived = now;
        c.Stats.Received(length);

        switch (type)
        {
            case MessageType.Hello when IsHost:
                HandleHello(peer);
                break;
            case MessageType.Welcome when !IsHost:
                HandleWelcome(c);
                break;
            case MessageType.Reject when !IsHost:
                HandleReject();
                break;
            case MessageType.Disconnect:
                Drop(c, EndReason.HostLeft);
                break;
            case MessageType.Ping:
                var sent = reader.ReadUInt();
                BeginMessage(MessageType.Pong);
                writer.WriteUInt(sent);
                writer.WriteUInt(TimeMs(now));
                Send(c, Channel.State);
                break;
            case MessageType.Pong:
                var original = reader.ReadUInt() / 1000.0;
                var remote = reader.ReadUInt() / 1000.0;
                if (!reader.Overflow)
                    c.Clock.AddSample(original, remote, now);
                break;
            case MessageType.PlayerJoined when !IsHost:
                var info = ReadPlayer();
                if (!reader.Overflow && info.Slot < Protocol.MaxPlayers)
                {
                    players[info.Slot] = info;
                    PlayerJoined?.Invoke(info);
                }
                break;
            case MessageType.PlayerLeft when !IsHost:
                RemovePlayer(reader.ReadByte());
                break;
            default:
                if (type < MessageType.FirstGameMessage || !c.Established)
                    break;
                if (holding)
                {
                    if (held.Count < MaxHeldMessages)
                    {
                        var copy = new byte[length];
                        Buffer.BlockCopy(receiveBuffer, 0, copy, 0, length);
                        held.Add(new HeldMessage { From = c, Type = type, Data = copy, Length = length, BitPosition = reader.BitPosition });
                    }
                    break;
                }
                MessageReceived?.Invoke(c, type, reader);
                break;
        }
    }

    void SendHello()
    {
        BeginMessage(MessageType.Hello);
        writer.WriteString(config.ModVersion, Protocol.MaxVersionChars);
        writer.WriteUInt(config.GameBuild);
        writer.WriteString(config.PlayerName, Protocol.MaxNameChars);
        Send(connections[0], Channel.Control);
    }

    void HandleHello(ulong peer)
    {
        var modVersion = reader.ReadString(Protocol.MaxVersionChars);
        var gameBuild = reader.ReadUInt();
        var name = reader.ReadString(Protocol.MaxNameChars);
        if (reader.Overflow)
            return;

        if (modVersion != config.ModVersion)
        {
            SendReject(peer, RejectReason.ModVersionMismatch);
            return;
        }
        if (gameBuild != config.GameBuild)
        {
            SendReject(peer, RejectReason.GameBuildMismatch);
            return;
        }

        var c = Find(peer);
        if (c == null)
        {
            if (!config.AcceptingJoins)
            {
                SendReject(peer, RejectReason.InProgress);
                return;
            }
            var slot = FreeSlot();
            if (slot < 0)
            {
                SendReject(peer, RejectReason.Full);
                return;
            }
            c = new Connection(peer) { Slot = (byte)slot, Established = true, LastReceived = now, NextPing = now };
            connections.Add(c);
            var info = new PlayerInfo((byte)slot, peer, name);
            players[slot] = info;

            BeginMessage(MessageType.PlayerJoined);
            WritePlayer(info);
            Broadcast(Channel.Control, c);
            PlayerJoined?.Invoke(info);
        }

        BeginMessage(MessageType.Welcome);
        writer.WriteByte(c.Slot);
        var count = 0;
        for (var i = 0; i < players.Length; i++)
        {
            if (players[i] != null)
                count++;
        }
        writer.WriteByte((byte)count);
        for (var i = 0; i < players.Length; i++)
        {
            if (players[i] != null)
                WritePlayer(players[i]);
        }
        Send(c, Channel.Control);
    }

    void HandleWelcome(Connection host)
    {
        if (State != SessionState.Connecting)
            return;
        var slot = reader.ReadByte();
        var count = reader.ReadByte();
        var received = new PlayerInfo[Protocol.MaxPlayers];
        for (var i = 0; i < count; i++)
        {
            var info = ReadPlayer();
            if (info.Slot < Protocol.MaxPlayers)
                received[info.Slot] = info;
        }
        if (reader.Overflow || slot >= Protocol.MaxPlayers || slot == 0)
            return;

        LocalSlot = slot;
        host.Slot = 0;
        host.Established = true;
        host.NextPing = now;
        State = SessionState.Connected;
        for (var i = 0; i < Protocol.MaxPlayers; i++)
        {
            players[i] = received[i];
            if (received[i] != null && i != slot)
                PlayerJoined?.Invoke(received[i]);
        }
    }

    void HandleReject()
    {
        LastRejectReason = (RejectReason)reader.ReadByte();
        End(EndReason.Rejected);
    }

    void SendReject(ulong peer, RejectReason reason)
    {
        BeginMessage(MessageType.Reject).WriteByte((byte)reason);
        transport.Send(peer, Channel.Control, sendBuffer, writer.Length);
    }

    void OnPeerLost(ulong peer)
    {
        var c = Find(peer);
        if (c != null)
            Drop(c, EndReason.TimedOut);
    }

    void Drop(Connection c, EndReason reasonIfHost)
    {
        if (!IsHost)
        {
            End(reasonIfHost == EndReason.HostLeft ? EndReason.HostLeft : EndReason.TimedOut);
            return;
        }

        connections.Remove(c);
        transport.Close(c.Id);
        if (!c.Established)
            return;
        RemovePlayer(c.Slot);
        BeginMessage(MessageType.PlayerLeft).WriteByte(c.Slot);
        Broadcast(Channel.Control);
    }

    void RemovePlayer(byte slot)
    {
        if (slot >= Protocol.MaxPlayers || players[slot] == null)
            return;
        var info = players[slot];
        players[slot] = null;
        PlayerLeft?.Invoke(info);
    }

    void End(EndReason reason)
    {
        if (State == SessionState.Idle)
            return;
        for (var i = 0; i < connections.Count; i++)
            transport.Close(connections[i].Id);
        Reset();
        Ended?.Invoke(reason);
    }

    void ReleaseHeld()
    {
        for (var i = 0; i < held.Count && State != SessionState.Idle; i++)
        {
            var m = held[i];
            if (!connections.Contains(m.From))
                continue;
            var r = new BitReader(m.Data, m.Length);
            r.Skip(m.BitPosition);
            MessageReceived?.Invoke(m.From, m.Type, r);
        }
        held.Clear();
    }

    void Reset()
    {
        held.Clear();
        holding = false;
        connections.Clear();
        Array.Clear(players, 0, players.Length);
        State = SessionState.Idle;
        IsHost = false;
        LocalSlot = 0;
    }

    int FreeSlot()
    {
        for (var i = 1; i < players.Length; i++)
        {
            if (players[i] == null)
                return i;
        }
        return -1;
    }

    Connection Find(ulong peer)
    {
        for (var i = 0; i < connections.Count; i++)
        {
            if (connections[i].Id == peer)
                return connections[i];
        }
        return null;
    }

    void WritePlayer(PlayerInfo info)
    {
        writer.WriteByte(info.Slot);
        writer.WriteULong(info.Id);
        writer.WriteString(info.Name, Protocol.MaxNameChars);
    }

    PlayerInfo ReadPlayer() => new(reader.ReadByte(), reader.ReadULong(), reader.ReadString(Protocol.MaxNameChars));

    static uint TimeMs(double time) => (uint)(long)(time * 1000);
}
