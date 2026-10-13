using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using Newtonsoft.Json;
using UnityEngine;

namespace MSCCoop.Recon;

struct Node(Transform transform, string path, string rootKind)
{
    public readonly Transform Transform = transform;
    public readonly string Path = path;
    public readonly string RootKind = rootKind;
}

static class SceneDump
{
    static readonly Encoding Utf8 = new UTF8Encoding(false);

    public static int WriteIds(string file)
    {
        var saveTags = TransformSaveTags();
        var lines = new List<string>();
        var seen = new Dictionary<string, int>();
        foreach (var node in CollectNodes())
        {
            if (node.RootKind == "asset")
                continue;
            var go = node.Transform.gameObject;
            var hasSaveTag = saveTags.TryGetValue(node.Transform, out var saveTag);
            if (!hasSaveTag && go.GetComponent<Rigidbody>() == null && go.GetComponent<PlayMakerFSM>() == null)
                continue;

            string key;
            if (hasSaveTag)
                key = "save:" + saveTag;
            else if (UseId(go) is { } useId)
                key = "item:" + useId;
            else
                key = "path:" + node.Path;
            seen.TryGetValue(key, out var count);
            seen[key] = count + 1;
            lines.Add(key + "\t" + node.Path);
        }

        lines.Sort(StringComparer.Ordinal);
        var collisions = 0;
        using var w = new StreamWriter(file, false, Utf8);
        foreach (var line in lines)
        {
            var key = line.Substring(0, line.IndexOf('\t'));
            var dup = seen[key] > 1;
            if (dup)
                collisions++;
            w.WriteLine(dup ? line + "\tCOLLISION" : line);
        }
        return collisions;
    }

    public static void WriteAll(string dir)
    {
        var nodes = CollectNodes();
        var paths = new Dictionary<Transform, string>(nodes.Count);
        foreach (var node in nodes)
            paths[node.Transform] = node.Path;
        var values = new ValueWriter(paths);

        var actionTypes = new Dictionary<string, int>();
        var fsmNames = new Dictionary<string, int>();
        int fsmCount = 0, stateCount = 0, actionCount = 0, failed = 0;

        using (var objects = new StreamWriter(Path.Combine(dir, "objects.jsonl"), false, Utf8))
        using (var fsms = new StreamWriter(Path.Combine(dir, "fsms.jsonl"), false, Utf8))
        {
            foreach (var node in nodes)
            {
                var go = node.Transform.gameObject;
                WriteLine(objects, w => WriteObject(w, node, go));

                foreach (var fsm in go.GetComponents<PlayMakerFSM>())
                {
                    fsmCount++;
                    Increment(fsmNames, fsm.FsmName);
                    try
                    {
                        WriteLine(fsms, w => WriteFsm(w, node.Path, fsm, values, actionTypes, ref stateCount, ref actionCount));
                    }
                    catch (Exception e)
                    {
                        failed++;
                        WriteLine(fsms, w =>
                        {
                            w.WriteStartObject();
                            w.WritePropertyName("path"); w.WriteValue(node.Path);
                            w.WritePropertyName("name"); w.WriteValue(fsm.FsmName);
                            w.WritePropertyName("error"); w.WriteValue(e.ToString());
                            w.WriteEndObject();
                        });
                    }
                }
            }
        }

        using (var globals = new StreamWriter(Path.Combine(dir, "globals.json"), false, Utf8))
            WriteLine(globals, w => WriteGlobals(w, values));

        using (var summary = new StreamWriter(Path.Combine(dir, "summary.txt"), false, Utf8))
        {
            summary.WriteLine("game objects: " + nodes.Count);
            summary.WriteLine("fsms: " + fsmCount + " (failed: " + failed + ")");
            summary.WriteLine("states: " + stateCount);
            summary.WriteLine("actions: " + actionCount);
            WriteHistogram(summary, "action types", actionTypes);
            WriteHistogram(summary, "fsm names", fsmNames);
        }

        WriteIds(Path.Combine(dir, "ids.tsv"));
    }

    static List<Node> CollectNodes()
    {
        var roots = new List<Transform>();
        foreach (var o in Resources.FindObjectsOfTypeAll(typeof(Transform)))
        {
            var t = (Transform)o;
            if (t.parent == null && t.gameObject.hideFlags == HideFlags.None)
                roots.Add(t);
        }
        roots.Sort((a, b) =>
        {
            var c = a.GetSiblingIndex().CompareTo(b.GetSiblingIndex());
            return c != 0 ? c : string.CompareOrdinal(a.name, b.name);
        });

        var nodes = new List<Node>(roots.Count * 32);
        AddLevel(roots, "", null, nodes);
        return nodes;
    }

