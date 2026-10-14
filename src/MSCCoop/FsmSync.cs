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
    const string StepEvent = "MSCCOOP_STEP_";

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
    readonly List<KeyValuePair<PlayMakerFSM, string>> deferred = new();
    const int MaxDeferred = 2000;
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
        deferred.Clear();
    }

    // Events for FSMs that were inactive when they arrived, e.g. doors of a building switched off by
    // distance, are entered once the FSM is running again, in the order they arrived.
    public void Update()
    {
        if (deferred.Count == 0)
            return;
        var kept = 0;
        for (var i = 0; i < deferred.Count; i++)
        {
            var e = deferred[i];
            if (e.Key == null)
                continue;
            if (IsRunning(e.Key) && !HasEarlierDeferred(e.Key, kept))
                Enter(e.Key, e.Value);
            else
                deferred[kept++] = e;
        }
        deferred.RemoveRange(kept, deferred.Count - kept);
    }

    bool HasEarlierDeferred(PlayMakerFSM fsm, int before)
    {
        for (var i = 0; i < before; i++)
        {
            if (deferred[i].Key == fsm)
                return true;
        }
        return false;
    }

    static bool IsRunning(PlayMakerFSM fsm) => fsm.enabled && fsm.gameObject.activeInHierarchy && fsm.Fsm.Started;

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
                case "Assemble":
                    HookSteps(fsm, IsWireStep);
                    break;
                case "BoltCheck" when fsm.GetState("Break off") != null:
                    HookSteps(fsm, IsBreakOff);
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
            HookTurns(fsm);
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
        if (Diagnostics.Logging)
            Diagnostics.Log("fsm sent " + registry.FsmKey(fsm));
    }

    void ApplyToggle(uint id, int index)
    {
        var fsm = registry.FindFsm(id);
        if (fsm == null || !toggles.TryGetValue(fsm, out var pair) || index >= pair.Length)
        {
            MarkUnknown("ApplyToggle", id);
            return;
        }
        // Replaying the state the door is already in would snap its animation back and play it again.
        if (toggleState.TryGetValue(fsm, out var current) && current == index)
            return;
        toggleState[fsm] = index;
        Apply(fsm, ToggleEvent + index);
    }

    // Adjusting screws (carburettor, valves, steering wheel bolts) and caps step a value in a Screw or
    // Unscrew state. Each step is deterministic, so replaying the same states in order on every
    // machine over the reliable channel keeps the values equal.
    void HookTurns(PlayMakerFSM fsm) => HookSteps(fsm, IsTurnStep);

    static bool IsTurnStep(string state) => state.StartsWith("Screw") || state.StartsWith("Unscrew");

    // A wire end connects in "Sound", which also tells the other end of the wire to close the loop.
    static bool IsWireStep(string state) => state == "Sound";

    // Loose parts break off on a random roll during crashes, which only the driver's machine simulates.
    static bool IsBreakOff(string state) => state == "Break off";

    void HookSteps(PlayMakerFSM fsm, System.Func<string, bool> isStep)
    {
        var states = fsm.FsmStates;
        var hooked = false;
        for (var i = 0; i < states.Length; i++)
        {
            var name = states[i].Name;
            if (!isStep(name))
                continue;
            var index = i;
            fsm.FsmInject(name, () => OnLocalStep(fsm, index), index: 0);
            fsm.AddSharedGlobalTransition(StepEvent + name, name);
            hooked = true;
        }
        if (!hooked)
            unsupported++;
    }

    void OnLocalStep(PlayMakerFSM fsm, int stateIndex)
    {
        if (applying || !Active || !registry.TryGetFsmId(fsm, out var id))
            return;
        var w = session.BeginMessage(MessageType.StateStep);
        w.WriteUInt(id);
        w.WriteByte((byte)stateIndex);
        session.Broadcast(Channel.Control);
        Sent++;
        if (Diagnostics.Logging)
            Diagnostics.Log("fsm sent " + registry.FsmKey(fsm));
    }

    void ApplyStep(uint id, int stateIndex)
    {
        var fsm = registry.FindFsm(id);
        if (fsm == null || stateIndex >= fsm.FsmStates.Length)
        {
            MarkUnknown("ApplyStep", id);
            return;
        }
        Apply(fsm, StepEvent + fsm.FsmStates[stateIndex].Name);
    }

    void OnLocalAssemble(PlayMakerFSM fsm, FsmGameObject partVar, byte variant)
    {
        if (applying || !Active || !registry.TryGetFsmId(fsm, out var id))
            return;
        // The save also assembles parts while loading; only an assembly from the local hand is news.
        var part = registry.Find(partVar.Value);
        if (part == null || !objects.WasRecentlyHeldLocally(part))
            return;

        var w = session.BeginMessage(MessageType.PartAssemble);
        w.WriteUInt(id);
        w.WriteByte(variant);
        w.WriteUInt(part.Id);
        session.Broadcast(Channel.Control);
        Sent++;
        if (Diagnostics.Logging)
            Diagnostics.Log("fsm sent " + registry.FsmKey(fsm));
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
        if (Diagnostics.Logging)
            Diagnostics.Log("fsm sent " + registry.FsmKey(fsm));
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
        if (Diagnostics.Logging)
            Diagnostics.Log("fsm sent " + registry.FsmKey(fsm));
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
            case MessageType.StateStep:
                var turnId = r.ReadUInt();
                var turnState = r.ReadByte();
                if (r.Overflow)
                    return;
                if (ShouldRelay)
                {
                    var w = session.BeginMessage(type);
                    w.WriteUInt(turnId);
                    w.WriteByte(turnState);
                    session.Broadcast(Channel.Control, from);
                }
                ApplyStep(turnId, turnState);
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
            MarkUnknown("ApplyAssemble", assemblyId);
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
            MarkUnknown("ApplyRemove", removalId);
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
            MarkUnknown("ApplyStage", boltId);
            return;
        }
        bolt.Stage.Value = stage;
        Apply(fsm, StageEvent);
        if (bolt.Tightness != null)
            bolt.Tightness.Value = tightness;
    }

    void MarkUnknown(string what, uint id)
    {
        Unknown++;
        if (Diagnostics.Logging)
            Diagnostics.Log("fsm unknown " + what + " " + id.ToString("x8"));
    }

    void Apply(PlayMakerFSM fsm, string eventName)
    {
        Applied++;
        if (IsRunning(fsm) && !HasEarlierDeferred(fsm, deferred.Count))
        {
            Enter(fsm, eventName);
            return;
        }
        if (Diagnostics.Logging)
            Diagnostics.Log("fsm defer " + registry.FsmKey(fsm) + " " + eventName);
        if (deferred.Count < MaxDeferred)
            deferred.Add(new KeyValuePair<PlayMakerFSM, string>(fsm, eventName));
    }

    void Enter(PlayMakerFSM fsm, string eventName)
    {
        if (Diagnostics.Logging)
            Diagnostics.Log("fsm apply " + registry.FsmKey(fsm) + " " + eventName + " from " + fsm.ActiveStateName);
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
