using System;
using System.Diagnostics;
using System.Net;
using System.Text;
using MSCCoop.Net;
using MSCLoader;
using Steamworks;
using UnityEngine;

namespace MSCCoop;

sealed class NetRunner : MonoBehaviour
{
    const float StatusInterval = 0.25f;

    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly StringBuilder status = new();
    string modVersion;
    uint gameBuild;
    string playerName = "Player";
    SteamLobby lobby;
    SteamTransport steam;
    LanTransport lan;
    SimulatedTransport sim;
    Session session;
    GameFlow flow;
    readonly PlayerSync players = new();
    readonly ObjectSync objects = new();
    double simLatency, simJitter, simLoss;
    string statusText = "";
    float nextStatus;
    bool overlay = true;

    double Now => clock.Elapsed.TotalSeconds;

    public bool Active => session != null && session.State != SessionState.Idle;

    public void Init(string version, string clientSaveDir)
    {
        modVersion = version;
        flow = new GameFlow(clientSaveDir);
        flow.OnLevelLoaded();
        gameBuild = ComputeGameBuild();
        try
        {
            playerName = SteamFriends.GetPersonaName();
            lobby = new SteamLobby(modVersion, gameBuild);
            lobby.Hosted += () => StartSession(steam, host: true, 0);
            lobby.JoinedAsClient += OnJoinedLobby;
            lobby.Failed += message => ModConsole.Error("Co-op: " + message);
            lobby.MemberLeft += OnLobbyMemberLeft;
            steam = new SteamTransport(lobby.IsMember);
        }
        catch (Exception e)
        {
            ModConsole.Warning("Co-op: Steam is not available, only LAN sessions will work. " + e.Message);
        }

        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "+connect_lobby" && ulong.TryParse(args[i + 1], out var id))
                JoinSteam(id);
            else if (args[i] == "-coop-lan-join" && IPAddress.TryParse(args[i + 1], out var address))
                JoinLan(address, LanTransport.DefaultPort);
        }
        if (Array.IndexOf(args, "-coop-lan-host") >= 0)
            HostLan(LanTransport.DefaultPort);
    }

    public void HostSteam()
    {
        if (!RequireSteam() || !RequireIdle())
            return;
        lobby.Create();
    }

    public void JoinSteam(ulong lobbyId)
    {
        if (!RequireSteam() || !RequireIdle() || !flow.CanJoin())
            return;
        lobby.Join(lobbyId);
    }

    public void Invite()
    {
        if (RequireSteam())
            lobby.Invite();
    }

    public void HostLan(int port)
    {
        if (!RequireIdle())
            return;
        lan?.Dispose();
        lan = LanTransport.Listen(port);
        StartSession(lan, host: true, 0);
    }

    public void JoinLan(IPAddress address, int port)
    {
        if (!RequireIdle() || !flow.CanJoin())
            return;
        lan?.Dispose();
        lan = LanTransport.Connect(address, port);
        StartSession(lan, host: false, LanTransport.HostId);
    }

    public void Leave()
    {
        session?.Leave();
        lobby?.Leave();
    }

    public void SetSimulation(double latencyMs, double jitterMs, double lossPercent)
    {
        simLatency = latencyMs / 1000;
        simJitter = jitterMs / 1000;
        simLoss = lossPercent / 100;
        ApplySimulation();
    }

    public void ToggleOverlay() => overlay = !overlay;

    void StartSession(ITransport transport, bool host, ulong hostId)
    {
        sim = new SimulatedTransport(transport, Environment.TickCount);
        ApplySimulation();
        var config = new SessionConfig { ModVersion = modVersion, GameBuild = gameBuild, PlayerName = playerName };
        session = new Session(sim, config);
        flow.Attach(session, config);
        players.Attach(session);
        objects.Attach(session);
        session.PlayerJoined += p => ModConsole.Print("Co-op: " + p.Name + " joined");
        session.PlayerLeft += p => ModConsole.Print("Co-op: " + p.Name + " left");
        session.Ended += OnEnded;
        if (host)
            session.Host(Now);
        else
            session.Connect(hostId, Now);
        ModConsole.Print(host ? "Co-op: hosting" : "Co-op: connecting");
    }

    void OnJoinedLobby(ulong owner)
    {
        if (flow.CanJoin())
            StartSession(steam, host: false, owner);
        else
            lobby.Leave();
    }

    void OnEnded(EndReason reason)
    {
        flow.Detach();
        players.Detach();
        objects.Detach();
        var text = reason == EndReason.Rejected ? "rejected (" + session.LastRejectReason + ")" : reason.ToString();
        ModConsole.Print("Co-op: session ended, " + text);
        lobby?.Leave();
    }

    void OnLobbyMemberLeft(ulong user)
    {
        if (session == null || !session.IsHost)
            return;
        var connections = session.Connections;
        for (var i = 0; i < connections.Count; i++)
        {
            if (connections[i].Id == user)
                connections[i].LastReceived = double.NegativeInfinity;
        }
    }

    void ApplySimulation()
    {
        if (sim == null)
            return;
        sim.Latency = simLatency;
        sim.Jitter = simJitter;
        sim.Loss = simLoss;
    }

    bool RequireSteam()
    {
        if (steam != null)
            return true;
        ModConsole.Error("Co-op: Steam is not available.");
        return false;
    }

    bool RequireIdle()
    {
        if (!Active)
            return true;
        ModConsole.Error("Co-op: already in a session, use 'coop leave' first.");
        return false;
    }

    void Update()
    {
        steam?.Update(Now);
        session?.Update(Now);
        flow.Update();
        players.Update(Time.deltaTime);
        objects.Update();
        if (overlay && Time.unscaledTime >= nextStatus)
        {
            nextStatus = Time.unscaledTime + StatusInterval;
            statusText = BuildStatus();
        }
    }

    public void OnGameReady()
    {
        objects.OnGameReady();
        flow.OnGameReady();
        players.OnGameReady();
    }

    void FixedUpdate() => objects.FixedUpdate();

    void OnLevelWasLoaded(int level)
    {
        flow.OnLevelLoaded();
        players.OnLevelLoaded();
        objects.OnLevelLoaded();
    }

    void OnGUI()
    {
        if (overlay && Active)
            GUI.Label(new Rect(10, 10, 420, 160), statusText);
    }

    string BuildStatus()
    {
        if (!Active)
            return "";
        status.Length = 0;
        status.Append("MSC Co-op ").Append(session.IsHost ? "host" : "client").Append(" | ").Append(session.State).AppendLine();
        var players = session.Players;
        for (var i = 0; i < players.Length; i++)
        {
            if (players[i] == null)
                continue;
            status.Append(i).Append("  ").Append(players[i].Name);
            var c = FindConnection(players[i].Id);
            if (c != null)
            {
                status.Append("  rtt ").Append((int)(c.Clock.Rtt * 1000)).Append(" ms");
                status.Append("  in ").Append(c.Stats.KbpsIn.ToString("0.0")).Append(" out ").Append(c.Stats.KbpsOut.ToString("0.0")).Append(" kbps");
            }
            status.AppendLine();
        }
        if (sim != null && sim.Enabled)
            status.Append("sim ").Append((int)(simLatency * 1000)).Append(" ms +").Append((int)(simJitter * 1000)).Append(" ms ").Append((int)(simLoss * 100)).Append("% loss");
        return status.ToString();
    }

    Connection FindConnection(ulong id)
    {
        var connections = session.Connections;
        for (var i = 0; i < connections.Count; i++)
        {
            if (connections[i].Id == id)
                return connections[i];
        }
        return null;
    }

    // Steam's build id is missing when the game runs outside Steam. The game assembly's MVID changes with every game update.
    static uint ComputeGameBuild()
    {
        var mvid = typeof(CarDynamics).Assembly.ManifestModule.ModuleVersionId.ToByteArray();
        return Fnv.Hash32(mvid, 0, mvid.Length);
    }

    void OnApplicationQuit() => Leave();

    // MSCLoader destroys every object, DontDestroyOnLoad ones included, when the game returns to the main menu.
    void OnDestroy()
    {
        Leave();
        lobby?.Dispose();
        steam?.Dispose();
        lan?.Dispose();
    }
}
