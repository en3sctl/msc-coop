using System.Collections.Generic;
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
    const string SkeletonName = "skeleton";

    // Traffic drivers share the walkers' rig but have no animation: their skeleton is posed seated.
    const string SeatedTemplate = "VehiclesHighway/FITTAN/LOD/Passengers/Driver/skeleton";

    static readonly string[] Looks = { "Kale", "Julli", "Kristian", "Unto" };

    readonly GameObject root;
    readonly Animation animation;
    readonly Transform nameTag;
    readonly Transform[] bones;
    readonly Pose[] standingPose;
    readonly Pose[] seatedPose;
    string playing;
    bool seated;

    struct Pose
    {
        public bool Valid;
        public Vector3 Position;
        public Quaternion Rotation;
    }

    Avatar(GameObject root, Animation animation, Transform nameTag, Transform[] bones, Pose[] standingPose, Pose[] seatedPose)
    {
        this.root = root;
        this.animation = animation;
        this.nameTag = nameTag;
        this.bones = bones;
        this.standingPose = standingPose;
        this.seatedPose = seatedPose;
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

        var skeleton = copy.transform.Find(SkeletonName);
        var bones = skeleton.GetComponentsInChildren<Transform>(true);
        var standing = new Pose[bones.Length];
        for (var i = 0; i < bones.Length; i++)
            standing[i] = new Pose { Valid = true, Position = bones[i].localPosition, Rotation = bones[i].localRotation };

        return new Avatar(copy, copy.GetComponentInChildren<Animation>(), CreateNameTag(copy.transform, name),
            bones, standing, CaptureSeatedPose(skeleton, bones));
    }

    public void Apply(Vector3 position, Quaternion rotation, Vector3 velocity, PlayerFlags flags, Transform camera)
    {
        root.transform.position = position;
        root.transform.rotation = rotation;
        if (camera != null)
            nameTag.rotation = camera.rotation;

        if ((flags & PlayerFlags.InVehicle) != 0 && seatedPose != null)
        {
            Sit();
            return;
        }
        if (seated)
            Stand();

        var speed = new Vector2(velocity.x, velocity.z).magnitude;
        if (speed > WalkThreshold)
        {
            Play(WalkClip);
            animation[WalkClip].speed = speed / WalkClipSpeed;
        }
        else
            Play(StandClip);
    }

    public void Destroy() => Object.Destroy(root);

    void Sit()
    {
        if (seated)
            return;
        seated = true;
        animation.Stop();
        playing = null;
        SetPose(seatedPose);
    }

    void Stand()
    {
        seated = false;
        SetPose(standingPose);
    }

    void SetPose(Pose[] pose)
    {
        for (var i = 0; i < bones.Length; i++)
        {
            if (!pose[i].Valid)
                continue;
            bones[i].localPosition = pose[i].Position;
            bones[i].localRotation = pose[i].Rotation;
        }
    }

    void Play(string clip)
    {
        if (clip == playing || animation[clip] == null)
            return;
        playing = clip;
        animation.CrossFade(clip, CrossFade);
    }

    static Pose[] CaptureSeatedPose(Transform skeleton, Transform[] bones)
    {
        var traffic = GameObject.Find("TRAFFIC");
        var source = traffic == null ? null : traffic.transform.Find(SeatedTemplate);
        if (source == null)
        {
            ModConsole.Warning("Co-op: no seated driver found, remote players will stand in vehicles.");
            return null;
        }

        var byPath = new Dictionary<string, Transform>();
        foreach (var t in source.GetComponentsInChildren<Transform>(true))
            byPath[RelativePath(source, t)] = t;

        var pose = new Pose[bones.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            if (byPath.TryGetValue(RelativePath(skeleton, bones[i]), out var match))
                pose[i] = new Pose { Valid = true, Position = match.localPosition, Rotation = match.localRotation };
        }
        return pose;
    }

    static string RelativePath(Transform root, Transform t)
    {
        var path = "";
        for (var current = t; current != root; current = current.parent)
            path = current.name + "/" + path;
        return path;
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
        text.characterSize = 0.01f;
        text.anchor = TextAnchor.LowerCenter;
        text.alignment = TextAlignment.Center;
        text.color = Color.white;
        go.GetComponent<MeshRenderer>().material = text.font.material;
        return go.transform;
    }
}