    // Same-named siblings get a [n] suffix in sibling order, so the path stays readable and unique.
    static void AddLevel(List<Transform> level, string parentPath, string rootKind, List<Node> nodes)
    {
        var nameCounts = new Dictionary<string, int>();
        foreach (var t in level)
        {
            nameCounts.TryGetValue(t.name, out var n);
            nameCounts[t.name] = n + 1;
            var segment = n == 0 ? t.name : t.name + "[" + n + "]";
            var path = parentPath.Length == 0 ? segment : parentPath + "/" + segment;
            var kind = rootKind ?? ClassifyRoot(t.gameObject);
            nodes.Add(new Node(t, path, kind));

            if (t.childCount == 0)
                continue;
            var children = new List<Transform>(t.childCount);
            for (var i = 0; i < t.childCount; i++)
                children.Add(t.GetChild(i));
            AddLevel(children, path, kind, nodes);
        }
    }

    // Unity 5.0 has no GameObject.scene. A root that is active but not active in hierarchy can only be an asset.
    static string ClassifyRoot(GameObject go)
    {
        if (!go.activeSelf)
            return "inactive";
        return go.activeInHierarchy ? "scene" : "asset";
    }

    // The ES2 tag an object's transform is saved under is unique by construction, otherwise the game's own saves would collide.
    static Dictionary<Transform, string> TransformSaveTags()
    {
        var tags = new Dictionary<Transform, string>();
        foreach (PlayMakerFSM component in Resources.FindObjectsOfTypeAll(typeof(PlayMakerFSM)))
        {
            if (component.gameObject.hideFlags != HideFlags.None)
                continue;
            var fsm = component.Fsm;
            foreach (var state in fsm.States)
            {
                foreach (var action in LoadActions(fsm, state))
                {
                    if (action is not LoadTransform load || string.IsNullOrEmpty(load.uniqueTag.Value))
                        continue;
                    var target = load.loadValue.OwnerOption == OwnerDefaultOption.UseOwner
                        ? component.gameObject
                        : load.loadValue.GameObject.Value;
                    if (target != null)
                        tags[target.transform] = load.uniqueTag.Value;
                }
            }
        }
        return tags;
    }

    static string UseId(GameObject go)
    {
        foreach (var fsm in go.GetComponents<PlayMakerFSM>())
        {
            if (fsm.FsmName != "Use")
                continue;
            var id = fsm.FsmVariables.FindFsmString("ID");
            if (id != null && !string.IsNullOrEmpty(id.Value))
                return id.Value;
        }
        return null;
    }

