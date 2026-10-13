using System;
using System.Collections.Generic;
using System.Linq;
using MSCCoop.Net;

namespace MSCCoop.Net.Tests;

public class SessionTests
{
    const ulong HostId = 100;

    readonly MemoryHub hub = new();
    double now;

    static SessionConfig Config(string name, string version = "0.1.0", uint build = 1) =>
        new() { PlayerName = name, ModVersion = version, GameBuild = build };

    Session CreateHost(ITransport? transport = null)
    {
        var host = new Session(transport ?? hub.Create(HostId), Config("host"));
        host.Host(now);
        return host;
    }

    Session CreateClient(ulong id, SessionConfig? config = null, ITransport? transport = null)
    {
        var client = new Session(transport ?? hub.Create(id), config ?? Config("client" + id));
        client.Connect(HostId, now);
        return client;
    }

    void Run(double seconds, params Session[] sessions)
    {
        for (var t = 0.0; t < seconds; t += 0.02)
        {
            now += 0.02;
            foreach (var s in sessions)
                s.Update(now);
        }
    }

    [Fact]
    public void ClientsJoinAndEveryoneSeesAllPlayers()
    {
        var host = CreateHost();
        var a = CreateClient(1);
        var b = CreateClient(2);
        Run(1, host, a, b);

        Assert.Equal(SessionState.Connected, a.State);
        Assert.Equal(SessionState.Connected, b.State);
        Assert.NotEqual(a.LocalSlot, b.LocalSlot);
        foreach (var s in new[] { host, a, b })
            Assert.Equal(new[] { "host", "client1", "client2" }, s.Players.Where(p => p != null).Select(p => p.Name));
    }

    [Fact]
    public void FifthPlayerIsRejected()
    {
        var host = CreateHost();
        var clients = Enumerable.Range(1, 4).Select(i => CreateClient((ulong)i)).ToArray();
        Run(1, clients.Prepend(host).ToArray());

        Assert.Equal(3, clients.Count(c => c.State == SessionState.Connected));
        var rejected = clients.Single(c => c.State == SessionState.Idle);
        Assert.Equal(RejectReason.Full, rejected.LastRejectReason);
    }

    [Theory]
    [InlineData("0.2.0", 1u, RejectReason.ModVersionMismatch)]
    [InlineData("0.1.0", 2u, RejectReason.GameBuildMismatch)]
    public void MismatchedVersionsAreRejected(string version, uint build, RejectReason expected)
    {
        var host = CreateHost();
        var client = CreateClient(1, Config("c", version, build));
        EndReason? ended = null;
        client.Ended += r => ended = r;
        Run(1, host, client);

        Assert.Equal(EndReason.Rejected, ended);
        Assert.Equal(expected, client.LastRejectReason);
        Assert.Empty(host.Connections);
    }

    [Fact]
    public void LeavingClientIsRemovedEverywhere()
    {
        var host = CreateHost();
        var a = CreateClient(1);
        var b = CreateClient(2);
        Run(1, host, a, b);

        var left = new List<string>();
        b.PlayerLeft += p => left.Add(p.Name);
        a.Leave();
        Run(0.5, host, b);

        Assert.Single(host.Connections);
        Assert.Equal(new[] { "client1" }, left);
        Assert.Null(b.Players[1]);
    }

    [Fact]
    public void HostLeavingEndsClientSession()
    {
        var host = CreateHost();
        var a = CreateClient(1);
        Run(1, host, a);

        EndReason? ended = null;
        a.Ended += r => ended = r;
        host.Leave();
        Run(0.5, a);

        Assert.Equal(EndReason.HostLeft, ended);
        Assert.Equal(SessionState.Idle, a.State);
    }

    [Fact]
    public void SilentPeerTimesOut()
    {
        var host = CreateHost();
        var a = CreateClient(1);
        Run(1, host, a);

        hub.Disconnect(1);
        Run(16, host);
        Assert.Empty(host.Connections);
        Assert.Null(host.Players[1]);
    }

    [Fact]
    public void UnreachableHostFailsToConnect()
    {
        var a = CreateClient(1);
        EndReason? ended = null;
        a.Ended += r => ended = r;
        Run(11, a);
        Assert.Equal(EndReason.ConnectFailed, ended);
    }

    [Fact]
    public void PingMeasuresSimulatedLatency()
    {
        var host = CreateHost(new SimulatedTransport(hub.Create(HostId), 2) { Latency = 0.05, Jitter = 0.02, Loss = 0.05 });
        var a = CreateClient(1, transport: new SimulatedTransport(hub.Create(1), 3) { Latency = 0.05, Jitter = 0.02, Loss = 0.05 });
        Run(10, host, a);

        var clock = a.HostConnection!.Clock;
        Assert.True(clock.HasSample);
        Assert.InRange(clock.Rtt, 0.09, 0.17);
        Assert.InRange(clock.Offset, -0.03, 0.03);
    }

    [Fact]
    public void GameMessagesReachHandler()
    {
        var host = CreateHost();
        var a = CreateClient(1);
        Run(1, host, a);

        uint received = 0;
        host.MessageReceived += (from, type, reader) =>
        {
            if (type == MessageType.FirstGameMessage)
                received = reader.ReadUInt();
        };
        a.BeginMessage(MessageType.FirstGameMessage).WriteUInt(0xC0FFEE);
        a.Send(a.HostConnection!, Channel.Control);
        Run(0.1, host, a);
        Assert.Equal(0xC0FFEEu, received);
    }

    [Fact]
    public void GarbagePacketsAreIgnored()
    {
        var host = CreateHost();
        var random = new Random(9);
        var junk = new byte[64];
        for (var i = 0; i < 1000; i++)
        {
            random.NextBytes(junk);
            hub.Deliver(999, HostId, Channel.Control, junk, random.Next(0, 64));
        }
        Run(0.1, host);
        Assert.Empty(host.Connections);
    }
}
