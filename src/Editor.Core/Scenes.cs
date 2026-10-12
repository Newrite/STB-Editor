using Mutagen.Bethesda.Skyrim;

namespace StbEditor.Core;

/// <summary>
/// Scene (SCEN) edits as the Creation Kit makes them: actors are the quest's aliases, phases are columns of
/// the timeline, actions (dialogue, package, timer) belong to an actor and span a start and an end phase.
/// New parts get the values vanilla scenes have (sceneprobe: 2141 scenes).
/// </summary>
public static class Scenes
{
    /// <summary>What an actor does when hit, in combat or talked to: end on death and combat, pause for dialogue (vanilla's usual).</summary>
    public const SceneActor.BehaviorFlag DefaultBehavior = SceneActor.BehaviorFlag.DeathEnd | SceneActor.BehaviorFlag.CombatEnd | SceneActor.BehaviorFlag.DialoguePause;

    public static void AddActor(Scene scene, int aliasId)
    {
        if (scene.Actors.Any(a => a.ID == aliasId)) return;
        scene.Actors.Add(new SceneActor { ID = (uint)aliasId, Flags = 0, BehaviorFlags = DefaultBehavior });
    }

    /// <summary>Removes an actor; refused (false) while actions still use it.</summary>
    public static bool RemoveActor(Scene scene, int aliasId)
    {
        if (scene.Actions.Any(a => a.ActorID == aliasId && a.Type != SceneAction.TypeEnum.Timer)) return false;
        scene.Actors.RemoveAll(a => a.ID == aliasId);
        return true;
    }

    /// <summary>
    /// A new phase at <paramref name="at"/> (0 = first): unnamed, the CK's editor width, and the two empty
    /// script blocks every vanilla phase carries (the NEXT markers). Actions from that phase on move right.
    /// </summary>
    public static void InsertPhase(Scene scene, int at)
    {
        at = Math.Clamp(at, 0, scene.Phases.Count);
        scene.Phases.Insert(at, new ScenePhase { Name = "", EditorWidth = 200, Unused = new ScenePhaseUnusedData(), Unused2 = new ScenePhaseUnusedData() });
        foreach (var action in scene.Actions)
        {
            if (action.StartPhase >= at) action.StartPhase++;
            if (action.EndPhase >= at) action.EndPhase++;
        }
        // Phase fragments are kept by phase number.
        foreach (var fragment in scene.VirtualMachineAdapter?.ScriptFragments?.PhaseFragments ?? [])
            if (fragment.Index >= at) fragment.Index++;
    }

    /// <summary>
    /// Removes a phase: actions only in it go with it, actions across it shrink, later ones move left.
    /// Returns how many actions were removed.
    /// </summary>
    public static int RemovePhase(Scene scene, int index)
    {
        if (index < 0 || index >= scene.Phases.Count) return 0;
        var gone = scene.Actions.RemoveAll(a => a.StartPhase == index && a.EndPhase == index);
        scene.Phases.RemoveAt(index);
        // What is left either starts before the phase or ends after it, so the end can always move left.
        foreach (var action in scene.Actions)
        {
            if (action.StartPhase > index) action.StartPhase--;
            if (action.EndPhase >= index) action.EndPhase--;
        }
        if (scene.VirtualMachineAdapter?.ScriptFragments is { } fragments)
        {
            fragments.PhaseFragments.RemoveAll(f => f.Index == index);
            foreach (var fragment in fragments.PhaseFragments)
                if (fragment.Index > index) fragment.Index--;
        }
        return gone;
    }

    /// <summary>
    /// A new action of <paramref name="type"/> for an actor in a phase, with the next action index (the scene's
    /// last index moves on). Dialogue gets vanilla's loop 1–10 s, no head tracking, neutral emotion; a timer 1 s.
    /// </summary>
    public static SceneAction AddAction(Scene scene, SceneAction.TypeEnum type, int actorId, int phase)
    {
        var index = Math.Max(scene.LastActionIndex ?? 0, scene.Actions.Select(a => a.Index ?? 0).DefaultIfEmpty(0u).Max()) + 1;
        var action = new SceneAction
        {
            Type = type,
            Name = "",
            ActorID = actorId,
            Index = index,
            StartPhase = (uint)phase,
            EndPhase = (uint)phase,
        };
        switch (type)
        {
            case SceneAction.TypeEnum.Dialog:
                action.LoopingMax = 10;
                action.LoopingMin = 1;
                action.HeadtrackActorID = -1;
                action.Emotion = Emotion.Neutral;
                action.EmotionValue = 0;
                break;
            case SceneAction.TypeEnum.Timer:
                action.TimerSeconds = 1;
                break;
        }
        scene.Actions.Add(action);
        scene.LastActionIndex = index;
        return action;
    }

    /// <summary>Actions shown in a cell of the timeline: the actor's (timers have their own row: actor null) in that phase.</summary>
    public static IEnumerable<(int Position, ISceneActionGetter Action)> In(ISceneGetter scene, int? actorId, int phase) =>
        scene.Actions.Select((a, i) => (i, a)).Where(x =>
            (actorId is null ? x.a.Type == SceneAction.TypeEnum.Timer : x.a.Type != SceneAction.TypeEnum.Timer && x.a.ActorID == actorId)
            && (x.a.StartPhase ?? 0) <= phase && phase <= (x.a.EndPhase ?? x.a.StartPhase ?? 0));
}
