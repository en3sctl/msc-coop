using System;
using System.Collections.Generic;
using System.Linq;
using MSCCoop.Net;

namespace MSCCoop.Net.Tests;

public class SaveTransferTests
{
    const ulong HostId = 100;
    readonly MemoryHub hub = new();
    double now;

    (Session host, Session client) Connect()
    {
        var host = new Session(hub.Create(HostId), new SessionConfig { PlayerName = "host" });
        host.Host(now);
        var client = new Session(hub.Create(1), new SessionConfig { PlayerName = "client" });
        client.Connect(HostId, now);
        Run(host, client);
        return (host, client);
    }

    void Run(params Session[] sessions)
    {
        for (var i = 0; i < 50; i++)
        {
            now += 0.02;
            foreach (var s in sessions)
                s.Update(now);
        }
    }

    static List<SaveFile> SampleFiles()
    {
        var random = new Random(11);
        var big = new byte[64245];
        random.NextBytes(big);
        return new List<SaveFile>
        {
            new("defaultES2File.txt", big),
            new("items.txt", new byte[] { 1, 2, 3 }),
            new("graveyard.txt", Array.Empty<byte>()),
            new("trophies.txt", new byte[SaveTransfer.ChunkBytes]),
        };
    }

    [Fact]
    public void FilesArriveIntactAcrossChunks()
    {
        var (host, client) = Connect();
        var receiver = new SaveReceiver();
        List<SaveFile>? result = null;
        var ok = false;
        client.MessageReceived += (from, type, r) =>
        {
            if (type == MessageType.SaveChunk)
                Assert.True(receiver.HandleChunk(r));
            else if (type == MessageType.SaveComplete)
                ok = receiver.Complete(r, out result);
        };

        var files = SampleFiles();
        SaveTransfer.Send(host, host.Connections[0], files);
        Run(host, client);

        Assert.True(ok);
        foreach (var expected in files)
            Assert.Equal(expected.Data, result!.Single(f => f.Name == expected.Name).Data);
    }

    [Fact]
    public void RepeatedTransferStartsOver()
    {
        var (host, client) = Connect();
        var receiver = new SaveReceiver();
        var completed = 0;
        client.MessageReceived += (from, type, r) =>
        {
            if (type == MessageType.SaveBegin)
                receiver.Reset();
            else if (type == MessageType.SaveChunk)
                Assert.True(receiver.HandleChunk(r));
            else if (type == MessageType.SaveComplete && receiver.Complete(r, out _))
                completed++;
        };

        SaveTransfer.Send(host, host.Connections[0], SampleFiles());
        SaveTransfer.Send(host, host.Connections[0], SampleFiles());
        Run(host, client);
        Assert.Equal(2, completed);
    }

    [Fact]
    public void DuplicateChunkIsRefused()
    {
        var buffer = new byte[256];
        var w = new BitWriter(buffer);
        w.WriteString("items.txt", SaveTransfer.MaxNameChars);
        w.WriteUInt(3);
        w.WriteUInt(0);
        w.WriteUShort(3);
        w.WriteBytes(new byte[] { 1, 2, 3 }, 0, 3);
        var receiver = new SaveReceiver();
        Assert.True(receiver.HandleChunk(new BitReader(buffer, w.Length)));
        Assert.False(receiver.HandleChunk(new BitReader(buffer, w.Length)));
    }

    [Fact]
    public void LoadingPeerGetsLongerTimeout()
    {
        var (host, client) = Connect();
        host.Connections[0].Loading = true;
        for (var i = 0; i < 60 * 50; i++)
        {
            now += 0.02;
            host.Update(now);
        }
        Assert.Single(host.Connections);

        host.Connections[0].Loading = false;
        Run(host);
        Assert.Empty(host.Connections);
    }

    [Fact]
    public void MissingChunkFailsVerification()
    {
        var (host, client) = Connect();
        var receiver = new SaveReceiver();
        var ok = true;
        var chunks = 0;
        client.MessageReceived += (from, type, r) =>
        {
            if (type == MessageType.SaveChunk && chunks++ != 2)
                receiver.HandleChunk(r);
            else if (type == MessageType.SaveComplete)
                ok = receiver.Complete(r, out _);
        };

        SaveTransfer.Send(host, host.Connections[0], SampleFiles());
        Run(host, client);
        Assert.False(ok);
    }

    [Fact]
    public void OversizedFileIsRefused()
    {
        var buffer = new byte[256];
        var w = new BitWriter(buffer);
        w.WriteString("x.txt", SaveTransfer.MaxNameChars);
        w.WriteUInt(SaveTransfer.MaxFileBytes + 1u);
        w.WriteUInt(0);
        w.WriteUShort(0);
        Assert.False(new SaveReceiver().HandleChunk(new BitReader(buffer, w.Length)));
    }

    [Theory]
    [InlineData("defaultES2File.txt", true)]
    [InlineData("Mods.txt", true)]
    [InlineData("../evil.txt", false)]
    [InlineData("..\\evil.txt", false)]
    [InlineData("C:\\Windows\\evil.txt", false)]
    [InlineData("sub/items.txt", false)]
    [InlineData("items.txt.exe", false)]
    [InlineData(".txt", false)]
    [InlineData("a..txt", false)]
    public void OnlyPlainSaveNamesAreAccepted(string name, bool safe) =>
        Assert.Equal(safe, SaveTransfer.IsSafeName(name));

    [Fact]
    public void HostInGameRejectsNewPlayers()
    {
        var config = new SessionConfig { PlayerName = "host" };
        var host = new Session(hub.Create(HostId), config);
        host.Host(now);
        config.AcceptingJoins = false;
        var client = new Session(hub.Create(1), new SessionConfig { PlayerName = "late" });
        client.Connect(HostId, now);
        Run(host, client);

        Assert.Equal(SessionState.Idle, client.State);
        Assert.Equal(RejectReason.InProgress, client.LastRejectReason);
    }
}
