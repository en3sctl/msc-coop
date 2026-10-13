using System;
using System.Collections.Generic;

namespace MSCCoop.Net;

public sealed class SaveFile(string name, byte[] data)
{
    public readonly string Name = name;
    public readonly byte[] Data = data;
}

public static class SaveTransfer
{
    public const int ChunkBytes = 12 * 1024;
    public const int MaxFileBytes = 16 * 1024 * 1024;
    public const int MaxFiles = 32;
    public const int MaxNameChars = 64;

    public static void Send(Session session, Connection to, List<SaveFile> files)
    {
        foreach (var file in files)
        {
            var offset = 0;
            do
            {
                var count = Math.Min(ChunkBytes, file.Data.Length - offset);
                var w = session.BeginMessage(MessageType.SaveChunk);
                w.WriteString(file.Name, MaxNameChars);
                w.WriteUInt((uint)file.Data.Length);
                w.WriteUInt((uint)offset);
                w.WriteUShort((ushort)count);
                w.WriteBytes(file.Data, offset, count);
                session.Send(to, Channel.Bulk);
                offset += count;
            }
            while (offset < file.Data.Length);
        }

        var done = session.BeginMessage(MessageType.SaveComplete);
        done.WriteByte((byte)files.Count);
        done.WriteUInt(Hash(files));
        session.Send(to, Channel.Bulk);
    }

    // Names come from the network and end up as file paths on the client.
    public static bool IsSafeName(string name)
    {
        if (name.Length < 5 || !name.EndsWith(".txt", StringComparison.Ordinal) || name.StartsWith(".", StringComparison.Ordinal))
            return false;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_' || c == '-' || c == '.' || c == ' '))
                return false;
        }
        return name.IndexOf("..", StringComparison.Ordinal) < 0;
    }

    public static uint Hash(List<SaveFile> files)
    {
        var sorted = new List<SaveFile>(files);
        sorted.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        var hash = Fnv.Offset32;
        foreach (var file in sorted)
        {
            for (var i = 0; i < file.Name.Length; i++)
            {
                hash ^= file.Name[i];
                hash *= 16777619;
            }
            hash = Fnv.Hash32(file.Data, 0, file.Data.Length, hash);
        }
        return hash;
    }
}

public sealed class SaveReceiver
{
    readonly Dictionary<string, byte[]> files = new();
    readonly Dictionary<string, int> received = new();

    public void Reset()
    {
        files.Clear();
        received.Clear();
    }

    public bool HandleChunk(BitReader r)
    {
        var name = r.ReadString(SaveTransfer.MaxNameChars);
        var total = r.ReadUInt();
        var offset = r.ReadUInt();
        var count = r.ReadUShort();
        if (r.Overflow || !SaveTransfer.IsSafeName(name) || total > SaveTransfer.MaxFileBytes || count > SaveTransfer.ChunkBytes
            || offset + count > total)
            return false;

        if (!files.TryGetValue(name, out var data))
        {
            if (files.Count >= SaveTransfer.MaxFiles)
                return false;
            data = new byte[total];
            files[name] = data;
            received[name] = 0;
        }
        if (data.Length != total)
            return false;

        r.ReadBytes(data, (int)offset, count);
        received[name] += count;
        return !r.Overflow;
    }

    public bool Complete(BitReader r, out List<SaveFile> result)
    {
        result = null;
        var count = r.ReadByte();
        var hash = r.ReadUInt();
        if (r.Overflow || count != files.Count)
            return false;

        var list = new List<SaveFile>(files.Count);
        foreach (var pair in files)
        {
            if (received[pair.Key] != pair.Value.Length)
                return false;
            list.Add(new SaveFile(pair.Key, pair.Value));
        }
        if (SaveTransfer.Hash(list) != hash)
            return false;
        result = list;
        return true;
    }
}
