using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// The Creation Kit's scene dialog: Scene (quest, flags, conditions), Actors (the quest's aliases with their
/// behaviour flags) and Phases and Actions — a timeline with a row per actor (and one for timers) and a column
/// per phase; an action spans its start to end phase. The selected action or phase is edited below it; a
/// dialogue action shows its topic with the lines, as the quest's Dialogue tab does.
/// </summary>
internal static class SceneEditor
{
    /// <summary>Selected action (its Index) or phase (list position) per scene.</summary>
    private static readonly Dictionary<FormKey, uint> SelectedAction = new();
    private static readonly Dictionary<FormKey, int> SelectedPhase = new();

    public static bool Draws(FormTab tab) => tab.Title is "Scene" or "Actors" or "Phases and Actions";

    /// <summary>Autotests: select an action by its index.</summary>
    public static void SelectActionForTest(FormKey scene, uint index)
    {
        SelectedAction[scene] = index;
        SelectedPhase.Remove(scene);
    }

    private static T? Current<T>(EditorApp app, FormKey key) where T : class, IMajorRecordGetter
    {
        if (app.Document.Override(key)?.Record is T edited) return edited;
        var session = app.Session!;
        return new RecordLookup(session.Workspace, session.Catalog).Resolve<T>(key);
    }

    public static IQuestGetter? Quest(EditorApp app, ISceneGetter scene) =>
        scene.Quest.IsNull ? null : Current<IQuestGetter>(app, scene.Quest.FormKey);

    public static void Draw(EditorApp app, ISceneGetter scene, FormTab tab, EditTarget? target)
    {
        var quest = Quest(app, scene);
        var aliases = quest is null ? new Dictionary<int, string>() : QuestEditor.AliasNames(quest);
        switch (tab.Title)
        {
            case "Scene":
                FormView.DrawObject(app, scene, ["EditorID", "Quest"], [], target);
                ImGui.SeparatorText(T("Flags"));
                QuestEditor.FlagBoxes(target, T("Scene flags"), ["Flags"], scene.Flags, typeof(Scene.Flag), 2);
                FormView.DrawObject(app, scene, ["Conditions"], [], target);
                break;
            case "Actors":
                DrawActors(scene, aliases, target);
                break;
            case "Phases and Actions":
                DrawTimeline(app, scene, quest, aliases, target);
                break;
        }
    }

    private static string AliasText(IReadOnlyDictionary<int, string> aliases, int id) =>
        aliases.TryGetValue(id, out var name) ? $"{id}: {name}" : TF($"{id} (no such alias)");

    // ------------------------------------------------------------------ actors