    static void WriteObject(JsonWriter w, Node node, GameObject go)
    {
        w.WriteStartObject();
        w.WritePropertyName("path"); w.WriteValue(node.Path);
        w.WritePropertyName("root"); w.WriteValue(node.RootKind);
        w.WritePropertyName("active"); w.WriteValue(go.activeInHierarchy);
        w.WritePropertyName("self"); w.WriteValue(go.activeSelf);
        w.WritePropertyName("layer"); w.WriteValue(LayerMask.LayerToName(go.layer));
        w.WritePropertyName("tag"); w.WriteValue(go.tag);

        w.WritePropertyName("c");
        w.WriteStartArray();
        foreach (var c in go.GetComponents<Component>())
            w.WriteValue(c == null ? "<missing>" : c.GetType().FullName);
        w.WriteEndArray();

        var rb = go.GetComponent<Rigidbody>();
        if (rb != null)
        {
            w.WritePropertyName("rb");
            w.WriteStartObject();
            w.WritePropertyName("mass"); w.WriteValue(rb.mass);
            w.WritePropertyName("kinematic"); w.WriteValue(rb.isKinematic);
            w.WritePropertyName("gravity"); w.WriteValue(rb.useGravity);
            w.WritePropertyName("interp"); w.WriteValue(rb.interpolation.ToString());
            w.WritePropertyName("cd"); w.WriteValue(rb.collisionDetectionMode.ToString());
            w.WritePropertyName("drag"); w.WriteValue(rb.drag);
            w.WritePropertyName("adrag"); w.WriteValue(rb.angularDrag);
            w.WritePropertyName("sleeping"); w.WriteValue(rb.IsSleeping());
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    static void WriteFsm(JsonWriter w, string path, PlayMakerFSM component, ValueWriter values,
        Dictionary<string, int> actionTypes, ref int stateCount, ref int actionCount)
    {
        var fsm = component.Fsm;
        w.WriteStartObject();
        w.WritePropertyName("path"); w.WriteValue(path);
        w.WritePropertyName("name"); w.WriteValue(component.FsmName);
        w.WritePropertyName("enabled"); w.WriteValue(component.enabled);
        w.WritePropertyName("initialized"); w.WriteValue(fsm.Initialized);
        w.WritePropertyName("activeState"); w.WriteValue(fsm.ActiveStateName);
        w.WritePropertyName("startState"); w.WriteValue(fsm.StartState);
        w.WritePropertyName("restartOnEnable"); w.WriteValue(fsm.RestartOnEnable);
        if (component.FsmTemplate != null)
        {
            w.WritePropertyName("template"); w.WriteValue(component.FsmTemplate.name);
        }

        w.WritePropertyName("vars");
        WriteVariables(w, fsm.Variables, values);

        w.WritePropertyName("events");
        w.WriteStartArray();
        foreach (var e in fsm.Events)
            w.WriteValue(e?.Name);
        w.WriteEndArray();

        w.WritePropertyName("global");
        WriteTransitions(w, fsm.GlobalTransitions);

        w.WritePropertyName("states");
        w.WriteStartArray();
        foreach (var state in fsm.States)
        {
            stateCount++;
            w.WriteStartObject();
            w.WritePropertyName("n"); w.WriteValue(state.Name);
            w.WritePropertyName("t");
            WriteTransitions(w, state.Transitions);

            w.WritePropertyName("a");
            w.WriteStartArray();
            foreach (var action in LoadActions(fsm, state))
            {
                actionCount++;
                w.WriteStartObject();
                if (action == null)
                {
                    w.WritePropertyName("type"); w.WriteValue("<missing>");
                    w.WriteEndObject();
                    continue;
                }
                var typeName = action.GetType().Name;
                Increment(actionTypes, typeName);
                w.WritePropertyName("type"); w.WriteValue(typeName);
                w.WritePropertyName("on"); w.WriteValue(action.Enabled);
                w.WritePropertyName("f");
                values.WriteFields(w, action);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    // Reading state.Actions on an FSM that never woke up would cache actions built without an owner,
    // so unloaded states are deserialized from a detached copy instead.
    static FsmStateAction[] LoadActions(Fsm fsm, FsmState state)
    {
        if (state.ActionsLoaded)
            return state.Actions;
        var copy = new FsmState(fsm) { Name = state.Name };
        copy.CopyActionData(state);
        return copy.ActionData.LoadActions(copy);
    }

    static void WriteTransitions(JsonWriter w, FsmTransition[] transitions)
    {
        w.WriteStartArray();
        foreach (var t in transitions)
        {
            w.WriteStartObject();
            w.WritePropertyName("e"); w.WriteValue(t.EventName);
            w.WritePropertyName("to"); w.WriteValue(t.ToState);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    static void WriteVariables(JsonWriter w, FsmVariables variables, ValueWriter values)
    {
        w.WriteStartArray();
        foreach (var v in variables.GetAllNamedVariables())
        {
            w.WriteStartObject();
            w.WritePropertyName("t"); w.WriteValue(v.GetType().Name);
            w.WritePropertyName("n"); w.WriteValue(v.Name);
            w.WritePropertyName("v");
            values.WriteVariableValue(w, v);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    static void WriteGlobals(JsonWriter w, ValueWriter values)
    {
        var globals = PlayMakerGlobals.Instance;
        w.WriteStartObject();
        w.WritePropertyName("vars");
        WriteVariables(w, globals.Variables, values);
        w.WritePropertyName("events");
        w.WriteStartArray();
        foreach (var e in globals.Events)
            w.WriteValue(e);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static void WriteLine(StreamWriter stream, Action<JsonWriter> write)
    {
        using (var w = new JsonTextWriter(stream) { CloseOutput = false, Formatting = Formatting.None })
            write(w);
        stream.Write('\n');
    }

    static void Increment(Dictionary<string, int> counts, string key)
    {
        key ??= "<null>";
        counts.TryGetValue(key, out var n);
        counts[key] = n + 1;
    }

    static void WriteHistogram(StreamWriter w, string title, Dictionary<string, int> counts)
    {
        var entries = new List<KeyValuePair<string, int>>(counts);
        entries.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
        w.WriteLine();
        w.WriteLine(title + " (" + entries.Count + "):");
        foreach (var e in entries)
            w.WriteLine(e.Value.ToString().PadLeft(8) + "  " + e.Key);
    }
}
