using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MSCCoop;

// The game only ever seats a driver. Anyone else in a cabin is a walking character that a moving
// vehicle slides out from under, so the cabin offers a passenger seat mirrored from the driver's,
// entered with the same button and held the same way the game holds a driver: parented to the
// vehicle with player movement stopped.
sealed class PassengerSeat(ObjectRegistry registry)
{
    const string Button = "DrivingMode";
    const string Prompt = "PASSENGER";
    const string InCabinState = "Press return";
    const float ExitSideways = 1.3f;

    sealed class Cabin
    {
        public NetObject Vehicle;
        public PlayMakerFSM Trigger;
        public PlayMakerFSM Drive;
        public Transform DriverHead;
    }

    readonly List<Cabin> cabins = new();
    Transform player;
    FsmGameObject pov;
    FsmBool playerStop;
    FsmString currentVehicle;
    FsmString interaction;
    Cabin seated;
    Transform seat;
    Vector3 seatedOffset;
    bool prompting;

    public bool Seated => seated != null;

    public void OnLevelLoaded()
    {
        cabins.Clear();
        seated = null;
        player = null;
    }

    public void OnGameReady()
    {
        var go = GameObject.Find("PLAYER");
        if (go == null)
            return;
        player = go.transform;
        var globals = PlayMakerGlobals.Instance.Variables;
        pov = globals.FindFsmGameObject("POV");
        playerStop = globals.FindFsmBool("PlayerStop");
        currentVehicle = globals.FindFsmString("PlayerCurrentVehicle");
        interaction = globals.FindFsmString("GUIinteraction");

        foreach (var obj in registry.Objects)
        {
            if (!obj.IsVehicle)
                continue;
            PlayMakerFSM trigger = null, drive = null;
            foreach (var fsm in obj.Go.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (fsm.FsmName != "PlayerTrigger")
                    continue;
                if (fsm.gameObject.name == "DriveTrigger")
                    drive = fsm;
                else if (fsm.gameObject.name == "PlayerTrigger")
                    trigger = fsm;
            }
            var head = drive == null ? null : drive.FsmVariables.FindFsmGameObject("DriverHeadPivot");
            if (trigger != null && head != null && head.Value != null)
                cabins.Add(new Cabin { Vehicle = obj, Trigger = trigger, Drive = drive, DriverHead = head.Value.transform });
        }
    }

    public void Update()
    {
        if (player == null)
            return;
        if (seated != null)
        {
            if (seat == null || cInput.GetKeyDown(Button))
                Stand();
            else
                player.localPosition = seatedOffset;
            return;
        }

        var cabin = string.IsNullOrEmpty(currentVehicle.Value) ? CabinOffered() : null;
        if (cabin == null)
        {
            if (prompting && interaction.Value == Prompt)
                interaction.Value = "";
            prompting = false;
            return;
        }
        if (string.IsNullOrEmpty(interaction.Value) || interaction.Value == Prompt)
        {
            interaction.Value = Prompt;
            prompting = true;
        }
        if (cInput.GetKeyDown(Button))
            Sit(cabin);
    }

    // Inside a cabin but not where the game would offer the driver's seat.
    Cabin CabinOffered()
    {
        foreach (var cabin in cabins)
        {
            if (cabin.Trigger.ActiveStateName == InCabinState && cabin.Drive.ActiveStateName != InCabinState)
                return cabin;
        }
        return null;
    }

    void Sit(Cabin cabin)
    {
        if (pov.Value == null)
            return;
        if (prompting && interaction.Value == Prompt)
            interaction.Value = "";
        prompting = false;

        var vehicle = cabin.Vehicle.Go.transform;
        var head = vehicle.InverseTransformPoint(cabin.DriverHead.position);
        head.x = -head.x;
        var rotation = Quaternion.Inverse(vehicle.rotation) * cabin.DriverHead.rotation;
        seat = new GameObject("MSCCoop Passenger").transform;
        seat.SetParent(vehicle, false);
        seat.localPosition = head;
        seat.localRotation = new Quaternion(rotation.x, -rotation.y, -rotation.z, rotation.w);

        var eyes = player.InverseTransformPoint(pov.Value.transform.position);
        playerStop.Value = true;
        player.SetParent(seat, false);
        player.localRotation = Quaternion.identity;
        seatedOffset = -eyes;
        player.localPosition = seatedOffset;
        seated = cabin;
    }

    public void Stand()
    {
        if (seated == null)
            return;
        var outward = seat == null ? Vector3.zero : seat.parent.right * (Mathf.Sign(seat.localPosition.x) * ExitSideways);
        var position = player.position + outward;
        player.SetParent(null);
        player.position = position;
        player.rotation = Quaternion.Euler(0, player.eulerAngles.y, 0);
        playerStop.Value = false;
        if (seat != null)
            Object.Destroy(seat.gameObject);
        seat = null;
        seated = null;
    }
}
