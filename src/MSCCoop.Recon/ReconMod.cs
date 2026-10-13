using System;
using System.IO;
using MSCLoader;
using UnityEngine;

namespace MSCCoop.Recon;

public class ReconMod : Mod
{
    public override string ID => "MSCCoopRecon";
    public override string Name => "MSC Co-op Recon";
    public override string Version => "0.1.0";
    public override string Author => "MSC Co-op contributors";
    public override string Description => "Developer tool. Dumps the scene hierarchy and every PlayMaker FSM to disk.";

    bool initialized;

    public override void ModSetup()
    {
        SetupFunction(Setup.OnMenuLoad, Init);
        SetupFunction(Setup.PreLoad, () => WriteIds("preload"));
        SetupFunction(Setup.PostLoad, () => WriteIds("postload"));
    }

    void Init()
    {
        if (initialized)
            return;
        ConsoleCommand.Add(new ReconCommand(this));
        var watcher = new GameObject("MSCCoopRecon").AddComponent<LevelWatcher>();
        watcher.Mod = this;
        UnityEngine.Object.DontDestroyOnLoad(watcher.gameObject);
        initialized = true;
    }

    internal string NewOutputDir(string label)
    {
        var dir = Path.Combine(ModLoader.GetModSettingsFolder(this), label + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal void WriteIds(string label)
    {
        var dir = NewOutputDir("ids-" + label);
        var collisions = SceneDump.WriteIds(Path.Combine(dir, "ids.tsv"));
        ModConsole.Print("Recon: ids written to " + dir + " (" + collisions + " collisions)");
    }

    internal void WriteFullDump()
    {
        var dir = NewOutputDir("dump");
        var start = DateTime.Now;
        SceneDump.WriteAll(dir);
        ModConsole.Print("Recon: full dump written to " + dir + " in " + (DateTime.Now - start).TotalSeconds.ToString("0.0") + " s");
    }
}

// OnLevelWasLoaded runs after every scene object's Awake but before any Start, so no FSM has reparented anything yet.
class LevelWatcher : MonoBehaviour
{
    public ReconMod Mod;

    void OnLevelWasLoaded(int level)
    {
        if (Application.loadedLevelName == "GAME")
            Mod.WriteIds("awake");
    }
}

class ReconCommand(ReconMod mod) : ConsoleCommand
{
    public override string Name => "recon";
    public override string Help => "recon dump | recon ids [label]";

    public override void Run(string[] args)
    {
        var verb = args.Length > 0 ? args[0] : "";
        try
        {
            switch (verb)
            {
                case "dump":
                    mod.WriteFullDump();
                    break;
                case "ids":
                    mod.WriteIds(args.Length > 1 ? args[1] : "manual");
                    break;
                default:
                    ModConsole.Print(Help);
                    break;
            }
        }
        catch (Exception e)
        {
            ModConsole.Error("Recon failed: " + e);
        }
    }
}
