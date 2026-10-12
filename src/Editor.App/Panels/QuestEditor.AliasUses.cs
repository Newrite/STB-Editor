using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// The alias Use Report (CK: Quest Aliases → Use Report): where the selected alias is used in the quest, its scenes,
/// its packages and its dialogue lines. Built in the background on request; edits made in this session count.
/// </summary>
internal static partial class QuestEditor
{
    private static readonly Dictionary<(FormKey Quest, uint Alias), Task<List<AliasUse>>> UseReports = new();

    /// <summary>Autotests: the report of an alias (started if needed).</summary>
    public static Task<List<AliasUse>> UseReportForTest(EditorApp app, IQuestGetter quest, uint alias) =>
        UseReports.TryGetValue((quest.FormKey, alias), out var task) ? task : UseReports[(quest.FormKey, alias)] = StartUseReport(app, quest, (int)alias);

    private static Task<List<AliasUse>> StartUseReport(EditorApp app, IQuestGetter quest, int alias)
    {
        var session = app.Session!;
        // Edited and new scenes, packages and lines of this session, taken here (the document is the UI thread's).
        var edited = app.Document.Drafts.Values.OfType<OverrideDraft>().Select(d => d.Record)
            .Where(r => r is ISceneGetter or IPackageGetter or IDialogResponsesGetter).Cast<IMajorRecordGetter>().ToDictionary(r => r.FormKey);
        var dialogue = session.Dialogue;
        return Task.Run(() =>
        {
            var lookup = new RecordLookup(session.Workspace, session.Catalog);
            var uses = AliasUses.InQuest(quest, alias);
            T? Current<T>(FormKey key) where T : class, IMajorRecordGetter => edited.GetValueOrDefault(key) as T ?? lookup.Resolve<T>(key);
            var scenes = session.Catalog.BySignature.GetValueOrDefault("SCEN")?.Select(r => r.FormKey) ?? [];
            foreach (var key in scenes.Concat(edited.Values.OfType<ISceneGetter>().Select(s => s.FormKey)).Distinct())
                if (Current<ISceneGetter>(key) is { } scene && scene.Quest.FormKeyNullable == quest.FormKey) uses.AddRange(AliasUses.InScene(scene, alias));
            var packages = session.Catalog.BySignature.GetValueOrDefault("PACK")?.Select(r => r.FormKey) ?? [];
            foreach (var key in packages.Concat(edited.Values.OfType<IPackageGetter>().Select(p => p.FormKey)).Distinct())
                if (Current<IPackageGetter>(key) is { } package && package.OwnerQuest.FormKeyNullable == quest.FormKey) uses.AddRange(AliasUses.InPackage(package, alias));
            if (dialogue.Wait(TimeSpan.FromMinutes(2)) && dialogue.IsCompletedSuccessfully)
                foreach (var topic in dialogue.Result.TopicsOf(quest.FormKey))
                    foreach (var (line, _) in DialogueIndex.Lines(session.Workspace, topic))
                        uses.AddRange(AliasUses.InLine(edited.GetValueOrDefault(line.FormKey) as IDialogResponsesGetter ?? line, alias));
            return uses;
        });
    }

    /// <summary>Core's English place names (AliasUses) in the UI language: pattern → template of the same groups.</summary>
    private static readonly (System.Text.RegularExpressions.Regex Pattern, string Template)[] WherePatterns =
    [
        (new(@"^Stage (\d+) log entry (\d+)"), "Stage {0} log entry {1}"),
        (new(@"^Objective (\d+) target conditions"), "Objective {0} target conditions"),
        (new(@"^Objective (\d+): target"), "Objective {0}: target"),
        (new(@"^Alias (\d+) (\S*) conditions"), "Alias {0} {1} conditions"),
        (new(@"^Alias scripts"), "Alias scripts"),
        (new(@"^Alias (\d+) (\S*)"), "Alias {0} {1}"),
        (new(@"^Script (\S+): property"), "Script {0}: property"),
        (new(@"^Dialogue conditions"), "Dialogue conditions"),
        (new(@"^Event conditions"), "Event conditions"),
        (new(@"^Phase (\d+) start conditions"), "Phase {0} start conditions"),
        (new(@"^Phase (\d+) completion conditions"), "Phase {0} completion conditions"),
        (new(@"^Procedure (\d+) conditions"), "Procedure {0} conditions"),
        (new(@"^Data input (-?\d+): target"), "Data input {0}: target"),
        (new(@"^Action (\d+): head-tracks"), "Action {0}: head-tracks"),
        (new(@"^Action (\d+) "), "Action {0} "),
        (new(@"^Actor$"), "Actor"),
        (new(@"^Conditions"), "Conditions"),
    ];

    private static string WhereText(string where)
    {
        foreach (var (pattern, template) in WherePatterns)
            if (pattern.Match(where) is { Success: true } match)
            {
                var head = string.Format(T(template), match.Groups.Cast<System.Text.RegularExpressions.Group>().Skip(1).Select(g => (object)g.Value).ToArray());
                return (head + where[match.Length..]).Replace("(run on)", T("(run on)"));
            }
        return where;
    }

    private static void DrawUseReport(EditorApp app, IQuestGetter quest, IQuestAliasGetter alias)
    {
        var key = (quest.FormKey, alias.ID);
        if (!UseReports.TryGetValue(key, out var task))
        {
            if (ImGui.SmallButton(T("Use report"))) UseReports[key] = StartUseReport(app, quest, (int)alias.ID);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Where this alias is used: the quest's conditions, objectives, aliases and scripts, its scenes, packages and dialogue"));
            return;
        }
        if (!task.IsCompleted)
        {
            ImGui.TextColored(Theme.TextDim, T("Looking for uses…"));
            return;
        }
        if (!task.IsCompletedSuccessfully)
        {
            ImGui.TextColored(Theme.Error, TF($"Use report failed: {task.Exception?.GetBaseException().Message}"));
            if (ImGui.SmallButton(T("again"))) UseReports.Remove(key);
            return;
        }
        var uses = task.Result;
        var open = ImGui.CollapsingHeader(TF($"Use report ({uses.Count})") + "###usereport", ImGuiTreeNodeFlags.DefaultOpen);
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - ImGui.GetFontSize() * 5);
        if (ImGui.SmallButton(T("refresh"))) UseReports.Remove(key);
        if (!open) return;
        if (uses.Count == 0)
        {
            ImGui.TextColored(Theme.TextDim, T("Not used anywhere."));
            return;
        }
        if (!ImGui.BeginTable("##uses", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn(T("Record"), ImGuiTableColumnFlags.WidthStretch, 0.4f);
        ImGui.TableSetupColumn(T("Where"), ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableHeadersRow();
        for (var i = 0; i < uses.Count; i++)
        {
            var use = uses[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var record = app.Session!.Catalog.Get(use.Record);
            var text = record is null ? TF($"Dialogue line {app.Session.Workspace.FormIdText(use.Record)}") : $"{record.EditorId ?? use.Record.ToString()} [{record.Signature}]";
            if (ImGui.Selectable($"{text}##use{i}", false, ImGuiSelectableFlags.SpanAllColumns) && record is not null) app.FormWindows.Open(use.Record);
            if (ImGui.IsItemHovered() && record is not null) ImGui.SetTooltip(T("Open its form"));
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.TextDim, WhereText(use.Where));
        }
        ImGui.EndTable();
    }
}
