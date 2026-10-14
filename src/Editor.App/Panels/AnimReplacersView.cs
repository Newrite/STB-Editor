using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// The NPC form's «Animations» tab: which Open / Dynamic Animation Replacer folders of its skeleton would play on it, by their
/// conditions read against the record (the NPC, race, class, voice, combat style, factions, perks, spells, keywords, sex,
/// child, what its outfit and inventory hold). Conditions only the game can tell (weapon drawn, weather, combat, random…)
/// make a row «?». The highest-priority replacer that applies plays its animations.
/// </summary>
internal static class AnimReplacersView
{
    private sealed record Row(AnimReplacer Replacer, Verdict Verdict, List<ConditionResult> Details);

    private static FormKey _for;
    private static object? _forSession;
    private static List<Row> _rows = [];
    private static string _skeleton = "";
    private static bool _showFailing;

    /// <summary>What the record says the NPC wears and holds: its outfit (levelled lists opened a few levels) and inventory.</summary>
    public static AnimFacts FactsOf(INpcGetter npc, RecordLookup lookup)
    {
        var gear = new HashSet<FormKey>();
        void Add(FormKey key, int depth)
        {
            if (key.IsNull || !gear.Add(key)) return;
            if (depth < 4 && lookup.Catalog.Get(key)?.Signature == "LVLI" && lookup.Resolve<ILeveledItemGetter>(key) is { } list)
                foreach (var entry in list.Entries ?? [])
                    if (entry.Data?.Reference.FormKey is { } item) Add(item, depth + 1);
        }
        if (!npc.DefaultOutfit.IsNull && lookup.Resolve<IOutfitGetter>(npc.DefaultOutfit.FormKey) is { } outfit)
            foreach (var item in outfit.Items ?? []) Add(item.FormKey, 0);
        foreach (var entry in npc.Items ?? []) Add(entry.Item.Item.FormKey, 0);
        var keywords = new HashSet<FormKey>();
        var keywordIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in gear)
            if (lookup.Resolve(key) is IKeywordedGetter<IKeywordGetter> { Keywords: { } list })
                foreach (var k in list)
                {
                    if (k.IsNull) continue;
                    keywords.Add(k.FormKey);
                    if (lookup.Catalog.Get(k.FormKey)?.EditorId is { } id) keywordIds.Add(id);
                }
        return new AnimFacts { Npc = SpidMatch.Npc.Of(npc, lookup), Gear = gear, GearKeywords = keywords, GearKeywordIds = keywordIds };
    }

    /// <summary>The animation folder of the NPC's skeleton: meshes\actors\character\ for people (from the race's skeleton model).</summary>
    public static string SkeletonFolder(INpcGetter npc, RecordLookup lookup)
    {
        var race = lookup.Resolve<IRaceGetter>(npc.Race.FormKey);
        var model = race?.SkeletalModel?.Male?.File.GivenPath ?? race?.SkeletalModel?.Female?.File.GivenPath ?? @"Actors\Character\Character Assets\skeleton.nif";
        var parts = model.Replace('/', '\\').TrimStart('\\').Split('\\');
        if (parts.Length > 0 && parts[0].Equals("meshes", StringComparison.OrdinalIgnoreCase)) parts = parts[1..];
        return parts.Length >= 2 ? $@"meshes\{parts[0]}\{parts[1]}\".ToLowerInvariant() : @"meshes\actors\character\";
    }

    /// <summary>The rows for an NPC: replacers of its skeleton (third person), each with its verdict.</summary>
    public static List<(AnimReplacer Replacer, Verdict Verdict, List<ConditionResult> Details)> Evaluate(EditorSession session, INpcGetter npc, List<AnimReplacer> replacers)
    {
        var lookup = new RecordLookup(session.Workspace, session.Catalog);
        var facts = FactsOf(npc, lookup);
        var skeleton = SkeletonFolder(npc, lookup);
        return replacers
            .Where(r => r.Folder.StartsWith(skeleton, StringComparison.OrdinalIgnoreCase) && !r.Folder.Contains(@"\_1stperson\", StringComparison.OrdinalIgnoreCase))
            .Select(r => { var (v, d) = AnimReplacers.Evaluate(r, facts, session.ByEditorId); return (r, r.Disabled ? Verdict.Fail : v, d); })
            .ToList();
    }

    public static void Draw(EditorApp app, INpcGetter npc)
    {
        var session = app.Session!;
        var task = session.AnimReplacers;
        if (!task.IsCompleted) { ImGui.TextColored(Theme.Warning, T("reading animation replacers…")); return; }
        if (!task.IsCompletedSuccessfully) { ImGui.TextColored(Theme.Error, task.Exception?.GetBaseException().Message ?? "?"); return; }
        if (_for != npc.FormKey || !ReferenceEquals(_forSession, session))
        {
            _for = npc.FormKey;
            _forSession = session;
            _rows = Evaluate(session, npc, task.Result).Select(r => new Row(r.Replacer, r.Verdict, r.Details)).ToList();
            _skeleton = SkeletonFolder(npc, new RecordLookup(session.Workspace, session.Catalog));
        }
        int Count(Verdict v) => _rows.Count(r => r.Verdict == v);
        ImGui.TextWrapped(TF($"{Count(Verdict.Pass)} replacer(s) apply, {Count(Verdict.Unknown)} depend on the game, {Count(Verdict.Fail)} do not — of {_rows.Count} for {_skeleton}"));
        ImGui.TextColored(Theme.TextDim, T("The highest priority replacer that applies plays its animations. «?» — some of its conditions only the game can tell (weapon drawn, weather, combat, a random roll…); hover a row for each condition."));
        ImGui.Checkbox(T("Show those that do not apply"), ref _showFailing);

        var shown = _rows.Where(r => _showFailing || r.Verdict != Verdict.Fail).ToList();
        // The one that certainly plays (for animations it has): the first «yes» with no «?» above it.
        var plays = shown.FindIndex(r => r.Verdict == Verdict.Pass);
        if (plays >= 0 && shown.Take(plays).Any(r => r.Verdict == Verdict.Unknown)) plays = -1;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##anims", 6, flags, new Vector2(0, Math.Max(200, ImGui.GetContentRegionAvail().Y)))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("Applies"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 4);
        ImGui.TableSetupColumn(T("Priority"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 6);
        ImGui.TableSetupColumn(T("Replacer"), ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn(T("Animations"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 5);
        ImGui.TableSetupColumn(T("Why"), ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn(T("Mod"), ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();
        var clipper = new ImGuiListClipper();
        clipper.Begin(shown.Count);
        while (clipper.Step())
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var row = shown[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var (text, colour) = row.Verdict switch
                {
                    Verdict.Pass => (i == plays ? T("yes, plays") : T("yes"), Theme.Accent),
                    Verdict.Unknown => ("?", Theme.Warning),
                    _ => (row.Replacer.Disabled ? T("off") : T("no"), Theme.TextDim),
                };
                ImGui.PushID(i);
                ImGui.PushStyleColor(ImGuiCol.Text, colour);
                ImGui.Selectable(text, false, ImGuiSelectableFlags.SpanAllColumns);
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(Tooltip(row));
                ImGui.PopID();
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, $"{row.Replacer.Priority}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{row.Replacer.Framework}: {row.Replacer.Name}");
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, $"{row.Replacer.Animations}");
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, Why(row));
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, row.Replacer.Mod);
            }
        clipper.End();
        ImGui.EndTable();
    }

    /// <summary>The short reason: the first condition that fails, else the first only the game can tell, else «no conditions».</summary>
    private static string Why(Row row)
    {
        if (row.Replacer.Disabled) return T("turned off in OAR");
        var key = row.Details.FirstOrDefault(d => d.Verdict == Verdict.Fail && !d.Text.EndsWith("AND") && !d.Text.EndsWith("OR"))
            ?? row.Details.FirstOrDefault(d => d.Verdict == Verdict.Unknown && !d.Text.EndsWith("AND") && !d.Text.EndsWith("OR"));
        if (key is null) return row.Details.Count == 0 ? T("no conditions") : T("all conditions hold");
        return (key.Verdict == Verdict.Fail ? T("fails: ") : T("in the game: ")) + key.Text;
    }

    private static string Tooltip(Row row)
    {
        var lines = row.Details.Select(d => new string(' ', d.Depth * 3) + (d.Verdict switch { Verdict.Pass => "+ ", Verdict.Fail => "- ", _ => "? " }) + d.Text);
        return $"{row.Replacer.Name}\n{row.Replacer.Folder}\n\n{string.Join("\n", lines.Take(30))}" + (row.Details.Count > 30 ? "\n…" : "");
    }

    /// <summary>Autotests: the counts and the first rows for an NPC (waits for the replacers).</summary>
    public static string ForTest(EditorSession session, INpcGetter npc)
    {
        var rows = Evaluate(session, npc, session.AnimReplacers.GetAwaiter().GetResult());
        return $"{rows.Count(r => r.Verdict == Verdict.Pass)} yes, {rows.Count(r => r.Verdict == Verdict.Unknown)} ?, {rows.Count(r => r.Verdict == Verdict.Fail)} no of {rows.Count}; yes: "
            + string.Join(", ", rows.Where(r => r.Verdict == Verdict.Pass).Take(6).Select(r => $"{r.Replacer.Name} ({r.Replacer.Priority})"));
    }
}
