using System.Collections.Generic;
using HutongGames.PlayMaker;
using MSCCoop.Net;
using MSCLoader;
using UnityEngine;

namespace MSCCoop;

// Distributed physics authority: whoever moves an object simulates it and streams it, everyone
// else follows a kinematic copy. The host arbitrates ownership; objects nobody owns rest on
// every machine under local physics, which stays in agreement because nothing is moving.
sealed class ObjectSync
{
    const double SendInterval = 0.05;
    const float ClaimSpeed = 0.15f;
    const float ClaimAngularSpeed = 0.5f;
    const float RestSpeed = 0.05f;
    const float RestAngularSpeed = 0.1f;
    const int RestStepsToRelease = 16;
    const int BodyCheckInterval = 50;
    const int MaxPacketsPerSend = 2;
    const int SnapshotChunk = 400;

    // The pickup ray ignores the layer the game itself gives an item while it is in a hand.
    const int HeldElsewhereLayer = 16;

    const string HandPath = "PLAYER/Pivot/AnimPivot/Camera/FPSCamera/1Hand_Assemble/Hand";
    static readonly string[] PickupStates = { "Set pivot", "Set pivot 2", "Tool picked" };
    static readonly string[] DropStates = { "Drop part", "Drop part 2", "Drop tool" };
    const string DriveState = "Player in car";
    static readonly string[] LeaveStates = { "Create player", "Create player 2" };

    readonly ObjectRegistry registry = new();
    readonly Ownership table = new();
    readonly List<Connection> pendingSnapshots = new();
    readonly List<KeyValuePair<uint, OwnerInfo>> released = new();
    Session session;
    PlayMakerFSM pickUp;
    FsmGameObject pickedObject;
    bool built;
    bool worldReady;
    int fixedStep;
    int sendCursor;
    double nextSend;

    bool Active => session != null && session.State == SessionState.Connected && built;
    byte Local => session.LocalSlot;

    public ObjectRegistry Registry => registry;

    public void Attach(Session newSession)
    {
        session = newSession;
        table.Clear();
        pendingSnapshots.Clear();
        session.MessageReceived += OnMessage;
        session.PlayerLeft += OnPlayerLeft;
    }

    public void Detach()
    {
        foreach (var obj in registry.Objects)
            EndProxy(obj);
        session = null;
        built = false;
    }

    public void OnLevelLoaded()
    {
        built = false;
        worldReady = false;
        if (Application.loadedLevelName == "GAME" && session != null)
            registry.CaptureAwake();
        else
            registry.Clear();
    }

    public void OnGameReady()
    {
        if (session == null)
            return;
        registry.Build();
        built = true;
        HookPickup();
        HookVehicles();
        ModConsole.Print("Co-op: tracking " + registry.Objects.Count + " objects.");

        if (session.IsHost)
        {
            worldReady = true;
            foreach (var c in pendingSnapshots)
                SendSnapshot(c);
            pendingSnapshots.Clear();
            return;
        }
        var w = session.BeginMessage(MessageType.ObjectHello);
        w.WriteUShort((ushort)registry.Objects.Count);
        w.WriteUInt(registry.Hash);
        session.Send(session.HostConnection, Channel.Control);
    }

    public void FixedUpdate()
    {
        if (!Active)
            return;
        fixedStep++;
        var hostTime = session.HostTime;
        var dt = Time.fixedDeltaTime;

        var objects = registry.Objects;
        for (var i = 0; i < objects.Count; i++)
        {
            var obj = objects[i];
            var body = Body(obj);
            if (obj.Proxy)
            {
                ApplyProxy(obj, body, hostTime, dt);
                continue;
            }
            if (body == null || body.isKinematic)
                continue;

            if (obj.Owner.Owner == Local)
            {
                if (!obj.Owner.Held)
                    TrackRest(obj, body);
            }
            else if (!obj.Owner.IsOwned && worldReady && !body.IsSleeping()
                && (body.velocity.sqrMagnitude > ClaimSpeed * ClaimSpeed
                    || body.angularVelocity.sqrMagnitude > ClaimAngularSpeed * ClaimAngularSpeed))
            {
                Claim(obj, held: false);
            }
        }
    }

