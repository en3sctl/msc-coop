using HutongGames.PlayMaker;
using MSCCoop.Net;
using UnityEngine;

namespace MSCCoop;

// One shared wallet held by the host. Dozens of FSMs write PlayerMoney (shops, jobs, bottle returns),
// so the value is watched rather than hooked. Clients report changes as deltas, never totals, so two
// players spending at the same moment cannot overwrite each other.
sealed class MoneySync
{
    const float Epsilon = 0.001f;
    const double TotalInterval = 5;

    Session session;
    FsmFloat money;
    float last;
    double nextTotal;

    public void Attach(Session newSession)
    {
        session = newSession;
        session.MessageReceived += OnMessage;
    }

    public void Detach()
    {
        session = null;
        money = null;
    }

    public void OnLevelLoaded() => money = null;

    public void OnGameReady()
    {
        if (session == null)
            return;
        money = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerMoney");
        if (money == null)
            return;
        last = money.Value;
        if (session.IsHost)
            BroadcastTotal();
    }

    public void Update()
    {
        if (money == null || session == null || session.State != SessionState.Connected)
            return;
        if (session.IsHost && session.Now >= nextTotal)
            BroadcastTotal();
        var delta = money.Value - last;
        if (Mathf.Abs(delta) < Epsilon)
            return;
        last = money.Value;
        if (session.IsHost)
        {
            BroadcastTotal();
            return;
        }
        session.BeginMessage(MessageType.MoneyDelta).WriteFloat(delta);
        session.Send(session.HostConnection, Channel.Control);
    }

    void OnMessage(Connection from, MessageType type, BitReader r)
    {
        if (money == null || type != MessageType.MoneyDelta && type != MessageType.MoneyTotal)
            return;
        var value = r.ReadFloat();
        if (r.Overflow || float.IsNaN(value) || float.IsInfinity(value))
            return;

        if (type == MessageType.MoneyDelta && session.IsHost)
        {
            money.Value += value;
            last = money.Value;
            BroadcastTotal();
        }
        else if (type == MessageType.MoneyTotal && !session.IsHost)
        {
            // A local change not yet reported stays on top of the host's total and goes out next frame.
            var unsent = money.Value - last;
            money.Value = value + unsent;
            last = value;
        }
    }

    void BroadcastTotal()
    {
        nextTotal = session.Now + TotalInterval;
        session.BeginMessage(MessageType.MoneyTotal).WriteFloat(money.Value);
        session.Broadcast(Channel.Control);
    }
}
