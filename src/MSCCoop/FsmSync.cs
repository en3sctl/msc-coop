using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCCoop.Net;
using MSCLoader;
using UnityEngine;

namespace MSCCoop;

// Mirrors part assembly, removal and bolting by driving the same PlayMaker states on every machine.
// Bolts are sent as absolute stage and part tightness rather than as turns, so a repeated or late
// message cannot leave a bolt one step off.
sealed class FsmSync(ObjectRegistry registry, ObjectSync objects)
{
    const string AssembleEvent = "MSCCOOP_ASSEMBLE";
    const string AssembleEvent2 = "MSCCOOP_ASSEMBLE2";
    const string RemoveEvent = "MSCCOOP_REMOVE";
    const string StageEvent = "MSCCOOP_STAGE";
    const string ToggleEvent = "MSCCOOP_TOGGLE_";

    // Doors, hoods, lids and similar: the game plays an animation in these states, so entering the
    // same state on every machine reproduces the movement and the sound.
    static readonly string[][] TogglePairs =
    {
        new[] { "Open door", "Close door" },
        new[] { "Open hood", "Close hood" },
        new[] { "Open hood 2", "Close hood 2" },
        new[] { "Open", "Close" },
    };

    sealed class Bolt
    {
        public FsmInt Stage;
        public FsmFloat Tightness;
    }

    readonly Dictionary<PlayMakerFSM, Bolt> bolts = new();
    readonly HashSet<PlayMakerFSM> turning = new();
    readonly Dictionary<PlayMakerFSM, string[]> toggles = new();
    readonly Dictionary<PlayMakerFSM, int> toggleState = new();
    Session session;
    bool applying;
    int unsupported;

    public int Sent { get; private set; }
    public int Applied { get; private set; }
    public int Unknown { get; private set; }

    bool Active => session != null && session.State == SessionState.Connected;

    public void Attach(Session newSession)
    {
        session = newSession;
        session.MessageReceived += OnMessage;
    }

    public void Detach()
    {
        session = null;
        bolts.Clear();
        turning.Clear();
        toggles.Clear();
        toggleState.Clear();
    }

    public void OnGameReady()
    {
        bolts.Clear();
        toggles.Clear();
        toggleState.Clear();
        unsupported = 0;
        foreach (var fsm in registry.Fsms)
        {
            switch (fsm.FsmName)
            {
                case "Assembly":
                    HookAssembly(fsm);
                    break;
                case "Removal":
                    HookRemoval(fsm);
                    break;
                case "Screw":
                    HookScrew(fsm);
                    break;
                case "Use":
                    HookToggle(fsm);
                    break;
            }
        }
        if (unsupported > 0)
            ModConsole.Warning("Co-op: " + unsupported + " assembly FSMs have an unknown layout and will not sync.");
    }

    void HookAssembly(PlayMakerFSM fsm)
    {
        var part = fsm.FsmVariables.FindFsmGameObject("Part");
        var hooked = false;
        if (fsm.GetState("Assemble") != null)
        {
            fsm.FsmInject("Assemble", () => OnLocalAssemble(fsm, part, 0), index: 0);
            fsm.AddSharedGlobalTransition(AssembleEvent, "Assemble");
            hooked = true;
        }
        if (fsm.GetState("Assemble 2") != null)
        {
            fsm.FsmInject("Assemble 2", () => OnLocalAssemble(fsm, part, 1), index: 0);
            fsm.AddSharedGlobalTransition(AssembleEvent2, "Assemble 2");
            hooked = true;
        }
        if (!hooked || part == null)
            unsupported++;
    }

    void HookRemoval(PlayMakerFSM fsm)
    {
        var detach = fsm.FsmVariables.FindFsmBool("Detach");
        if (fsm.GetState("Remove part") == null || detach == null)
        {
            unsupported++;
            return;
        }
        fsm.FsmInject("Remove part", () => OnLocalRemove(fsm, detach), index: 0);
        fsm.AddSharedGlobalTransition(RemoveEvent, "Remove part");
    }

