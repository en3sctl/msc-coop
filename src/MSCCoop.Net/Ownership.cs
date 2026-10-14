using System.Collections.Generic;

namespace MSCCoop.Net;

public struct OwnerInfo
{
    public byte Owner;
    public ushort Seq;
    public bool Held;

    public bool IsOwned => Owner != Ownership.NoOwner;
}

// Host-side arbiter. Holding an object is exclusive; simulating a moving object is not, so a
// pickup may take an object away from whoever is only simulating it, but nobody can take a held one.
public sealed class Ownership
{
    public const byte NoOwner = 7;

    readonly Dictionary<uint, OwnerInfo> entries = new();

    public OwnerInfo Get(uint id) =>
        entries.TryGetValue(id, out var info) ? info : new OwnerInfo { Owner = NoOwner };

    public bool TryClaim(uint id, byte slot, bool held, out OwnerInfo result)
    {
        var current = Get(id);
        if (current.Owner != slot && current.IsOwned && (current.Held || !held))
        {
            result = current;
            return false;
        }
        result = new OwnerInfo { Owner = slot, Seq = (ushort)(current.Seq + 1), Held = held };
        entries[id] = result;
        return true;
    }

    public bool Release(uint id, byte slot, out OwnerInfo result)
    {
        var current = Get(id);
        if (current.Owner != slot)
        {
            result = current;
            return false;
        }
        result = new OwnerInfo { Owner = NoOwner, Seq = (ushort)(current.Seq + 1) };
        entries[id] = result;
        return true;
    }

    public void ReleaseAll(byte slot, List<KeyValuePair<uint, OwnerInfo>> changed)
    {
        var ids = new List<uint>();
        foreach (var pair in entries)
        {
            if (pair.Value.Owner == slot)
                ids.Add(pair.Key);
        }
        foreach (var id in ids)
        {
            Release(id, slot, out var info);
            changed.Add(new KeyValuePair<uint, OwnerInfo>(id, info));
        }
    }

    public void Clear() => entries.Clear();
}
