using System.Collections.Generic;
using System.IO;
using MSCCoop.Net;
using MSCLoader;
using UnityEngine;

namespace MSCCoop;

// Starts every player into the host's save. The host's Continue click is mirrored to clients,
// and clients read and write saves in a scratch folder so their own save is never touched.
sealed class GameFlow(string clientSaveDir)
{
    const string MenuScene = "MainMenu";
    const string GameScene = "GAME";
    const string ContinueState = "Reset globals 2";
    const string ContinueEvent = "MSCCOOP_CONTINUE";
    const string OwnOptionsFile = "options.txt";

    readonly SaveReceiver receiver = new();
    Session session;
    SessionConfig config;
    PlayMakerFSM continueFsm;
    Collider continueButton;
    Collider newGameButton;
    bool saveReady;
    bool startingFromHost;

    static bool InMenu => Application.loadedLevelName == MenuScene;
    static bool InGame => Application.loadedLevelName == GameScene;

    public bool CanJoin()
    {
        if (InMenu)
            return true;
        ModConsole.Error("Co-op: join from the main menu.");
        return false;
    }

    public void Attach(Session newSession, SessionConfig newConfig)
    {
        session = newSession;
        config = newConfig;
        config.AcceptingJoins = !InGame;
        receiver.Reset();
        saveReady = false;
        session.MessageReceived += OnMessage;
    }

    public void Detach()
    {
        var wasClient = session != null && !session.IsHost;
        session = null;
        SetMenuButtons(true);
        if (!wasClient)
            return;

        ES2GlobalSettings.defaultPCDataPath = "";
        if (!InMenu)
        {
            ModConsole.Print("Co-op: returning to the main menu.");
            Application.LoadLevel(MenuScene);
        }
    }

    public void OnLevelLoaded()
    {
        if (config != null)
            config.AcceptingJoins = !InGame && session != null && session.IsHost;

        if (InMenu)
            HookMenu();
        else if (InGame && session != null && !session.IsHost)
        {
            if (!startingFromHost)
            {
                ModConsole.Error("Co-op: loaded a game without the host, leaving the session.");
                session.Leave();
                return;
            }
            session.BeginMessage(MessageType.ClientReady);
            session.Send(session.HostConnection, Channel.Control);
        }
        startingFromHost = false;
    }

    public void Update()
    {
        if (InMenu)
            SetMenuButtons(session == null || session.IsHost);
    }

    void HookMenu()
    {
        var button = GameObject.Find("Interface/Buttons/ButtonContinue");
        continueFsm = button.GetPlayMaker("SetSize");
        continueButton = button.GetComponent<Collider>();
        newGameButton = GameObject.Find("Interface/Buttons/ButtonNewgame").GetComponent<Collider>();
        continueFsm.FsmInject(ContinueState, OnContinue, index: 0);
        continueFsm.AddGlobalTransition(ContinueEvent, ContinueState);
    }

    void SetMenuButtons(bool enabled)
    {
        if (continueButton == null)
            return;
        continueButton.enabled = enabled;
        newGameButton.enabled = enabled;
    }

    void OnContinue()
    {
        if (session == null || !session.IsHost)
            return;

        var files = ReadSave(Application.persistentDataPath);
        var connections = session.Connections;
        for (var i = 0; i < connections.Count; i++)
        {
            if (!connections[i].Established)
                continue;
            SaveTransfer.Send(session, connections[i], files);
            session.BeginMessage(MessageType.StartGame);
            session.Send(connections[i], Channel.Bulk);
        }
        config.AcceptingJoins = false;
        ModConsole.Print("Co-op: sent save to " + connections.Count + " player(s).");
    }

    void OnMessage(Connection from, MessageType type, BitReader reader)
    {
        switch (type)
        {
            case MessageType.SaveChunk when !session.IsHost:
                if (!receiver.HandleChunk(reader))
                    Fail("received a malformed save chunk.");
                break;
            case MessageType.SaveComplete when !session.IsHost:
                if (receiver.Complete(reader, out var files))
                {
                    WriteClientSave(files);
                    saveReady = true;
                }
                else
                {
                    Fail("the host's save did not arrive intact.");
                }
                break;
            case MessageType.StartGame when !session.IsHost:
                if (saveReady && InMenu)
                {
                    ES2GlobalSettings.defaultPCDataPath = clientSaveDir;
                    startingFromHost = true;
                    continueFsm.SendEvent(ContinueEvent);
                }
                break;
            case MessageType.ClientReady when session.IsHost:
                ModConsole.Print("Co-op: player in slot " + from.Slot + " finished loading.");
                break;
        }
    }

    void Fail(string reason)
    {
        ModConsole.Error("Co-op: " + reason);
        session.Leave();
    }

    static List<SaveFile> ReadSave(string dir)
    {
        var files = new List<SaveFile>();
        foreach (var path in Directory.GetFiles(dir, "*.txt"))
        {
            var name = Path.GetFileName(path);
            if (name != OwnOptionsFile && SaveTransfer.IsSafeName(name))
                files.Add(new SaveFile(name, File.ReadAllBytes(path)));
        }
        return files;
    }

    void WriteClientSave(List<SaveFile> files)
    {
        Directory.CreateDirectory(clientSaveDir);
        foreach (var old in Directory.GetFiles(clientSaveDir, "*.txt"))
            File.Delete(old);
        foreach (var file in files)
            File.WriteAllBytes(Path.Combine(clientSaveDir, file.Name), file.Data);

        var ownOptions = Path.Combine(Application.persistentDataPath, OwnOptionsFile);
        if (File.Exists(ownOptions))
            File.Copy(ownOptions, Path.Combine(clientSaveDir, OwnOptionsFile), true);
    }
}