    void HookScrew(PlayMakerFSM fsm)
    {
        var stage = fsm.FsmVariables.FindFsmInt("Stage");
        var partVar = fsm.FsmVariables.FindFsmGameObject("PartAssembled");
        if (stage == null || partVar == null || fsm.GetState("Get scroll 2") == null || fsm.GetState("Stages 2") == null)
        {
            unsupported++;
            return;
        }
        var part = partVar.Value;
        var check = part == null ? null : part.GetPlayMaker("BoltCheck");
        bolts[fsm] = new Bolt { Stage = stage, Tightness = check == null ? null : check.FsmVariables.FindFsmFloat("Tightness") };

        fsm.FsmInject("Screw 2", () => OnLocalTurn(fsm), index: 0);
        fsm.FsmInject("Unscrew 2", () => OnLocalTurn(fsm), index: 0);
        fsm.FsmInject("Get scroll 2", () => OnStageSettled(fsm), index: 0);
        fsm.AddSharedGlobalTransition(StageEvent, "Stages 2");
    }

    void HookToggle(PlayMakerFSM fsm)
    {
        foreach (var pair in TogglePairs)
        {
            if (fsm.GetState(pair[0]) == null || fsm.GetState(pair[1]) == null)
                continue;
            toggles[fsm] = pair;
            for (var i = 0; i < pair.Length; i++)
            {
                var index = i;
                fsm.FsmInject(pair[i], () => OnLocalToggle(fsm, index), index: 0);
                fsm.AddSharedGlobalTransition(ToggleEvent + i, pair[i]);
            }
            return;
        }
    }

    void OnLocalToggle(PlayMakerFSM fsm, int index)
    {
        if (applying || !Active || !registry.TryGetFsmId(fsm, out var id))
            return;
        toggleState[fsm] = index;
        var w = session.BeginMessage(MessageType.ToggleState);
        w.WriteUInt(id);
        w.WriteByte((byte)index);
        session.Broadcast(Channel.Control);
        Sent++;
    }

    void ApplyToggle(uint id, int index)
    {
        var fsm = registry.FindFsm(id);
        if (fsm == null || !toggles.TryGetValue(fsm, out var pair) || index >= pair.Length)
        {
            Unknown++;
            return;
        }
        // Replaying the state the door is already in would snap its animation back and play it again.
        if (toggleState.TryGetValue(fsm, out var current) && current == index)
            return;
        toggleState[fsm] = index;
        Apply(fsm, ToggleEvent + index);
    }

    void OnLocalAssemble(PlayMakerFSM fsm, FsmGameObject partVar, byte variant)
    {
        if (applying || !Active || !registry.TryGetFsmId(fsm, out var id))
            return;
        // The save also assembles parts while loading; only an assembly from the local hand is news.
        var part = registry.Find(partVar.Value);
        if (part == null || !objects.IsHeldLocally(part))
            return;

        var w = session.BeginMessage(MessageType.PartAssemble);
        w.WriteUInt(id);
        w.WriteByte(variant);
        w.WriteUInt(part.Id);
        session.Broadcast(Channel.Control);
        Sent++;
        objects.ReleaseWithoutPose(part);
        objects.RefreshBody(part);
    }

    void OnLocalRemove(PlayMakerFSM fsm, FsmBool detach)
    {
        // Detach is set when a removal cascades from another part; every machine runs that cascade itself.
        if (applying || !Active || detach.Value || !registry.TryGetFsmId(fsm, out var id))
            return;
        session.BeginMessage(MessageType.PartRemove).WriteUInt(id);
        session.Broadcast(Channel.Control);
        Sent++;
        var part = registry.Find(fsm.gameObject);
        if (part != null)
            objects.ClaimNextStep(part);
    }

    void OnLocalTurn(PlayMakerFSM fsm)
    {
        if (!applying && Active)
            turning.Add(fsm);
    }