    public void Update()
    {
        if (!Active)
            return;
        var hostTime = session.HostTime;
        if (hostTime < nextSend && nextSend - hostTime < 1)
            return;
        nextSend = hostTime + SendInterval;
        SendStates(hostTime);
    }

    void TrackRest(NetObject obj, Rigidbody body)
    {
        if (body.IsSleeping() || body.velocity.sqrMagnitude < RestSpeed * RestSpeed
            && body.angularVelocity.sqrMagnitude < RestAngularSpeed * RestAngularSpeed)
        {
            if (++obj.RestSteps >= RestStepsToRelease)
                Release(obj);
        }
        else
        {
            obj.RestSteps = 0;
        }
    }

    void Claim(NetObject obj, bool held)
    {
        obj.RestSteps = 0;
        if (session.IsHost)
        {
            table.TryClaim(obj.Id, Local, held, out var info);
            ApplyOwner(obj, info);
            BroadcastOwner(obj.Id, info, null);
            return;
        }
        obj.Owner.Owner = Local;
        obj.Owner.Held = held;
        obj.Pending = true;
        EndProxy(obj);
        var w = session.BeginMessage(MessageType.ObjectClaim);
        w.WriteUInt(obj.Id);
        w.WriteBool(held);
        session.Send(session.HostConnection, Channel.Control);
    }

    void Release(NetObject obj)
    {
        obj.RestSteps = 0;
        var position = ToNet(obj.Go.transform.position);
        var rotation = ToNet(obj.Go.transform.rotation);
        if (session.IsHost)
        {
            if (table.Release(obj.Id, Local, out var info))
            {
                ApplyOwner(obj, info);
                BroadcastOwner(obj.Id, info, obj);
            }
            return;
        }
        obj.Owner.Owner = Ownership.NoOwner;
        obj.Owner.Held = false;
        obj.Pending = true;
        var w = session.BeginMessage(MessageType.ObjectRelease);
        w.WriteUInt(obj.Id);
        ObjectMessages.WritePose(w, position, rotation);
        session.Send(session.HostConnection, Channel.Control);
    }

    void SendStates(double hostTime)
    {
        var objects = registry.Objects;
        if (objects.Count == 0)
            return;
        var packets = 0;
        var start = sendCursor;
        BitWriter w = null;
        var countPosition = 0;
        var count = 0;
        for (var n = 0; n < objects.Count; n++)
        {
            var index = (start + n) % objects.Count;
            var obj = objects[index];
            if (obj.Owner.Owner != Local || obj.Proxy)
                continue;
            var body = Body(obj);
            if (body == null || body.isKinematic || !obj.Owner.Held && body.IsSleeping())
                continue;

            if (w != null && w.BitPosition + ObjectMessages.MaxStateBits > Protocol.MaxUnreliableBytes * 8)
            {
                FinishStates(w, countPosition, count);
                w = null;
                if (++packets == MaxPacketsPerSend)
                {
                    sendCursor = index;
                    return;
                }
            }
            if (w == null)
            {
                w = BeginStates(hostTime, Local, out countPosition);
                count = 0;
            }
            new ObjectState
            {
                Id = obj.Id,
                Position = ToNet(body.position),
                Rotation = ToNet(body.rotation),
                Velocity = ToNet(body.velocity),
                AngularVelocity = ToNet(body.angularVelocity),
            }.Write(w);
            count++;
        }
        if (w != null)
            FinishStates(w, countPosition, count);
        sendCursor = 0;
    }

    BitWriter BeginStates(double hostTime, byte owner, out int countPosition)
    {
        var w = session.BeginMessage(MessageType.ObjectStates);
        w.WriteUInt((uint)(long)(hostTime * 1000));
        w.Write(owner, ObjectMessages.SlotBits);
        countPosition = w.BitPosition;
        w.Write(0, ObjectMessages.CountBits);
        return w;
    }

    void FinishStates(BitWriter w, int countPosition, int count)
    {
        PatchCount(w, countPosition, count);
        session.Broadcast(Channel.State);
    }

