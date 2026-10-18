using System;
using MSCCoop.Net;
using Steamworks;

namespace MSCCoop;

sealed class SteamLobby
{
    const string VersionKey = "mscc_version";
    const string BuildKey = "mscc_build";
    const string ProtocolKey = "mscc_protocol";

    readonly string modVersion;
    readonly uint gameBuild;
    readonly CallResult<LobbyCreated_t> created;
    readonly Callback<LobbyEnter_t> entered;
    readonly Callback<GameLobbyJoinRequested_t> joinRequested;
    readonly Callback<LobbyChatUpdate_t> memberChanged;

    public SteamLobby(string modVersion, uint gameBuild)
    {
        this.modVersion = modVersion;
        this.gameBuild = gameBuild;
        created = CallResult<LobbyCreated_t>.Create(OnCreated);
        entered = Callback<LobbyEnter_t>.Create(OnEntered);
        joinRequested = Callback<GameLobbyJoinRequested_t>.Create(r => Join(r.m_steamIDLobby.m_SteamID));
        memberChanged = Callback<LobbyChatUpdate_t>.Create(OnMemberChanged);
    }

    public event Action Hosted;
    public event Action<ulong> JoinedAsClient;
    public event Action<string> Failed;
    public event Action<ulong> MemberLeft;

    public ulong Current { get; private set; }

    public bool IsMember(ulong user)
    {
        if (Current == 0)
            return false;
        var lobby = new CSteamID(Current);
        var count = SteamMatchmaking.GetNumLobbyMembers(lobby);
        for (var i = 0; i < count; i++)
        {
            if (SteamMatchmaking.GetLobbyMemberByIndex(lobby, i).m_SteamID == user)
                return true;
        }
        return false;
    }

    public void Create() => created.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, Protocol.MaxPlayers));

    public void Join(ulong lobby)
    {
        Leave();
        SteamMatchmaking.JoinLobby(new CSteamID(lobby));
    }

    public void Invite()
    {
        if (Current != 0)
            SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(Current));
    }

    public void Leave()
    {
        if (Current == 0)
            return;
        SteamMatchmaking.LeaveLobby(new CSteamID(Current));
        Current = 0;
    }

    void OnCreated(LobbyCreated_t result, bool ioFailure)
    {
        if (ioFailure || result.m_eResult != EResult.k_EResultOK)
        {
            Failed?.Invoke("Could not create lobby: " + result.m_eResult);
            return;
        }
        Current = result.m_ulSteamIDLobby;
        var lobby = new CSteamID(Current);
        SteamMatchmaking.SetLobbyData(lobby, VersionKey, modVersion);
        SteamMatchmaking.SetLobbyData(lobby, BuildKey, gameBuild.ToString());
        SteamMatchmaking.SetLobbyData(lobby, ProtocolKey, Protocol.Version.ToString());
        Hosted?.Invoke();
    }

    void OnEntered(LobbyEnter_t result)
    {
        var lobby = new CSteamID(result.m_ulSteamIDLobby);
        var owner = SteamMatchmaking.GetLobbyOwner(lobby);
        if (owner == SteamUser.GetSteamID())
            return;

        Current = result.m_ulSteamIDLobby;
        var hostVersion = SteamMatchmaking.GetLobbyData(lobby, VersionKey);
        var hostBuild = SteamMatchmaking.GetLobbyData(lobby, BuildKey);
        var hostProtocol = SteamMatchmaking.GetLobbyData(lobby, ProtocolKey);
        string problem = null;
        if (hostProtocol != Protocol.Version.ToString() || hostVersion != modVersion)
            problem = "Host runs mod " + hostVersion + " protocol " + hostProtocol + ", you run " + modVersion + " protocol " + Protocol.Version + ". Use the same MSCCoop.dll.";
        else if (hostBuild != gameBuild.ToString())
            problem = "Host runs game build " + hostBuild + ", you run " + gameBuild + ". Both need the same game update.";

        if (problem != null)
        {
            Leave();
            Failed?.Invoke(problem);
            return;
        }
        JoinedAsClient?.Invoke(owner.m_SteamID);
    }

    void OnMemberChanged(LobbyChatUpdate_t update)
    {
        const EChatMemberStateChange gone = EChatMemberStateChange.k_EChatMemberStateChangeLeft
            | EChatMemberStateChange.k_EChatMemberStateChangeDisconnected
            | EChatMemberStateChange.k_EChatMemberStateChangeKicked
            | EChatMemberStateChange.k_EChatMemberStateChangeBanned;
        if (update.m_ulSteamIDLobby == Current && ((EChatMemberStateChange)update.m_rgfChatMemberStateChange & gone) != 0)
            MemberLeft?.Invoke(update.m_ulSteamIDUserChanged);
    }

    public void Dispose()
    {
        created.Dispose();
        entered.Dispose();
        joinRequested.Dispose();
        memberChanged.Dispose();
    }
}
