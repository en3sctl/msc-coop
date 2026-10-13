using HutongGames.PlayMaker;
using MSCCoop.Net;
using UnityEngine;

namespace MSCCoop;

sealed class PlayerSync
{
    const double SendInterval = 0.05;
    const float CrouchHeightRatio = 0.8f;

    readonly SnapshotBuffer[] buffers = new SnapshotBuffer[Protocol.MaxPlayers];
    readonly Avatar[] avatars = new Avatar[Protocol.MaxPlayers];
    readonly PlayerFlags[] flags = new PlayerFlags[Protocol.MaxPlayers];
    Session session;
    Transform player;
    CharacterController controller;
    FsmGameObject pov;
    FsmString currentVehicle;
    float standingHeight;
    Vector3 lastFeet;
    double lastSendTime = double.NaN;
    double nextSend;

    public PlayerSync()
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
        if (Application.loadedLevelName != "GAME")
            return;

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
            if (buffer.Sample(hostTime - buffer.Delay, out var m))
                avatar.Apply(ToUnity(m.Position), ToUnity(m.Rotation), ToUnity(m.Velocity), flags[slot], camera);
        }
    }

    void Send(double hostTime)
    {
        nextSend = hostTime + SendInterval;
        var feet = player.position + controller.center - Vector3.up * (controller.height * 0.5f);
        var velocity = double.IsNaN(lastSendTime) ? Vector3.zero : (feet - lastFeet) / (float)(hostTime - lastSendTime);
        lastFeet = feet;
        lastSendTime = hostTime;

        var state = new PlayerState
        {
            Slot = session.LocalSlot,
            Time = hostTime,
            Position = ToNet(feet),
            Velocity = ToNet(velocity),
            Yaw = player.eulerAngles.y,
            Pitch = Pitch(),
            Flags = LocalFlags(),
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
        if (avatars[state.Slot] == null)
        {
            avatars[state.Slot] = Avatar.Create(state.Slot, info.Name);
            buffers[state.Slot].Clear();
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
