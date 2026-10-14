using System;
using System.IO;
using System.Text;
using MSCCoop.Net;
using UnityEngine;

namespace MSCCoop;

// An optional in-memory event log and a full snapshot of what this machine believes about every
// synced object and FSM, written side by side on two machines to find where they disagree.
static class Diagnostics
{
    const int Capacity = 4000;

    static readonly string[] lines = new string[Capacity];
    static int next;
    static int count;

    // Callers check this before building a message so a disabled log costs nothing.
    public static bool Logging;

    public static void Log(string line)
    {
        lines[next] = Time.realtimeSinceStartup.ToString("0.000") + " " + line;
        next = (next + 1) % Capacity;
        if (count < Capacity)
            count++;
    }

    public static string Write(string dir, Session session, ObjectRegistry registry, SharedValueSync shared)
    {
        Directory.CreateDirectory(dir);
        var role = session == null ? "offline" : session.IsHost ? "host" : "client";
        var file = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + role + "-"
            + System.Diagnostics.Process.GetCurrentProcess().Id + ".txt");
        using var w = new StreamWriter(file, false, new UTF8Encoding(false));

        w.WriteLine("# role=" + role + " level=" + Application.loadedLevelName
            + (session == null ? "" : " slot=" + session.LocalSlot + " state=" + session.State + " hostTime=" + session.HostTime.ToString("0.000")));
        w.WriteLine("# objects=" + registry.Objects.Count + " hash=" + registry.Hash.ToString("x8")
            + " fsms=" + registry.Fsms.Count + " fsmHash=" + registry.FsmHash.ToString("x8"));

        foreach (var o in registry.Objects)
        {
            var go = o.Go;
            if (go == null)
            {
                w.WriteLine("O\t" + o.Id.ToString("x8") + "\t" + o.Key + "\tdestroyed");
                continue;
            }
            var body = go.GetComponent<Rigidbody>();
            var bodyState = body == null ? "none" : body.isKinematic ? "kin" : body.IsSleeping() ? "sleep" : "dyn";
            var t = go.transform;
            w.WriteLine("O\t" + o.Id.ToString("x8") + "\t" + o.Key
                + "\towner=" + o.Owner.Owner + "\tseq=" + o.Owner.Seq + "\theld=" + o.Owner.Held
                + "\tpending=" + o.Pending + "\tproxy=" + o.Proxy + "\tbody=" + bodyState
                + "\tactive=" + go.activeInHierarchy
                + "\tpos=" + Vec(t.position) + "\trot=" + Vec(t.eulerAngles)
                + "\tparent=" + (t.parent == null ? "" : PathOf(t.parent)));
        }

        foreach (var fsm in registry.Fsms)
        {
            if (fsm == null || !registry.TryGetFsmId(fsm, out var id))
                continue;
            w.WriteLine("F\t" + id.ToString("x8") + "\t" + registry.FsmKey(fsm) + "\tstate=" + fsm.ActiveStateName
                + "\tactive=" + fsm.gameObject.activeInHierarchy);
        }

        shared?.Dump(w);

        w.WriteLine("# log (" + (Logging ? "on" : "off") + ")");
        var start = (next - count + Capacity) % Capacity;
        for (var i = 0; i < count; i++)
            w.WriteLine("L\t" + lines[(start + i) % Capacity]);
        return file;
    }

    static string Vec(Vector3 v) => v.x.ToString("0.000") + "," + v.y.ToString("0.000") + "," + v.z.ToString("0.000");

    static string PathOf(Transform t)
    {
        var path = t.name;
        for (var p = t.parent; p != null; p = p.parent)
            path = p.name + "/" + path;
        return path;
    }
}