    private static void DrawActors(ISceneGetter scene, Dictionary<int, string> aliases, EditTarget? target)
    {
        if (target is not null)
        {
            var free = aliases.Keys.Where(id => scene.Actors.All(a => a.ID != id)).OrderBy(id => id).ToList();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 16);
            if (ImGui.BeginCombo("##addactor", T("+ actor (quest alias)")))
            {
                foreach (var id in free)
                    if (ImGui.Selectable(AliasText(aliases, id)))
                    {
                        var chosen = id;
                        target.Apply(TF($"Add scene actor {AliasText(aliases, chosen)}"), copy => Scenes.AddActor((Scene)copy, chosen));
                    }
                if (free.Count == 0) ImGui.TextColored(Theme.TextDim, aliases.Count == 0 ? T("Set the scene's quest: actors are its aliases.") : T("Every alias is already an actor."));
                ImGui.EndCombo();
            }
        }
        if (scene.Actors.Count == 0)
        {
            ImGui.TextColored(Theme.TextDim, T("No actors."));
            return;
        }
        for (var i = 0; i < scene.Actors.Count; i++)
        {
            var actor = scene.Actors[i];
            ImGui.PushID(i);
            ImGui.SeparatorText(AliasText(aliases, (int)actor.ID));
            if (target is not null)
            {
                ImGui.SameLine(ImGui.GetContentRegionAvail().X - ImGui.GetFontSize() * 4);
                var id = (int)actor.ID;
                if (ImGui.SmallButton(T("remove")))
                {
                    if (scene.Actions.Any(a => a.ActorID == id && a.Type != SceneAction.TypeEnum.Timer)) Log.Warn(T("This actor still has actions: remove them first"));
                    else target.Apply(T("Remove scene actor"), copy => Scenes.RemoveActor((Scene)copy, id));
                }
            }
            QuestEditor.FlagBoxes(target, T("Actor flags"), ["Actors", i, "Flags"], actor.Flags, typeof(SceneActor.Flag), 2);
            QuestEditor.FlagBoxes(target, T("Actor behaviour"), ["Actors", i, "BehaviorFlags"], actor.BehaviorFlags, typeof(SceneActor.BehaviorFlag), 2);
            ImGui.PopID();
        }
    }

    // ------------------------------------------------------------------ timeline

    private static void DrawTimeline(EditorApp app, ISceneGetter scene, IQuestGetter? quest, Dictionary<int, string> aliases, EditTarget? target)
    {
        var key = scene.FormKey;
        var phases = scene.Phases.Count;
        var phase = SelectedPhase.TryGetValue(key, out var p) && p < phases ? p : (int?)null;
        if (target is not null)
        {
            if (ImGui.SmallButton(T("+ phase"))) target.Apply(T("Add scene phase"), copy => Scenes.InsertPhase((Scene)copy, phases));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("A new phase at the end"));
            if (phase is { } selectedPhase)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton(TF($"+ phase before {selectedPhase + 1}"))) target.Apply(T("Insert scene phase"), copy => Scenes.InsertPhase((Scene)copy, selectedPhase));
                ImGui.SameLine();
                if (ImGui.SmallButton(TF($"remove phase {selectedPhase + 1}")))
                {
                    target.Apply(TF($"Remove scene phase {selectedPhase + 1}"), copy => Scenes.RemovePhase((Scene)copy, selectedPhase));
                    SelectedPhase.Remove(key);
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Actions only in this phase go with it; later ones move left"));
            }
        }
        if (phases == 0)
        {
            ImGui.TextColored(Theme.TextDim, T("No phases: add one, then actions in it."));
            return;
        }

        // Rows: the actors, then timers. Columns: the phases.
        var rows = scene.Actors.Select(a => ((int?)a.ID, AliasText(aliases, (int)a.ID))).Append((null, T("Timers"))).ToList();
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingFixedFit;
        var height = Math.Min((rows.Count + 1) * ImGui.GetFrameHeightWithSpacing() * 2.2f, ImGui.GetFontSize() * 20);
        if (ImGui.BeginTable("##timeline", phases + 1, flags, new System.Numerics.Vector2(0, height)))
        {
            ImGui.TableSetupScrollFreeze(1, 1);
            ImGui.TableSetupColumn(T("Actor"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 9);
            for (var i = 0; i < phases; i++) ImGui.TableSetupColumn($"##phase{i}", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 9);
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            ImGui.TableNextColumn();
            ImGui.Text(T("Actor"));
            for (var i = 0; i < phases; i++)
            {
                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{PhaseText(scene, i)}##ph{i}", phase == i))
                {
                    SelectedPhase[key] = i;
                    SelectedAction.Remove(key);
                }
            }
            foreach (var (actor, label) in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.Text(label);
                for (var i = 0; i < phases; i++)
                {
                    ImGui.TableNextColumn();
                    ImGui.PushID($"{actor}/{i}");
                    foreach (var (position, action) in Scenes.In(scene, actor, i))
                    {
                        var starts = (action.StartPhase ?? 0) == i;
                        var text = starts ? ActionText(app, action) : "  …";
                        var selected = SelectedAction.TryGetValue(key, out var s) && s == action.Index && !SelectedPhase.ContainsKey(key);
                        if (ImGui.Selectable($"{text}##a{position}", selected))
                        {
                            SelectedAction[key] = action.Index ?? 0;
                            SelectedPhase.Remove(key);
                        }
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip(ActionText(app, action));
                    }
                    if (target is not null)
                    {
                        if (ImGui.SmallButton("+")) ImGui.OpenPopup("##add");
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Add an action here"));
                        if (ImGui.BeginPopup("##add"))
                        {
                            var at = i;
                            var who = actor ?? (int)(scene.Actors.FirstOrDefault()?.ID ?? 0);
                            SceneAction.TypeEnum[] kinds = actor is null ? [SceneAction.TypeEnum.Timer] : [SceneAction.TypeEnum.Dialog, SceneAction.TypeEnum.Package];
                            foreach (var kind in kinds)
                                if (ImGui.Selectable(T(KindName(kind))))
                                {
                                    var chosen = kind;
                                    var next = Math.Max(scene.LastActionIndex ?? 0, scene.Actions.Select(a => a.Index ?? 0).DefaultIfEmpty(0u).Max()) + 1;
                                    target.Apply(TF($"Add scene action {T(KindName(chosen))}"), copy => Scenes.AddAction((Scene)copy, chosen, who, at));
                                    SelectedAction[key] = next;
                                    SelectedPhase.Remove(key);
                                }
                            ImGui.EndPopup();
                        }
                    }
                    ImGui.PopID();
                }
            }
            ImGui.EndTable();
        }

        if (phase is { } shown)
        {
            ImGui.SeparatorText(TF($"Phase {shown + 1}"));
            ImGui.PushID($"phase{shown}");
            FormView.DrawObject(app, scene.Phases[shown], ["Name", "StartConditions", "CompletionConditions"], ["Phases", shown], target);
            ImGui.PopID();
        }
        else if (SelectedAction.TryGetValue(key, out var index) && scene.Actions.Select((a, i) => (a, i)).FirstOrDefault(x => x.a.Index == index) is { a: { } chosen } found)
            DrawAction(app, scene, quest, aliases, chosen, found.i, target);
    }

    private static string KindName(SceneAction.TypeEnum kind) => kind switch
    {
        SceneAction.TypeEnum.Dialog => "Dialogue action",
        SceneAction.TypeEnum.Package => "Package action",
        _ => "Timer action",
    };

    private static string ActionText(EditorApp app, ISceneActionGetter action) => action.Type switch
    {
        SceneAction.TypeEnum.Dialog => action.Topic.IsNull ? T("Dialogue: (no topic)") : TF($"Dialogue: {Name(app, action.Topic.FormKey)}"),
        SceneAction.TypeEnum.Package => action.Packages.Count == 0 ? T("Package: (none)") : TF($"Package: {Name(app, action.Packages[0].FormKey)}"),
        _ => TF($"Timer {RecordFields.FloatText(action.TimerSeconds ?? 0)} s"),
    };

    private static string Name(EditorApp app, FormKey key) => app.Session!.Catalog.Get(key)?.EditorId ?? key.ToString();

    private static void DrawAction(EditorApp app, ISceneGetter scene, IQuestGetter? quest, Dictionary<int, string> aliases, ISceneActionGetter action, int position, EditTarget? target)
    {
        ImGui.PushID($"action{action.Index}");
        ImGui.SeparatorText(TF($"Action {action.Index}: {T(KindName(action.Type))}"));
        if (target is not null)
        {
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - ImGui.GetFontSize() * 4);
            if (ImGui.SmallButton(T("remove")))
            {
                target.Apply(T("Remove scene action"), copy => ((Scene)copy).Actions.RemoveAt(position));
                SelectedAction.Remove(scene.FormKey);
                ImGui.PopID();
                return;
            }
        }
        var path = new object[] { "Actions", position };
        if (ImGui.BeginTable("##action", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch, 0.36f);
            ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch, 0.64f);
            var actors = scene.Actors.ToDictionary(a => (int)a.ID, a => aliases.GetValueOrDefault((int)a.ID, ""));
            if (action.Type != SceneAction.TypeEnum.Timer)
            {
                Row(T("Actor"));
                ConditionsEditor.IndexCombo("##actor", action.ActorID ?? 0, actors, target, id => target!.Apply(T("Scene action actor"), copy => RecordFields.Set(copy, [.. path, "ActorID"], id)));
            }
            Row(T("Start phase"));
            PhaseCombo("##start", scene, (int)(action.StartPhase ?? 0), 0, target, i => target!.Apply(T("Scene action start phase"), copy =>
            {
                RecordFields.Set(copy, [.. path, "StartPhase"], (uint)i);
                if (RecordFields.Get(copy, [.. path, "EndPhase"]) is uint end && end < i) RecordFields.Set(copy, [.. path, "EndPhase"], (uint)i);
            }));
            Row(T("End phase"));
            PhaseCombo("##end", scene, (int)(action.EndPhase ?? action.StartPhase ?? 0), (int)(action.StartPhase ?? 0), target,
                i => target!.Apply(T("Scene action end phase"), copy => RecordFields.Set(copy, [.. path, "EndPhase"], (uint)i)));
            if (action.Type == SceneAction.TypeEnum.Dialog)
            {
                Row(T("Head-track actor"));
                var heads = new Dictionary<int, string> { [-1] = T("(none)") };
                foreach (var (id, name) in actors) heads[id] = name;
                ConditionsEditor.IndexCombo("##head", action.HeadtrackActorID ?? -1, heads, target, id => target!.Apply(T("Scene action head-track actor"), copy => RecordFields.Set(copy, [.. path, "HeadtrackActorID"], id)));
            }
            ImGui.EndTable();
        }
        switch (action.Type)
        {
            case SceneAction.TypeEnum.Dialog:
                QuestEditor.FlagBoxes(target, T("Scene action flags"), [.. path, "Flags"], action.Flags, typeof(SceneAction.Flag), 3);
                FormView.DrawObject(app, action, ["Name", "Topic", "Emotion", "EmotionValue", "LoopingMin", "LoopingMax"], path, target);
                DrawTopic(app, scene, quest, action, position, target);
                break;
            case SceneAction.TypeEnum.Package:
                FormView.DrawObject(app, action, ["Name", "Packages"], path, target);
                break;
            default:
                FormView.DrawObject(app, action, ["Name", "TimerSeconds"], path, target);
                break;
        }
        ImGui.PopID();
    }

    private static string PhaseText(ISceneGetter scene, int i) =>
        i < scene.Phases.Count && scene.Phases[i].Name is { Length: > 0 } name ? TF($"Phase {i + 1}") + $": {name}" : TF($"Phase {i + 1}");

    /// <summary>A phase by its number as the timeline shows it (from 1), from <paramref name="first"/> on.</summary>
    private static void PhaseCombo(string id, ISceneGetter scene, int current, int first, EditTarget? target, Action<int> set)
    {
        ImGui.SetNextItemWidth(-1);
        if (target is null) { ImGui.Text(PhaseText(scene, current)); return; }
        if (!ImGui.BeginCombo(id, PhaseText(scene, current))) return;
        for (var i = first; i < scene.Phases.Count; i++)
            if (ImGui.Selectable(PhaseText(scene, i), i == current) && i != current) set(i);
        ImGui.EndCombo();
    }

    private static void Row(string label)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.TableNextColumn();
    }

    /// <summary>The dialogue action's topic with its lines; without a topic, a button that makes a scene topic for it.</summary>
    private static void DrawTopic(EditorApp app, ISceneGetter scene, IQuestGetter? quest, ISceneActionGetter action, int position, EditTarget? target)
    {
        if (!action.Topic.IsNull && Current<IDialogTopicGetter>(app, action.Topic.FormKey) is { } topic)
        {
            if (quest is not null) DialogueEditor.DrawTopic(app, quest, topic, target is not null);
            return;
        }
        if (target is null || quest is null) return;
        if (ImGui.SmallButton(T("+ scene topic")))
        {
            if (DialogueEditor.NeedsActive(app)) return;
            var topicRecord = new DialogTopic(app.ActivePlugin!.Allocate(), app.Session!.Game.Release)
            {
                EditorID = DialogueEditor.FreeEditorId(app, $"{scene.EditorID}Topic"),
                Priority = 50,
                Category = DialogTopic.CategoryEnum.Scene,
                Subtype = DialogTopic.SubtypeEnum.Scene,
            };
            topicRecord.Quest.SetTo(quest.FormKey);
            var copy = (Scene)app.Document.WorkingCopy(scene.FormKey, scene);
            copy.Actions[position].Topic.SetTo(topicRecord.FormKey);
            app.Document.CommitEdits(TF($"New scene topic {topicRecord.EditorID}"), [(topicRecord, RecordPlace.TopLevel, null), (copy, RecordPlace.TopLevel, null)]);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("A new topic (category Scene) of the scene's quest for this action; add its lines below"));
    }
}