    static void PatchCount(BitWriter w, int position, int count)
    {
        var buffer = w.Buffer;
        for (var bit = 0; bit < ObjectMessages.CountBits; bit++)
        {
            var at = position + bit;
            var mask = (byte)(1 << (at & 7));
            if ((count >> bit & 1) != 0)
                buffer[at >> 3] |= mask;
            else
                buffer[at >> 3] &= (byte)~mask;
        }
    }

    void OnMessage(Connection from, MessageType type, BitReader r)
    {
        switch (type)
        {
            case MessageType.ObjectHello when session.IsHost:
                var count = r.ReadUShort();
                var hash = r.ReadUInt();
                if (!built)
                {
                    pendingSnapshots.Add(from);
                    break;
                }
                if (count != registry.Objects.Count || hash != registry.Hash)
                    ModConsole.Warning("Co-op: player " + from.Slot + " tracks " + count + " objects, host tracks " + registry.Objects.Count + ". Some objects will not sync.");
                SendSnapshot(from);
                break;
            case MessageType.ObjectSnapshot when !session.IsHost:
                ReadSnapshot(r);
                break;
            case MessageType.ObjectClaim when session.IsHost:
                HandleClaim(from, r.ReadUInt(), r.ReadBool());
                break;
            case MessageType.ObjectRelease when session.IsHost:
                HandleRelease(from, r);
                break;
            case MessageType.ObjectOwner when !session.IsHost:
                ReadOwner(r);
                break;
            case MessageType.ObjectStates:
                ReadStates(from, r);
                break;
        }
    }

    void HandleClaim(Connection from, uint id, bool held)
    {
        var obj = registry.Find(id);
        if (obj == null)
            return;
        table.TryClaim(id, from.Slot, held, out var info);
        ApplyOwner(obj, info);
        BroadcastOwner(id, info, null);
    }

    void HandleRelease(Connection from, BitReader r)
    {
        var id = r.ReadUInt();
        ObjectMessages.ReadPose(r, out var position, out var rotation);
        var obj = registry.Find(id);
        if (r.Overflow || obj == null || !table.Release(id, from.Slot, out var info))
            return;
        ApplyOwner(obj, info);
        SetPose(obj, position, rotation);
        BroadcastOwner(id, info, obj);
    }

    void BroadcastOwner(uint id, OwnerInfo info, NetObject withPose)
    {
        var w = session.BeginMessage(MessageType.ObjectOwner);
        ObjectMessages.WriteOwner(w, id, info);
        w.WriteBool(withPose != null);
        if (withPose != null)
            ObjectMessages.WritePose(w, ToNet(withPose.Go.transform.position), ToNet(withPose.Go.transform.rotation));
        session.Broadcast(Channel.Control);
    }

    void ReadOwner(BitReader r)
    {
        var info = ObjectMessages.ReadOwner(r, out var id);
        var hasPose = r.ReadBool();
        Vec3 position = default;
        Quat rotation = default;
        if (hasPose)
            ObjectMessages.ReadPose(r, out position, out rotation);
        var obj = registry.Find(id);
        if (r.Overflow || obj == null)
            return;
        if (!Seq.IsNewer(info.Seq, obj.Owner.Seq) && !(obj.Pending && info.Seq == obj.Owner.Seq))
            return;
        ApplyOwner(obj, info);
        if (hasPose && info.Owner != Local)
            SetPose(obj, position, rotation);
    }

    void ApplyOwner(NetObject obj, OwnerInfo info)
    {
        obj.Owner = info;
        obj.Pending = false;
        obj.RestSteps = 0;
        var heldElsewhere = info.IsOwned && info.Owner != Local && info.Held;
        if (obj.SeatTriggers != null)
        {
            foreach (var trigger in obj.SeatTriggers)
                trigger.enabled = !heldElsewhere;
        }

        if (info.IsOwned && info.Owner != Local)
        {
            BeginProxy(obj);
            if (!obj.IsVehicle)
                obj.Go.layer = heldElsewhere ? HeldElsewhereLayer : obj.Layer;
            if (pickedObject != null && pickedObject.Value == obj.Go)
                pickUp.SendEvent("PROCEED Drop");
        }
        else
        {
            EndProxy(obj);
            if (!obj.IsVehicle && obj.Go.layer == HeldElsewhereLayer && (pickedObject == null || pickedObject.Value != obj.Go))
                obj.Go.layer = obj.Layer;
        }
    }

