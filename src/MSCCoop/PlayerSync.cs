using HutongGames.PlayMaker;
using MSCCoop.Net;
using UnityEngine;

namespace MSCCoop;

sealed class PlayerSync(ObjectRegistry registry)
{
    const double SendInterval = 0.05;
    const float CrouchHeightRatio = 0.8f;

    readonly SnapshotBuffer[] buffers = new SnapshotBuffer[Protocol.MaxPlayers];
    readonly Avatar[] avatars = new Avatar[Protocol.MaxPlayers];
    readonly PlayerFlags[] flags = new PlayerFlags[Protocol.MaxPlayers];
    readonly uint[] frames = new uint[Protocol.MaxPlayers];
    Session session;
    Transform player;
    CharacterController controller;
    FsmGameObject pov;
    FsmString currentVehicle;
    float standingHeight;
    Vector3 lastPosition;
    uint lastFrame;
    double lastSendTime = double.NaN;
    double nextSend;

    public void Init()
    {
        for (var i = 0; i < buffers.Length; i++)
            buffers[i] = new SnapshotBuffer();
    }

    public void Attach(Session newSession)
    {
        session = newSession;
        session.MessageReceived += OnMessage;
        session.PlayerLeft += p => Remove(p.Slot);
    }

    public void Detach()
    {
        session = null;
        RemoveAll();
    }

    public void OnLevelLoaded()
    {
        RemoveAll();
        player = null;
    }

    // PLAYER stays inactive until the level has finished its own loading, so it is looked up from PostLoad.
    public void OnGameReady()
    {
        var go = GameObject.Find("PLAYER");
        if (go == null)
            return;
        player = go.transform;
        controller = go.GetComponent<CharacterController>();
        standingHeight = controller.height;
        var globals = PlayMakerGlobals.Instance.Variables;
        pov = globals.FindFsmGameObject("POV");
        currentVehicle = globals.FindFsmString("PlayerCurrentVehicle");
        lastSendTime = double.NaN;
    }

    public void Update(float deltaTime)
    {
        if (session == null || session.State != SessionState.Connected || player == null)
            return;

        var hostTime = session.HostTime;
        if (hostTime >= nextSend || nextSend - hostTime > 1)
            Send(hostTime);

        var camera = pov.Value == null ? null : pov.Value.transform;
        for (var slot = 0; slot < avatars.Length; slot++)
        {
            var avatar = avatars[slot];
            if (avatar == null)
                continue;
            var buffer = buffers[slot];
            buffer.AdvanceDelay(deltaTime);
            if (!buffer.Sample(hostTime - buffer.Delay, out var m))
                continue;
            var position = ToUnity(m.Position);
            var rotation = ToUnity(m.Rotation);
            var vehicle = frames[slot] == 0 ? null : registry.Find(frames[slot]);
            if (vehicle != null)
            {
                var frame = vehicle.Go.transform;
                position = frame.TransformPoint(position);
                rotation = frame.rotation * rotation;
            }
            avatar.Apply(position, rotation, ToUnity(m.Velocity), flags[slot], camera);
        }
    }

    void Send(double hostTime)
    {
        nextSend = hostTime + SendInterval;
        var position = player.position + controller.center - Vector3.up * (controller.height * 0.5f);
        var yaw = player.eulerAngles.y;
        var vehicle = player.parent == null ? null : registry.Find(player.root.gameObject);
        uint frame = 0;
        if (vehicle != null && vehicle.IsVehicle)
        {
            var t = vehicle.Go.transform;
            position = t.InverseTransformPoint(position);
            yaw = (Quaternion.Inverse(t.rotation) * player.rotation).eulerAngles.y;
            frame = vehicle.Id;
        }

        var velocity = double.IsNaN(lastSendTime) || frame != lastFrame
            ? Vector3.zero
            : (position - lastPosition) / (float)(hostTime - lastSendTime);
        lastPosition = position;
        lastFrame = frame;
        lastSendTime = hostTime;

        var state = new PlayerState
        {
            Slot = session.LocalSlot,
            Time = hostTime,
            Position = ToNet(position),
            Velocity = ToNet(velocity),
            Yaw = yaw,
            Pitch = Pitch(),
            Flags = LocalFlags(),
            Vehicle = frame,
        };
        state.Write(session.BeginMessage(MessageType.PlayerState));
        session.Broadcast(Channel.State);
    }

    void OnMessage(Connection from, MessageType type, BitReader reader)
    {
        if (type != MessageType.PlayerState)
            return;
        var state = PlayerState.Read(reader);
        if (reader.Overflow || state.Slot == session.LocalSlot)
            return;

        if (session.IsHost)
        {
            if (state.Slot != from.Slot)
                return;
            state.Write(session.BeginMessage(MessageType.PlayerState));
            session.Broadcast(Channel.State, from);
        }

        var info = session.Players[state.Slot];
        if (player == null || info == null)
            return;
        if (avatars[state.Slot] == null || frames[state.Slot] != state.Vehicle)
        {
            if (avatars[state.Slot] == null)
                avatars[state.Slot] = Avatar.Create(state.Slot, info.Name);
            buffers[state.Slot].Clear();
            frames[state.Slot] = state.Vehicle;
        }
        buffers[state.Slot].Add(state.Time, session.HostTime, new MotionState
        {
            Position = state.Position,
            Velocity = state.Velocity,
            Rotation = PlayerState.YawRotation(state.Yaw),
        });
        flags[state.Slot] = state.Flags;
    }

    float Pitch()
    {
        if (pov.Value == null)
            return 0;
        var x = pov.Value.transform.localEulerAngles.x;
        return x > 180 ? x - 360 : x;
    }

    PlayerFlags LocalFlags()
    {
        var f = PlayerFlags.None;
        if (controller.height < standingHeight * CrouchHeightRatio)
            f |= PlayerFlags.Crouching;
        if (!string.IsNullOrEmpty(currentVehicle.Value))
            f |= PlayerFlags.InVehicle;
        return f;
    }

    void Remove(byte slot)
    {
        if (slot >= avatars.Length || avatars[slot] == null)
            return;
        avatars[slot].Destroy();
        avatars[slot] = null;
        buffers[slot].Clear();
    }

    void RemoveAll()
    {
        for (byte i = 0; i < avatars.Length; i++)
            Remove(i);
    }

    static Vec3 ToNet(Vector3 v) => new(v.x, v.y, v.z);
    static Vector3 ToUnity(Vec3 v) => new(v.X, v.Y, v.Z);
    static Quaternion ToUnity(Quat q) => new(q.X, q.Y, q.Z, q.W);
}
