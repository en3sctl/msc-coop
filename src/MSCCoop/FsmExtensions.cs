using HutongGames.PlayMaker;
using MSCLoader;

namespace MSCCoop;

static class FsmExtensions
{
    // MSCLoader's AddGlobalTransition(string, ...) creates a new FsmEvent for every FSM, but PlayMaker
    // resolves an event name to the first instance it registered and matches transitions by reference,
    // so only the first FSM given a name would ever take the transition.
    public static void AddSharedGlobalTransition(this PlayMakerFSM fsm, string eventName, string stateName) =>
        fsm.AddGlobalTransition(FsmEvent.GetFsmEvent(eventName), stateName);
}