    void ReadStates(Connection from, BitReader r)
    {
        var time = r.ReadUInt() / 1000.0;
        var owner = (byte)r.Read(ObjectMessages.SlotBits);
        var count = (int)r.Read(ObjectMessages.CountBits);
        if (r.Overflow || session.IsHost && owner != from.Slot)
            return;

        BitWriter relay = null;
        var relayCountPosition = 0;
        var relayCount = 0;

        for (var i = 0; i < count; i++)
        {
            var state = ObjectState.Read(r);
            if (r.Overflow)
                return;
            var obj = registry.Find(state.Id);
            if (obj == null || obj.Owner.Owner != owner || !obj.Proxy)
                continue;
            obj.Buffer.Add(time, session.HostTime, new MotionState
            {
                Position = state.Position,
                Rotation = state.Rotation,
                Velocity = state.Velocity,
                AngularVelocity = state.AngularVelocity,
            });

            if (!session.IsHost || session.Connections.Count < 2)
                continue;
            if (relay == null)
            {
                relay = BeginStates(time, owner, out relayCountPosition);
                relayCount = 0;
            }
            state.Write(relay);
            relayCount++;
        }

        if (relay != null)
        {
            PatchCount(relay, relayCountPosition, relayCount);
            session.Broadcast(Channel.State, from);
        }
    }

    void SendSnapshot(Connection to)
    {
        var objects = registry.Objects;
        for (var start = 0; start < objects.Count || start == 0; start += SnapshotChunk)
        {
            var end = Mathf.Min(start + SnapshotChunk, objects.Count);
            var w = session.BeginMessage(MessageType.ObjectSnapshot);
            w.WriteBool(end == objects.Count);
            w.WriteUShort((ushort)(end - start));
            for (var i = start; i < end; i++)
            {
                var obj = objects[i];
                ObjectMessages.WriteOwner(w, obj.Id, table.Get(obj.Id));
                ObjectMessages.WritePose(w, ToNet(obj.Go.transform.position), ToNet(obj.Go.transform.rotation));
            }
            session.Send(to, Channel.Bulk);
            if (end == objects.Count)
                break;
        }
    }

    void ReadSnapshot(BitReader r)
    {
        var last = r.ReadBool();
        var count = r.ReadUShort();
        for (var i = 0; i < count; i++)
        {
            var info = ObjectMessages.ReadOwner(r, out var id);
            ObjectMessages.ReadPose(r, out var position, out var rotation);
            if (r.Overflow)
                return;
            var obj = registry.Find(id);
            if (obj == null)
                continue;
            ApplyOwner(obj, info);
            SetPose(obj, position, rotation);
        }
        if (last)
        {
            worldReady = true;
            ModConsole.Print("Co-op: world state received.");
        }
    }

    void OnPlayerLeft(PlayerInfo player)
    {
        if (session == null || !session.IsHost)
            return;
        released.Clear();
        table.ReleaseAll(player.Slot, released);
        foreach (var pair in released)
        {
            var obj = registry.Find(pair.Key);
            if (obj == null)
                continue;
            ApplyOwner(obj, pair.Value);
            BroadcastOwner(pair.Key, pair.Value, null);
        }
    }

    void HookPickup()
    {
        var hand = GameObject.Find(HandPath);
        pickUp = hand == null ? null : hand.GetPlayMaker("PickUp");
        if (pickUp == null)
        {
            ModConsole.Error("Co-op: could not find the pickup logic, held items will not sync.");
            return;
        }
        pickedObject = pickUp.FsmVariables.FindFsmGameObject("PickedObject");
        foreach (var state in PickupStates)
            pickUp.FsmInject(state, OnLocalPickup, index: 0);
        foreach (var state in DropStates)
            pickUp.FsmInject(state, OnLocalDrop, index: 0);
    }

