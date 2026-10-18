using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace MSCCoop.Recon;

// Shortcuts for setting up co-op test scenarios alone. Everything here goes through the same
// game state a player would change, so the co-op mod syncs it like any other change.
class TestCommand(string marksFile) : ConsoleCommand
{
    static readonly string[] Needs = { "PlayerHunger", "PlayerThirst", "PlayerFatigue", "PlayerStress", "PlayerUrine", "PlayerDirtiness" };
    static readonly string[] Keys = { "PlayerKeyFerndale", "PlayerKeyGifu", "PlayerKeyHayosiko", "PlayerKeyHome", "PlayerKeyRuscko", "PlayerKeySatsuma" };

    public override string Name => "test";

    public override string Help =>
        "test money AMOUNT | test needs | test keys | test tp VEHICLE|player|MARK | test bring VEHICLE | " +
        "test mark NAME | test marks";

    public override void Run(string[] rawArgs)
    {
        var args = Array.FindAll(rawArgs, a => a.Length > 0);
        var verb = args.Length > 0 ? args[0] : "";
        var target = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : "";
        switch (verb)
        {
            case "money" when float.TryParse(target, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount):
                var money = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerMoney");
                money.Value += amount;
                ModConsole.Print("money: " + money.Value);
                break;
            case "keys":
                foreach (var key in Keys)
                    PlayMakerGlobals.Instance.Variables.FindFsmInt(key).Value = 1;
                ModConsole.Print("all keys given");
                break;
            case "needs":
                foreach (var name in Needs)
                    PlayMakerGlobals.Instance.Variables.FindFsmFloat(name).Value = 0;
                ModConsole.Print("needs reset");
                break;
            case "tp" when target.Length > 0:
                Teleport(target);
                break;
            case "bring" when target.Length > 0:
                Bring(target);
                break;
            case "mark" when target.Length > 0:
                Mark(target);
                break;
            case "marks":
                foreach (var mark in ReadMarks())
                    ModConsole.Print(mark.Key + " " + mark.Value);
                break;
            default:
                ModConsole.Print(Help);
                break;
        }
    }

    void Teleport(string target)
    {
        var player = GameObject.Find("PLAYER");
        if (player == null || PlayMakerGlobals.Instance.Variables.FindFsmBool("PlayerSeated").Value)
        {
            ModConsole.Error("test: get out of the vehicle first");
            return;
        }
        Vector3 destination;
        if (ReadMarks().TryGetValue(target, out var marked))
            destination = marked;
        else
        {
            var root = FindRoot(target == "player" ? "MSCCoop Player" : target);
            if (root == null)
                return;
            destination = root.position + root.right * 2.5f + Vector3.up;
        }
        player.transform.position = destination;
        ModConsole.Print("teleported to " + destination);
    }

    static void Bring(string target)
    {
        var root = FindRoot(target);
        var player = GameObject.Find("PLAYER");
        if (root == null || player == null)
            return;
        var look = player.transform.forward;
        var forward = new Vector3(look.x, 0, look.z).normalized;
        // Dropped from above, so it lands moving and the co-op mod claims and streams it.
        var position = player.transform.position + forward * 5 + Vector3.up * 1.5f;
        var rotation = Quaternion.LookRotation(Vector3.Cross(forward, Vector3.up));
        var body = root.GetComponent<Rigidbody>();
        if (body != null)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        root.position = position;
        root.rotation = rotation;
        ModConsole.Print("brought " + root.name);
    }

    void Mark(string name)
    {
        var player = GameObject.Find("PLAYER");
        if (player == null || name == "player" || name.IndexOf(' ') >= 0)
        {
            ModConsole.Error("test: marks are single words and only exist in game");
            return;
        }
        var marks = ReadMarks();
        marks[name] = player.transform.position;
        var lines = new List<string>();
        foreach (var mark in marks)
        {
            var p = mark.Value;
            lines.Add(mark.Key + " " + F(p.x) + " " + F(p.y) + " " + F(p.z));
        }
        File.WriteAllText(marksFile, string.Join("\n", lines.ToArray()) + "\n");
        ModConsole.Print("marked " + name + " at " + player.transform.position);
    }

    static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    Dictionary<string, Vector3> ReadMarks()
    {
        var marks = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(marksFile))
            return marks;
        foreach (var line in File.ReadAllLines(marksFile))
        {
            var f = line.Split(' ');
            if (f.Length == 4 && TryFloat(f[1], out var x) && TryFloat(f[2], out var y) && TryFloat(f[3], out var z))
                marks[f[0]] = new Vector3(x, y, z);
        }
        return marks;
    }

    static bool TryFloat(string s, out float v) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    // Vehicles are scene roots named like "SATSUMA(557kg, 248)", so a prefix is enough to pick one.
    static Transform FindRoot(string prefix)
    {
        Transform found = null;
        foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
        {
            if (t.parent != null || !t.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (found != null)
            {
                ModConsole.Error("test: \"" + prefix + "\" matches both " + found.name + " and " + t.name);
                return null;
            }
            found = t;
        }
        if (found == null)
            ModConsole.Error("test: nothing named " + prefix);
        return found;
    }
}
