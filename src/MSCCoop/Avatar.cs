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
    const float NameTagAboveEyes = 0.4f;
    const string SkeletonName = "skeleton";
    const float NameTagReferenceDistance = 8f;
    const float LookSmoothing = 12f;
    const float ChestShareOfPitch = 0.4f;
    const float CrouchDepth = 0.42f;
    const float CrouchLean = 20f;
    const float CrouchSpeed = 6f;

    // Traffic drivers share the walkers' rig but have no animation: their skeleton is posed seated.
    const string SeatedTemplate = "VehiclesHighway/FITTAN/LOD/Passengers/Driver/skeleton";

    static readonly string[] Looks = { "Kale", "Julli", "Kristian", "Unto" };

    readonly GameObject root;
    readonly Animation animation;
    readonly Transform nameTag;
    readonly Transform[] bones;
    readonly Pose[] standingPose;
    readonly Pose[] seatedPose;
    readonly Vector3 modelOffset;
    readonly Quaternion modelRotation;
    readonly int head;
    readonly int chest;
    readonly int headPivot = -1;
    readonly int pelvis;
    readonly int[] thighs;
    readonly int[] knees;
    readonly int[] ankles;
    Quaternion facing = Quaternion.identity;
    float pitch;
    float crouch;
    bool crouching;
    string playing;
    bool seated;

    struct Pose
    {
        public bool Valid;
        public Vector3 Position;
        public Quaternion Rotation;
    }

    Avatar(GameObject root, Animation animation, Transform nameTag, Transform[] bones, Pose[] standingPose, Pose[] seatedPose,
        Vector3 modelOffset, Quaternion modelRotation)
    {
        this.modelOffset = modelOffset;
        this.modelRotation = modelRotation;
        this.root = root;
        this.animation = animation;
        this.nameTag = nameTag;
        this.bones = bones;
        this.standingPose = standingPose;
        this.seatedPose = seatedPose;
        // The head's parent is numbered per walker ("HeadPivot 1", "HeadPivot 2"); the head bone is not.
        head = System.Array.FindIndex(bones, b => b.name == "head");
        chest = System.Array.FindIndex(bones, b => b.name == "spine_upper");

        pelvis = Bone("pelvis");
        thighs = new[] { Bone("thig_left"), Bone("thig_right") };
        knees = new[] { Bone("knee_left"), Bone("knee_right") };
        ankles = new[] { Bone("ankle_left"), Bone("ankle_right") };

        // HeadPivot is what the NPC's look-at FSM aims at its target, so it is aimed the same way here.
        if (head >= 0)
        {
            var pivot = System.Array.IndexOf(bones, bones[head].parent);
            if (pivot >= 0 && bones[pivot].name.StartsWith("HeadPivot"))
                headPivot = pivot;
        }
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

        // The walker's root decides where it stands and which way it faces; Char sits under it at an offset.
        var walker = template.parent.parent;
        var inverse = Quaternion.Inverse(walker.rotation);
        var modelOffset = inverse * (template.position - walker.position);
        var modelRotation = inverse * template.rotation;

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
            bones, standing, CaptureSeatedPose(skeleton, bones), modelOffset, modelRotation);
    }

    public void Apply(Vector3 position, Quaternion rotation, Vector3 velocity, PlayerFlags flags, Transform camera)
    {
        var inVehicle = (flags & PlayerFlags.InVehicle) != 0;
        crouching = !inVehicle && (flags & PlayerFlags.Crouching) != 0;
        facing = rotation;
        root.transform.position = position + rotation * modelOffset;
        root.transform.rotation = rotation * modelRotation;
        nameTag.position = position + Vector3.up * (inVehicle ? NameTagAboveEyes : NameTagHeight);
        if (camera != null)
        {
            nameTag.rotation = camera.rotation;
            var scale = Mathf.Clamp(Vector3.Distance(camera.position, nameTag.position) / NameTagReferenceDistance, 0.2f, 2f);
            nameTag.localScale = new Vector3(scale, scale, scale);
        }

        if ((flags & PlayerFlags.InVehicle) != 0 && seatedPose != null)
        {
            Sit();
            ResetPoseBones();
            // In a vehicle the position is the player's eyes, so the head is put there.
            if (inVehicle && head >= 0)
                root.transform.position += position - bones[head].position;
            return;
        }
        if (seated)
            Stand();
        ResetPoseBones();

        var speed = new Vector2(velocity.x, velocity.z).magnitude;
        if (speed > WalkThreshold)
        {
            Play(WalkClip);
            animation[WalkClip].speed = speed / WalkClipSpeed;
        }
        else
            Play(StandClip);
    }

    // Runs after animation has posed the skeleton. ResetPoseBones put every bone touched here back
    // to its pose in Update, so these adjustments cannot pile up on bones a clip does not key.
    public void LateUpdate(float targetPitch, float deltaTime)
    {
        pitch = Mathf.Lerp(pitch, targetPitch, Mathf.Min(1f, deltaTime * LookSmoothing));
        crouch = Mathf.MoveTowards(crouch, crouching && !seated ? 1f : 0f, deltaTime * CrouchSpeed);
        var right = facing * Vector3.right;

        if (crouch > 0)
            Crouch(right);
        if (chest >= 0)
            bones[chest].rotation = Quaternion.AngleAxis(pitch * ChestShareOfPitch, right) * bones[chest].rotation;
        if (headPivot >= 0)
            bones[headPivot].rotation = facing * Quaternion.Euler(pitch, 0, 0);
        else if (head >= 0)
            bones[head].rotation = Quaternion.AngleAxis(pitch * (1 - ChestShareOfPitch), right) * bones[head].rotation;
    }

    // There is no crouch clip for NPCs, so the skeleton is lowered with the feet kept where the
    // animation put them and each leg solved as a two-bone chain bending the knee forward.
    void Crouch(Vector3 right)
    {
        var leftFoot = ankles[0] >= 0 ? bones[ankles[0]].position : Vector3.zero;
        var rightFoot = ankles[1] >= 0 ? bones[ankles[1]].position : Vector3.zero;

        bones[0].position -= Vector3.up * (CrouchDepth * crouch);
        if (pelvis >= 0)
            bones[pelvis].rotation = Quaternion.AngleAxis(CrouchLean * crouch, right) * bones[pelvis].rotation;

        var forward = facing * Vector3.forward;
        for (var i = 0; i < 2; i++)
        {
            if (thighs[i] >= 0 && knees[i] >= 0 && ankles[i] >= 0)
                SolveLeg(bones[thighs[i]], bones[knees[i]], bones[ankles[i]], i == 0 ? leftFoot : rightFoot, forward);
        }
    }

    static void SolveLeg(Transform thigh, Transform knee, Transform ankle, Vector3 target, Vector3 bendHint)
    {
        var hip = thigh.position;
        var upper = Vector3.Distance(hip, knee.position);
        var lower = Vector3.Distance(knee.position, ankle.position);
        var toTarget = target - hip;
        var reach = Mathf.Clamp(toTarget.magnitude, 0.01f, upper + lower - 0.001f);
        var direction = toTarget.normalized;

        var along = (upper * upper - lower * lower + reach * reach) / (2 * reach);
        var side = Mathf.Sqrt(Mathf.Max(upper * upper - along * along, 0));
        var bend = (bendHint - Vector3.Dot(bendHint, direction) * direction).normalized;
        var kneeTarget = hip + direction * along + bend * side;

        thigh.rotation = Quaternion.FromToRotation(knee.position - hip, kneeTarget - hip) * thigh.rotation;
        knee.rotation = Quaternion.FromToRotation(ankle.position - knee.position, target - knee.position) * knee.rotation;
    }

    public void Destroy() => Object.Destroy(root);

    void ResetPoseBones()
    {
        var pose = seated ? seatedPose : standingPose;
        if (pose[0].Valid)
            bones[0].localPosition = pose[0].Position;
        ResetBone(head);
        ResetBone(chest);
        ResetBone(pelvis);
        for (var i = 0; i < 2; i++)
        {
            ResetBone(thighs[i]);
            ResetBone(knees[i]);
        }
    }

    int Bone(string name) => System.Array.FindIndex(bones, b => b.name == name);

    void ResetBone(int i)
    {
        var pose = seated ? seatedPose : standingPose;
        if (i >= 0 && pose[i].Valid)
            bones[i].localRotation = pose[i].Rotation;
    }

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
        {
            var name = current.name.StartsWith("HeadPivot") ? "HeadPivot" : current.name;
            path = name + "/" + path;
        }
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
