using System.Collections;
using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// The Creation Kit's quest tabs: Quest Stages (stages, their log entries with conditions, the
/// Papyrus fragment each has), Quest Objectives (display text, target aliases with conditions) and
/// Quest Aliases (fill type, flags, inventory, packages, spells…). A list on the left, the selected
/// item on the right; every change edits the quest's working copy as one undo step.
/// </summary>
internal static partial class QuestEditor
{
    /// <summary>Selected row per quest and tab.</summary>
    private static readonly Dictionary<string, int> Selected = new();

    /// <summary>Fill type picked for an alias whose fill field is still empty (quest, alias ID → fill member).</summary>
    private static readonly Dictionary<(FormKey, uint), string> PickedFill = new();

    public static bool Draws(FormTab tab) => tab.Members is [{ Name: "Stages" or "Objectives" or "Aliases" }];

    public static Dictionary<int, string> AliasNames(IQuestGetter quest) =>
        quest.Aliases.GroupBy(a => (int)a.ID).ToDictionary(g => g.Key, g => g.First().Name ?? "");

    /// <summary>Autotests: select a row of a tab ("Stages", "Objectives", "Aliases").</summary>
    public static void SelectForTest(FormKey quest, string tab, int row) => Selected[$"{quest}/{tab}"] = row;

    public static void Draw(EditorApp app, IQuestGetter quest, FormTab tab, EditTarget? target)
    {
        switch (tab.Members[0].Name)
        {
            case "Stages": DrawStages(app, quest, target); break;
            case "Objectives": DrawObjectives(app, quest, target); break;
            case "Aliases": DrawAliases(app, quest, target); break;
        }
    }

    // ------------------------------------------------------------------ layout

    /// <summary>A list on the left (its buttons above it), the selected item on the right.</summary>
    internal static void Split(string id, Action left, Action right)
    {
        var height = Math.Max(ImGui.GetContentRegionAvail().Y, ImGui.GetFontSize() * 12);
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("list", ImGuiTableColumnFlags.WidthStretch, 0.32f);
        ImGui.TableSetupColumn("item", ImGuiTableColumnFlags.WidthStretch, 0.68f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (ImGui.BeginChild("##left", new Vector2(0, height))) left();
        ImGui.EndChild();
        ImGui.TableNextColumn();
        if (ImGui.BeginChild("##right", new Vector2(0, height))) right();
        ImGui.EndChild();
        ImGui.EndTable();
    }

    private static int SelectedRow(string key, int count)
    {
        var row = Selected.TryGetValue(key, out var s) ? s : 0;
        return count == 0 ? -1 : Math.Clamp(row, 0, count - 1);
    }

    /// <summary>Checkboxes for the bits of a flags field, a few per row.</summary>
    internal static void FlagBoxes(EditTarget? target, string label, IReadOnlyList<object> path, object? value, Type enumType, int columns)
    {
        var raw = value is null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        if (!ImGui.BeginTable("##flags" + label, columns)) return;
        foreach (var (name, bit) in RecordFields.FlagBits(enumType))
        {
            ImGui.TableNextColumn();
            var on = (raw & bit) == bit;
            if (target is null) ImGui.BeginDisabled();
            if (ImGui.Checkbox(FieldEditors.Label(name), ref on) && target is not null)
            {
                var set = Enum.ToObject(enumType, on ? raw | bit : raw & ~bit);
                var at = path.ToArray();
                target.Apply(TF($"Set {label}"), copy => RecordFields.Set(copy, at, set));
            }
            if (target is null) ImGui.EndDisabled();
        }
        ImGui.EndTable();
    }

    /// <summary>
    /// The index of a stage or an objective: unique in the quest, the list kept sorted by it (as the CK).
    /// Returns the item's new row when it moved.
    /// </summary>
    private static int? IndexField(EditTarget? target, string list, int row, int index, IEnumerable<int> taken)
    {
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6);
        if (target is null) { ImGui.Text(TF($"Index {index}")); return null; }
        if (!Fields.Int(T("Index"), index, out var value) || value == index) return null;
        if (value is < 0 or > ushort.MaxValue || taken.Contains(value))
        {
            Log.Warn(TF($"Index {value}: already used or out of range (0–65535)"));
            return null;
        }
        target.Apply(TF($"Set index {value}"), copy =>
        {
            RecordFields.Set(copy, [list, row, "Index"], (ushort)value);
            SortByIndex((IList)RecordFields.Get(copy, [list])!);
        });
        return taken.Count(t => t < value);
    }