    void OnStageSettled(PlayMakerFSM fsm)
    {
        if (!turning.Remove(fsm) || !Active || !registry.TryGetFsmId(fsm, out var id))
            return;
        var bolt = bolts[fsm];
        var w = session.BeginMessage(MessageType.BoltStage);
        w.WriteUInt(id);
        w.Write((uint)Mathf.Clamp(bolt.Stage.Value, 0, 8), 4);
        w.WriteFloat(bolt.Tightness == null ? 0 : bolt.Tightness.Value);
        session.Broadcast(Channel.Control);
        Sent++;
    }

    void OnMessage(Connection from, MessageType type, BitReader r)
    {
        switch (type)
        {
            case MessageType.PartAssemble:
                var assemblyId = r.ReadUInt();
                var variant = r.ReadByte();
                var partId = r.ReadUInt();
                if (r.Overflow)
                    return;
                if (ShouldRelay)
                {
                    var w = session.BeginMessage(type);
                    w.WriteUInt(assemblyId);
                    w.WriteByte(variant);
                    w.WriteUInt(partId);
                    session.Broadcast(Channel.Control, from);
                }
                ApplyAssemble(assemblyId, variant, partId);
                break;
            case MessageType.PartRemove:
                var removalId = r.ReadUInt();
                if (r.Overflow)
                    return;
                if (ShouldRelay)
                {
                    session.BeginMessage(type).WriteUInt(removalId);
                    session.Broadcast(Channel.Control, from);
                }
                ApplyRemove(removalId);
                break;
            case MessageType.ToggleState:
                var toggleId = r.ReadUInt();
                var toggleIndex = r.ReadByte();
                if (r.Overflow)
                    return;
                if (ShouldRelay)
                {
                    var w = session.BeginMessage(type);
                    w.WriteUInt(toggleId);
                    w.WriteByte(toggleIndex);
                    session.Broadcast(Channel.Control, from);
                }
                ApplyToggle(toggleId, toggleIndex);
                break;
            case MessageType.BoltStage:
                var boltId = r.ReadUInt();
                var stage = (int)r.Read(4);
                var tightness = r.ReadFloat();
                if (r.Overflow || stage > 8 || float.IsNaN(tightness))
                    return;
                if (ShouldRelay)
                {
                    var w = session.BeginMessage(type);
                    w.WriteUInt(boltId);
                    w.Write((uint)stage, 4);
                    w.WriteFloat(tightness);
                    session.Broadcast(Channel.Control, from);
                }
                ApplyStage(boltId, stage, tightness);
                break;
        }
    }

    // Clients only talk to the host, so the host forwards every change to the other clients.
    bool ShouldRelay => session.IsHost && session.Connections.Count > 1;

    void ApplyAssemble(uint assemblyId, byte variant, uint partId)
    {
        var fsm = registry.FindFsm(assemblyId);
        var part = registry.Find(partId);
        if (fsm == null || part == null)
        {
            Unknown++;
            return;
        }
        objects.TakeOutOfProxy(part);
        fsm.FsmVariables.FindFsmGameObject("Part").Value = part.Go;
        Apply(fsm, variant == 0 ? AssembleEvent : AssembleEvent2);
        objects.RefreshBody(part);
    }

    void ApplyRemove(uint removalId)
    {
        var fsm = registry.FindFsm(removalId);
        if (fsm == null)
        {
            Unknown++;
            return;
        }
        Apply(fsm, RemoveEvent);
        var part = registry.Find(fsm.gameObject);
        if (part != null)
            objects.RefreshBody(part);
    }

    void ApplyStage(uint boltId, int stage, float tightness)
    {
        var fsm = registry.FindFsm(boltId);
        if (fsm == null || !bolts.TryGetValue(fsm, out var bolt))
        {
            Unknown++;
            return;
        }
        bolt.Stage.Value = stage;
        Apply(fsm, StageEvent);
        if (bolt.Tightness != null)
            bolt.Tightness.Value = tightness;
    }

    void Apply(PlayMakerFSM fsm, string eventName)
    {
        Applied++;
        applying = true;
        try
        {
            fsm.SendEvent(eventName);
        }
        finally
        {
            applying = false;
        }
    }
}
