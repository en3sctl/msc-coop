using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCCoop.Net;
using UnityEngine;

namespace MSCCoop;

// Fuel, fluids and charge live in FsmFloats that change on whichever machine burns, pours or charges
// them. Like money, every machine reports its local change as a delta and the host owns the total,
// so refuelling on one machine and burning fuel on the driver's machine both count.
sealed class SharedValueSync(ObjectRegistry registry)
{
    const double SendInterval = 0.5;
    const double FullInterval = 10;
    const float Epsilon = 0.0001f;
    const int MaxPerMessage = 120;

    static readonly string[] SharedNames =
    {
        "FuelLevel", "FuelOil", "FluidCoolant", "FluidDiesel", "FluidGasoline", "FluidOil", "FluidUrine",
        "ClutchFluid", "BrakeFluidF", "BrakeFluidR", "Water", "Oil", "OilContamination", "OilGrade",
        "Charge", "Fluid",
    };

    sealed class Shared(uint id, FsmFloat value)
    {
        public readonly uint Id = id;
        public readonly FsmFloat Value = value;
        public float Last = value.Value;
        public bool Dirty;
    }

    readonly List<Shared> values = new();
    readonly Dictionary<uint, Shared> byId = new();
    Session session;
    double nextSend;
    double nextFull;

    public void Attach(Session newSession)
    {
        session = newSession;
        session.MessageReceived += OnMessage;
    }

    public void Detach()
    {
        session = null;
        values.Clear();
        byId.Clear();
    }

    public void OnLevelLoaded()
    {
        values.Clear();
        byId.Clear();
    }

    public void OnGameReady()
    {
        if (session == null)
            return;
        foreach (var fsm in registry.Fsms)
        {
            if (fsm.FsmName != "Data" || !registry.TryGetFsmId(fsm, out var fsmId))
                continue;
            foreach (var v in fsm.FsmVariables.FloatVariables)
            {
                if (System.Array.IndexOf(SharedNames, v.Name) < 0)
                    continue;
                var shared = new Shared(Fnv.Hash32(v.Name, fsmId), v);
                if (byId.ContainsKey(shared.Id))
                    continue;
                values.Add(shared);
                byId[shared.Id] = shared;
            }
        }
    }

    public void Update()
    {
        if (session == null || session.State != SessionState.Connected || values.Count == 0 || session.Now < nextSend)
            return;
        nextSend = session.Now + SendInterval;

        if (session.IsHost)
        {
            var full = session.Now >= nextFull;
            if (full)
                nextFull = session.Now + FullInterval;
            foreach (var s in values)
            {
                if (Mathf.Abs(s.Value.Value - s.Last) > Epsilon)
                {
                    s.Last = s.Value.Value;
                    s.Dirty = true;
                }
                if (full)
                    s.Dirty = true;
            }
            Send(MessageType.SharedTotals, dirtyOnly: true);
            return;
        }

        foreach (var s in values)
            s.Dirty = Mathf.Abs(s.Value.Value - s.Last) > Epsilon;
        Send(MessageType.SharedDeltas, dirtyOnly: true);
    }

    void Send(MessageType type, bool dirtyOnly)
    {
        var start = 0;
        while (start < values.Count)
        {
            var w = session.BeginMessage(type);
            var countPosition = w.BitPosition;
            w.Write(0, 8);
            var count = 0;
            var i = start;
            for (; i < values.Count && count < MaxPerMessage; i++)
            {
                var s = values[i];
                if (dirtyOnly && !s.Dirty)
                    continue;
                s.Dirty = false;
                w.WriteUInt(s.Id);
                if (type == MessageType.SharedDeltas)
                {
                    w.WriteFloat(s.Value.Value - s.Last);
                    s.Last = s.Value.Value;
                }
                else
                {
                    w.WriteFloat(s.Value.Value);
                }
                count++;
            }
            start = i;
            if (count == 0)
                return;
            w.Patch(countPosition, (uint)count, 8);
            if (session.IsHost)
                session.Broadcast(Channel.Control);
            else
                session.Send(session.HostConnection, Channel.Control);
        }
    }

    void OnMessage(Connection from, MessageType type, BitReader r)
    {
        if (type != MessageType.SharedDeltas && type != MessageType.SharedTotals)
            return;
        var count = (int)r.Read(8);
        for (var i = 0; i < count; i++)
        {
            var id = r.ReadUInt();
            var value = r.ReadFloat();
            if (r.Overflow || float.IsNaN(value) || float.IsInfinity(value))
                return;
            if (!byId.TryGetValue(id, out var s))
                continue;

            if (type == MessageType.SharedDeltas && session.IsHost)
            {
                s.Value.Value += value;
                s.Last = s.Value.Value;
                s.Dirty = true;
            }
            else if (type == MessageType.SharedTotals && !session.IsHost)
            {
                // A local change not yet reported stays on top of the host's total and goes out next tick.
                var unsent = s.Value.Value - s.Last;
                s.Value.Value = value + unsent;
                s.Last = value;
            }
        }
    }
}
