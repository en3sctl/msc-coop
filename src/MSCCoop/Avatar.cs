using MSCCoop.Net;
using MSCLoader;
using UnityEngine;

namespace MSCCoop;

// A remote player is drawn with a copy of one of the town's walking NPCs, stripped of everything
// but its mesh and skeleton so the copy runs no game logic (crime triggers, look-at, ragdoll).
sealed class Avatar
{
    const string StandClip = "fat_standing";
    const string WalkClip = "fat_walk";
    const float WalkClipSpeed = 1.3f;
    const float WalkThreshold = 0.25f;
    const float CrossFade = 0.2f;
    const float NameTagHeight = 2.05f;

    static readonly string[] Looks = { "Kale", "Julli", "Kristian", "Unto" };
    static bool clipsLogged;

    readonly GameObject root;
    readonly Animation animation;
    readonly Transform nameTag;
    readonly string sitClip;
    string playing;

    Avatar(GameObject root, Animation animation, Transform nameTag, string sitClip)
    {
        this.root = root;
        this.animation = animation;
        this.nameTag = nameTag;
        this.sitClip = sitClip;
    }

    public static Avatar Create(byte slot, string name)
    {
        var walkers = GameObject.Find("HUMANS/Randomizer/Walkers");
        var template = walkers == null ? null : walkers.transform.Find(Looks[slot % Looks.Length] + "/Pivot/Char");
        if (template == null)
        {
            ModConsole.Error("Co-op: could not find an NPC model to use for " + name + ".");
            return null;
        }

        var copy = (GameObject)Object.Instantiate(template.gameObject);
        copy.name = "MSCCoop Player " + slot;
        foreach (var joint in copy.GetComponentsInChildren<Joint>(true))
            Object.DestroyImmediate(joint);
        foreach (var behaviour in copy.GetComponentsInChildren<MonoBehaviour>(true))
            Object.DestroyImmediate(behaviour);
        foreach (var collider in copy.GetComponentsInChildren<Collider>(true))
            Object.DestroyImmediate(collider);
        foreach (var body in copy.GetComponentsInChildren<Rigidbody>(true))
            Object.DestroyImmediate(body);
        copy.SetActive(true);

        var animation = copy.GetComponentInChildren<Animation>();
        string sitClip = null;
        foreach (AnimationState state in animation)
        {
            if (sitClip == null && state.name.Contains("_sit"))
                sitClip = state.name;
        }
        if (!clipsLogged)
        {
            clipsLogged = true;
            var names = "";
            foreach (AnimationState state in animation)
                names += state.name + " ";
            ModConsole.Print("Co-op: avatar clips: " + names);
        }

        return new Avatar(copy, animation, CreateNameTag(copy.transform, name), sitClip);
    }

    public void Apply(Vector3 position, Quaternion rotation, Vector3 velocity, PlayerFlags flags, Transform camera)
    {
        root.transform.position = position;
        root.transform.rotation = rotation;
        if (camera != null)
            nameTag.rotation = camera.rotation;

        var speed = new Vector2(velocity.x, velocity.z).magnitude;
        if ((flags & PlayerFlags.InVehicle) != 0 && sitClip != null)
            Play(sitClip);
        else if (speed > WalkThreshold)
        {
            Play(WalkClip);
            animation[WalkClip].speed = speed / WalkClipSpeed;
        }
        else
            Play(StandClip);
    }

    public void Destroy() => Object.Destroy(root);

    void Play(string clip)
    {
        if (clip == playing || animation[clip] == null)
            return;
        playing = clip;
        animation.CrossFade(clip, CrossFade);
    }

    static Transform CreateNameTag(Transform parent, string name)
    {
        var go = new GameObject("NameTag");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0, NameTagHeight, 0);
        var text = go.AddComponent<TextMesh>();
        text.font = (Font)Resources.GetBuiltinResource(typeof(Font), "Arial.ttf");
        text.text = name;
        text.fontSize = 64;
        text.characterSize = 0.02f;
        text.anchor = TextAnchor.LowerCenter;
        text.alignment = TextAlignment.Center;
        text.color = Color.white;
        go.GetComponent<MeshRenderer>().material = text.font.material;
        return go.transform;
    }
}