    private static void SortByIndex(IList list)
    {
        var sorted = list.Cast<object>().OrderBy(o => Convert.ToInt32(o.GetType().GetProperty("Index")!.GetValue(o), CultureInfo.InvariantCulture)).ToList();
        list.Clear();
        foreach (var item in sorted) list.Add(item);
    }

    private static string Short(string? text, int length = 48)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var line = text.ReplaceLineEndings(" ");
        return line.Length <= length ? line : line[..length] + "…";
    }

    // ------------------------------------------------------------------ stages

    private static void DrawStages(EditorApp app, IQuestGetter quest, EditTarget? target)
    {
        var key = $"{quest.FormKey}/Stages";
        var stages = quest.Stages;
        var row = SelectedRow(key, stages.Count);
        Split("##stages", () =>
        {
            if (target is not null)
            {
                if (ImGui.SmallButton(T("+ stage")))
                {
                    var index = stages.Count == 0 ? 10 : stages.Max(s => s.Index) / 10 * 10 + 10;
                    target.Apply(TF($"Add stage {index}"), copy =>
                    {
                        var list = (IList)RecordFields.Get(copy, ["Stages"])!;
                        list.Add(new QuestStage { Index = (ushort)index });
                        SortByIndex(list);
                    });
                    Selected[key] = stages.Count(s => s.Index < index);
                }
                ImGui.SameLine();
                if (row >= 0 && ImGui.SmallButton(T("remove")))
                {
                    var at = row;
                    target.Apply(TF($"Remove stage {stages[at].Index}"), copy => ((IList)RecordFields.Get(copy, ["Stages"])!).RemoveAt(at));
                }
            }
            for (var i = 0; i < stages.Count; i++)
            {
                var stage = stages[i];
                var marks = (stage.Flags.HasFlag(QuestStage.Flag.StartUpStage) ? T(" [start]") : "") + (stage.Flags.HasFlag(QuestStage.Flag.ShutDownStage) ? T(" [shut down]") : "");
                var text = $"{stage.Index,5}{marks}  {Short(stage.LogEntries.FirstOrDefault()?.Entry?.String)}";
                if (ImGui.Selectable(text + $"##stage{i}", i == row)) Selected[key] = i;
            }
            if (stages.Count == 0) ImGui.TextColored(Theme.TextDim, T("No stages."));
        }, () =>
        {
            DrawCompileResult(app, quest);
            if (row < 0) return;
            var stage = stages[row];
            if (IndexField(target, "Stages", row, stage.Index, stages.Where((_, j) => j != row).Select(s => (int)s.Index)) is { } moved) Selected[key] = moved;
            FlagBoxes(target, T("Stage flags"), ["Stages", row, "Flags"], stage.Flags, typeof(QuestStage.Flag), 2);

            ImGui.SeparatorText(TF($"Log entries ({stage.LogEntries.Count})"));
            if (target is not null)
            {
                if (ImGui.SmallButton(T("+ log entry")))
                    target.Apply(TF($"Add log entry to stage {stage.Index}"), copy => RecordFields.AddItem(copy, ["Stages", row, "LogEntries"]));
            }
            for (var j = 0; j < stage.LogEntries.Count; j++)
            {
                var entry = stage.LogEntries[j];
                ImGui.PushID(j);
                var open = ImGui.CollapsingHeader(TF($"Log entry {j + 1}: {Short(entry.Entry?.String, 60)}") + $"###entry{j}", ImGuiTreeNodeFlags.DefaultOpen);
                if (target is not null)
                {
                    ImGui.SameLine(ImGui.GetContentRegionAvail().X - ImGui.GetFontSize() * 4);
                    var at = j;
                    if (ImGui.SmallButton(T("remove"))) target.Apply(T("Remove log entry"), copy => RecordFields.RemoveItem(copy, ["Stages", row, "LogEntries"], at));
                }
                if (open)
                {
                    ImGui.Indent(ImGui.GetFontSize() * 0.5f);
                    FlagBoxes(target, T("Log entry flags"), ["Stages", row, "LogEntries", j, "Flags"], entry.Flags, typeof(QuestLogEntry.Flag), 2);
                    FormView.DrawObject(app, entry, ["Entry", "NextQuest", "Conditions"], ["Stages", row, "LogEntries", j], target);
                    DrawFragment(app, quest, stage.Index, j, target);
                    ImGui.Unindent(ImGui.GetFontSize() * 0.5f);
                }
                ImGui.PopID();
            }
        });
    }

    // ------------------------------------------------------------------ objectives

    private static void DrawObjectives(EditorApp app, IQuestGetter quest, EditTarget? target)
    {
        var key = $"{quest.FormKey}/Objectives";
        var objectives = quest.Objectives;
        var row = SelectedRow(key, objectives.Count);
        var aliases = AliasNames(quest);
        string AliasText(int id) => aliases.TryGetValue(id, out var name) ? $"{id}: {name}" : TF($"{id} (no such alias)");
        Split("##objectives", () =>
        {
            if (target is not null)
            {
                if (ImGui.SmallButton(T("+ objective")))
                {
                    var index = objectives.Count == 0 ? 10 : objectives.Max(o => o.Index) / 10 * 10 + 10;
                    target.Apply(TF($"Add objective {index}"), copy =>
                    {
                        var list = (IList)RecordFields.Get(copy, ["Objectives"])!;
                        list.Add(new QuestObjective { Index = (ushort)index, DisplayText = new Mutagen.Bethesda.Strings.TranslatedString(Mutagen.Bethesda.Strings.TranslatedString.DefaultLanguage, "") });
                        SortByIndex(list);
                    });
                    Selected[key] = objectives.Count(o => o.Index < index);
                }
                ImGui.SameLine();
                if (row >= 0 && ImGui.SmallButton(T("remove")))
                {
                    var at = row;
                    target.Apply(TF($"Remove objective {objectives[at].Index}"), copy => ((IList)RecordFields.Get(copy, ["Objectives"])!).RemoveAt(at));
                }
            }
            for (var i = 0; i < objectives.Count; i++)
            {
                var objective = objectives[i];
                if (ImGui.Selectable($"{objective.Index,5}  {Short(objective.DisplayText?.String)}##objective{i}", i == row)) Selected[key] = i;
            }
            if (objectives.Count == 0) ImGui.TextColored(Theme.TextDim, T("No objectives."));
        }, () =>
        {
            if (row < 0) return;
            var objective = objectives[row];
            if (IndexField(target, "Objectives", row, objective.Index, objectives.Where((_, j) => j != row).Select(o => (int)o.Index)) is { } moved) Selected[key] = moved;
            FlagBoxes(target, T("Objective flags"), ["Objectives", row, "Flags"], objective.Flags, typeof(QuestObjective.Flag), 2);
            FormView.DrawObject(app, objective, ["DisplayText"], ["Objectives", row], target);

            ImGui.SeparatorText(TF($"Targets ({objective.Targets.Count})"));
            if (target is not null && ImGui.SmallButton(T("+ target")))
            {
                var first = aliases.Keys.DefaultIfEmpty(0).Min();
                target.Apply(T("Add objective target"), copy =>
                {
                    RecordFields.AddItem(copy, ["Objectives", row, "Targets"]);
                    var targets = (IList)RecordFields.Get(copy, ["Objectives", row, "Targets"])!;
                    ((QuestObjectiveTarget)targets[^1]!).AliasID = first;
                });
            }
            for (var t = 0; t < objective.Targets.Count; t++)
            {
                var objectiveTarget = objective.Targets[t];
                ImGui.PushID(t);
                var open = ImGui.CollapsingHeader(TF($"Target: alias {AliasText(objectiveTarget.AliasID)}") + $"###target{t}", ImGuiTreeNodeFlags.DefaultOpen);
                if (target is not null)
                {
                    ImGui.SameLine(ImGui.GetContentRegionAvail().X - ImGui.GetFontSize() * 4);
                    var at = t;
                    if (ImGui.SmallButton(T("remove"))) target.Apply(T("Remove objective target"), copy => RecordFields.RemoveItem(copy, ["Objectives", row, "Targets"], at));
                }
                if (open)
                {
                    ImGui.Indent(ImGui.GetFontSize() * 0.5f);
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text(T("Alias"));
                    ImGui.SameLine(ImGui.GetFontSize() * 6);
                    ImGui.SetNextItemWidth(-1);
                    if (target is null) ImGui.Text(AliasText(objectiveTarget.AliasID));
                    else if (ImGui.BeginCombo("##alias", AliasText(objectiveTarget.AliasID)))
                    {
                        foreach (var (id, _) in aliases.OrderBy(a => a.Key))
                        {
                            if (!ImGui.Selectable(AliasText(id), id == objectiveTarget.AliasID)) continue;
                            var at = t;
                            target.Apply(T("Objective target alias"), copy => RecordFields.Set(copy, ["Objectives", row, "Targets", at, "AliasID"], id));
                        }
                        ImGui.EndCombo();
                    }
                    FlagBoxes(target, T("Target flags"), ["Objectives", row, "Targets", t, "Flags"], objectiveTarget.Flags, typeof(Quest.TargetFlag), 1);
                    FormView.DrawObject(app, objectiveTarget, ["Conditions"], ["Objectives", row, "Targets", t], target);
                    ImGui.Unindent(ImGui.GetFontSize() * 0.5f);
                }
                ImGui.PopID();
            }
        });
    }

    // ------------------------------------------------------------------ aliases

    /// <summary>How an alias is filled (CK's fill type), by the member that holds it.</summary>
    private static readonly (string Member, string Label, QuestAlias.TypeEnum? Only)[] FillTypes =
    [
        ("", "Find matching (conditions) / none", null),
        (nameof(IQuestAliasGetter.ForcedReference), "Specific reference", QuestAlias.TypeEnum.Reference),
        (nameof(IQuestAliasGetter.UniqueActor), "Unique actor", QuestAlias.TypeEnum.Reference),
        (nameof(IQuestAliasGetter.SpecificLocation), "Specific location", QuestAlias.TypeEnum.Location),
        (nameof(IQuestAliasGetter.Location), "Location alias reference", null),
        (nameof(IQuestAliasGetter.External), "External alias reference", null),
        (nameof(IQuestAliasGetter.CreateReferenceToObject), "Create reference to object", QuestAlias.TypeEnum.Reference),
        (nameof(IQuestAliasGetter.FindMatchingRefNearAlias), "Find matching reference near alias", QuestAlias.TypeEnum.Reference),
        (nameof(IQuestAliasGetter.FindMatchingRefFromEvent), "Find matching from event", null),
    ];

    private static string FillOf(IQuestAliasGetter alias) =>
        alias.ForcedReference.FormKeyNullable is { IsNull: false } ? "ForcedReference"
        : alias.UniqueActor.FormKeyNullable is { IsNull: false } ? "UniqueActor"
        : alias.SpecificLocation.FormKeyNullable is { IsNull: false } ? "SpecificLocation"
        : alias.Location is not null ? "Location"
        : alias.External is not null ? "External"
        : alias.CreateReferenceToObject is not null ? "CreateReferenceToObject"
        : alias.FindMatchingRefNearAlias is not null ? "FindMatchingRefNearAlias"
        : alias.FindMatchingRefFromEvent is not null ? "FindMatchingRefFromEvent"
        : "";

    private static void DrawAliases(EditorApp app, IQuestGetter quest, EditTarget? target)
    {
        var key = $"{quest.FormKey}/Aliases";
        var aliases = quest.Aliases;
        var row = SelectedRow(key, aliases.Count);
        Split("##aliases", () =>
        {
            if (target is not null)
            {
                if (ImGui.SmallButton(T("+ reference alias"))) AddAlias(quest, target, QuestAlias.TypeEnum.Reference, key);
                ImGui.SameLine();
                if (ImGui.SmallButton(T("+ location alias"))) AddAlias(quest, target, QuestAlias.TypeEnum.Location, key);
                if (row >= 0 && ImGui.SmallButton(T("remove")))
                {
                    var at = row;
                    target.Apply(TF($"Remove alias {aliases[at].Name}"), copy => ((IList)RecordFields.Get(copy, ["Aliases"])!).RemoveAt(at));
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Objectives, conditions and scripts that use its ID are not changed"));
            }
            for (var i = 0; i < aliases.Count; i++)
            {
                var alias = aliases[i];
                var kind = alias.Type == QuestAlias.TypeEnum.Location ? T("loc") : T("ref");
                if (ImGui.Selectable($"{alias.ID,3}  {alias.Name}  ({kind})##alias{i}", i == row)) Selected[key] = i;
            }
            if (aliases.Count == 0) ImGui.TextColored(Theme.TextDim, T("No aliases."));
        }, () =>
        {
            if (row < 0) return;
            var alias = aliases[row];
            ImGui.TextColored(Theme.TextDim, TF($"Alias ID {alias.ID} · {(alias.Type == QuestAlias.TypeEnum.Location ? T("location alias") : T("reference alias"))}"));
            FormView.DrawObject(app, alias, ["Name"], ["Aliases", row], target);
            DrawUseReport(app, quest, alias);

            // Fill type: the member present decides; a picked one whose field is still empty is remembered.
            var fill = FillOf(alias);
            if (fill.Length == 0 && PickedFill.TryGetValue((quest.FormKey, alias.ID), out var picked)) fill = picked;
            var options = FillTypes.Where(f => f.Only is null || f.Only == alias.Type).ToList();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(T("Fill type"));
            ImGui.SameLine(ImGui.GetFontSize() * 6);
            ImGui.SetNextItemWidth(-1);
            var current = options.FirstOrDefault(f => f.Member == fill);
            if (target is null) ImGui.Text(T(current.Label ?? ""));
            else if (ImGui.BeginCombo("##fill", T(current.Label ?? "")))
            {
                foreach (var option in options)
                {
                    if (!ImGui.Selectable(T(option.Label), option.Member == fill)) continue;
                    PickedFill[(quest.FormKey, alias.ID)] = option.Member;
                    var at = row;
                    var member = option.Member;
                    target.Apply(TF($"Alias fill type: {T(option.Label)}"), copy =>
                    {
                        var a = (QuestAlias)((IList)RecordFields.Get(copy, ["Aliases"])!)[at]!;
                        if (member != "ForcedReference") a.ForcedReference.Clear();
                        if (member != "UniqueActor") a.UniqueActor.Clear();
                        if (member != "SpecificLocation") a.SpecificLocation.Clear();
                        if (member != "Location") a.Location = null;
                        if (member != "External") a.External = null;
                        if (member != "CreateReferenceToObject") a.CreateReferenceToObject = null;
                        if (member != "FindMatchingRefNearAlias") a.FindMatchingRefNearAlias = null;
                        if (member != "FindMatchingRefFromEvent") a.FindMatchingRefFromEvent = null;
                        if (member is "Location" or "External" or "CreateReferenceToObject" or "FindMatchingRefNearAlias" or "FindMatchingRefFromEvent"
                            && RecordFields.Get(a, [member]) is null)
                            RecordFields.Create(a, [member]);
                    });
                }
                ImGui.EndCombo();
            }
            if (fill.Length > 0) FormView.DrawObject(app, alias, [fill], ["Aliases", row], target);

            ImGui.SeparatorText(T("Alias flags"));
            FlagBoxes(target, T("Alias flags"), ["Aliases", row, "Flags"], alias.Flags, typeof(QuestAlias.Flag), 3);

            ImGui.SeparatorText(T("Alias data"));
            FormView.DrawObject(app, alias, ["Conditions", "DisplayName", "AliasIDToForceIntoWhenFilled", "VoiceTypes", "Keywords", "Items", "Spells",
                "Factions", "PackageData", "SpectatorOverridePackageList", "ObserveDeadBodyOverridePackageList", "GuardWarnOverridePackageList",
                "CombatOverridePackageList"], ["Aliases", row], target);
        });
    }

    private static void AddAlias(IQuestGetter quest, EditTarget target, QuestAlias.TypeEnum type, string key)
    {
        // New IDs come from the quest's counter (never reused: objectives, conditions and scripts point at IDs).
        var id = Math.Max(quest.NextAliasID ?? 0, quest.Aliases.Count == 0 ? 0 : quest.Aliases.Max(a => a.ID) + 1);
        target.Apply(TF($"Add alias {id}"), copy =>
        {
            var q = (Quest)copy;
            q.Aliases.Add(new QuestAlias { ID = id, Type = type, Name = $"{(type == QuestAlias.TypeEnum.Location ? "Location" : "Ref")}Alias{id:00}" });
            q.NextAliasID = id + 1;
        });
        Selected[key] = quest.Aliases.Count;
    }
}