    // Entering the driver's seat holds the vehicle; nobody else can drive it until the driver gets out.
    void HookVehicles()
    {
        foreach (var obj in registry.Objects)
        {
            if (!obj.IsVehicle)
                continue;
            foreach (var fsm in obj.Go.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (fsm.FsmName != "PlayerTrigger" || fsm.gameObject.name != "DriveTrigger")
                    continue;
                var vehicle = obj;
                fsm.FsmInject(DriveState, () => OnDrive(vehicle, true), index: 0);
                foreach (var state in LeaveStates)
                    fsm.FsmInject(state, () => OnDrive(vehicle, false), index: 0);
                obj.SeatTriggers = fsm.GetComponents<Collider>();
            }
        }
    }

    void OnDrive(NetObject vehicle, bool driving)
    {
        if (!Active || vehicle.Owner.Owner == Local && vehicle.Owner.Held == driving)
            return;
        if (driving || vehicle.Owner.Owner == Local)
            Claim(vehicle, held: driving);
    }

    void OnLocalPickup()
    {
        if (!Active)
            return;
        var obj = registry.Find(pickedObject.Value);
        if (obj != null)
            Claim(obj, held: true);
    }

    void OnLocalDrop()
    {
        if (!Active)
            return;
        var obj = registry.Find(pickedObject.Value);
        if (obj != null && obj.Owner.Owner == Local)
            Claim(obj, held: false);
    }

    void ApplyProxy(NetObject obj, Rigidbody body, double hostTime, float dt)
    {
        // The game toggles some bodies itself, e.g. the Satsuma while a tool is in hand.
        if (body != null && !body.isKinematic)
        {
            obj.GameKinematic = false;
            body.isKinematic = true;
        }

        var buffer = obj.Buffer;
        if (buffer.Count == 0)
            return;
        buffer.AdvanceDelay(dt);
        if (!buffer.Sample(hostTime - buffer.Delay, out var m))
            return;
        if (body != null)
        {
            body.MovePosition(ToUnity(m.Position));
            body.MoveRotation(ToUnity(m.Rotation));
        }
        else
        {
            obj.Go.transform.position = ToUnity(m.Position);
            obj.Go.transform.rotation = ToUnity(m.Rotation);
        }
    }

    void BeginProxy(NetObject obj)
    {
        if (obj.Proxy)
            return;
        obj.Proxy = true;
        obj.Buffer ??= new SnapshotBuffer();
        obj.Buffer.Clear();
        var body = Body(obj);
        if (body == null)
            return;
        obj.GameKinematic = body.isKinematic;
        obj.GameInterpolation = body.interpolation;
        body.isKinematic = true;
        body.interpolation = RigidbodyInterpolation.Interpolate;
    }

    void EndProxy(NetObject obj)
    {
        if (!obj.Proxy)
            return;
        obj.Proxy = false;
        var body = Body(obj);
        if (body == null)
            return;
        body.isKinematic = obj.GameKinematic;
        body.interpolation = obj.GameInterpolation;
    }

    static void SetPose(NetObject obj, Vec3 position, Quat rotation)
    {
        obj.Go.transform.position = ToUnity(position);
        obj.Go.transform.rotation = ToUnity(rotation);
        var body = obj.Body;
        if (body == null || body.isKinematic)
            return;
        body.velocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        body.Sleep();
    }

    // The game destroys a part's rigidbody when it is bolted on and adds a new one when it comes off.
    Rigidbody Body(NetObject obj)
    {
        if (obj.Body == null && fixedStep >= obj.NextBodyCheck)
        {
            obj.Body = obj.Go == null ? null : obj.Go.GetComponent<Rigidbody>();
            obj.NextBodyCheck = fixedStep + BodyCheckInterval;
        }
        return obj.Body;
    }

    static Vec3 ToNet(Vector3 v) => new(v.x, v.y, v.z);
    static Quat ToNet(Quaternion q) => new(q.x, q.y, q.z, q.w);
    static Vector3 ToUnity(Vec3 v) => new(v.X, v.Y, v.Z);
    static Quaternion ToUnity(Quat q) => new(q.X, q.Y, q.Z, q.W);
}
