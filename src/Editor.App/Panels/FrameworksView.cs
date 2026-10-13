using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// Framework references as a table (Use Info's «Frameworks» tab, World → Framework configs): which config gives, swaps, lists or
/// describes the record, the line, the mod; for SPID and KID lines, «who gets it» works out the NPCs / records the filters match.
/// </summary>
internal static class FrameworksView
{
    private static readonly Dictionary<FrameworkRef, Task<(List<FormKey> Matched, int Unknown)>> Matches = new();
    private static List<SpidMatch.Npc>? _npcs;
    private static object? _npcsFor;
    private static FrameworkRef? _shown;

    public static string RoleText(FrameworkRef r) => (r.Framework, r.Role) switch
    {
        ("SPID", "gives") => TF($"gives {r.Kind} to NPCs"),
        ("SPID", "filter") => T("is a condition of who gets something"),
        ("KID", "keyword") => TF($"keyword added to {r.Kind}"),
        ("KID", "filter") => T("is a condition of which records get a keyword"),
        ("BOS", "swapped out") => T("replaced by another in the world"),
        ("BOS", "swapped in") => T("replaces another in the world"),
        ("BOS", "transformed") => T("moved / turned / scaled in the world"),
        ("FLM", "list") => T("form list that gets entries"),
        ("FLM", "added") => T("added to a form list"),
        ("DESC", _) => T("description shown in the menu"),
        ("SRD", _) => T("sounds distributed"),
        ("OAR", _) => T("in an animation replacer's conditions"),
        ("DAR", _) => T("in an animation replacer's conditions"),
        _ => r.Role,
    };

    /// <summary>Who or what a SPID / KID line reaches (worked out on a worker once per line).</summary>
    public static Task<(List<FormKey> Matched, int Unknown)> Reach(EditorSession session, FrameworkRef r)
    {
        if (Matches.TryGetValue(r, out var task)) return task;
        var lookup = new RecordLookup(session.Workspace, session.Catalog);
        task = Task.Run(() =>
        {
            if (r.Framework == "KID") return KidMatch.Records(r.Fields, lookup, session.ByEditorId);
            List<SpidMatch.Npc> npcs;
            lock (Matches)
            {
                if (!ReferenceEquals(_npcsFor, session) || _npcs is null)
                {
                    _npcs = (session.Catalog.BySignature.GetValueOrDefault("NPC_") ?? []).Select(n => lookup.Resolve<INpcGetter>(n.FormKey)).OfType<INpcGetter>()
                        .Select(n => SpidMatch.Npc.Of(n, lookup)).ToList();
                    _npcsFor = session;
                }
                npcs = _npcs;
            }
            IEnumerable<FormKey> Items(FormKey k) => lookup.Resolve<IFormListGetter>(k)?.Items.Select(i => i.FormKey) ?? [];
            var matched = new List<FormKey>();
            var unknown = 0;
            foreach (var npc in npcs)
                switch (SpidMatch.Matches(r.Fields, npc, session.ByEditorId, Items))
                {
                    case true: matched.Add(npc.Key); break;
                    case null: unknown++; matched.Add(npc.Key); break;
                }
            return (matched, unknown);
        });
        Matches[r] = task;
        return task;
    }

    /// <summary>A readable account of a SPID line's filters.</summary>
    public static string SpidFilters(FrameworkRef r)
    {
        string F(int i) => i < r.Fields.Count && r.Fields[i].Trim() is { Length: > 0 } f && !f.Equals("NONE", StringComparison.OrdinalIgnoreCase) ? f : "";
        var parts = new List<string>();
        if (F(1).Length > 0) parts.Add(TF($"names / keywords: {F(1)}"));
        if (F(2).Length > 0) parts.Add(TF($"forms: {F(2)}"));
        if (F(3).Length > 0) parts.Add(TF($"level: {F(3)}"));
        if (F(4).Length > 0) parts.Add(TF($"traits: {F(4)}"));
        if (F(5).Length > 0) parts.Add(TF($"count: {F(5)}"));
        parts.Add(TF($"chance {SpidMatch.Chance(r.Fields)}%"));
        return parts.Count == 1 ? T("every NPC") + ", " + parts[0] : string.Join("; ", parts);
    }

    public static void DrawTable(EditorApp app, EditorSession session, IReadOnlyList<FrameworkRef> refs, bool showRecord)
    {
        if (refs.Count == 0) { ImGui.TextColored(Theme.TextDim, T("No framework config names it.")); return; }
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        var height = _shown is not null && refs.Contains(_shown) ? ImGui.GetContentRegionAvail().Y * 0.55f : 0;
        if (ImGui.BeginTable("##frameworks", showRecord ? 5 : 4, flags, new Vector2(0, height)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn(T("Framework"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 4);
            if (showRecord) ImGui.TableSetupColumn(T("Record"), ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn(T("What"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn(T("Line"), ImGuiTableColumnFlags.WidthStretch, 2.4f);
            ImGui.TableSetupColumn(T("Mod / file"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableHeadersRow();
            var clipper = new ImGuiListClipper();
            clipper.Begin(refs.Count);
            while (clipper.Step())
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    var r = refs[i];
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    if (ImGui.Selectable($"{r.Framework}##fw{i}", ReferenceEquals(_shown, r), ImGuiSelectableFlags.SpanAllColumns)) _shown = r;
                    if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && session.Catalog.Get(r.Form) is not null) app.FormWindows.Open(r.Form);
                    if (showRecord)
                    {
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(session.Catalog.Get(r.Form)?.EditorId ?? session.Workspace.FormIdText(r.Form));
                    }
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(RoleText(r));
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.TextDim, r.Text.Length > 160 ? r.Text[..160] + "…" : r.Text);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.TextDim, $"{r.Mod} · {Path.GetFileName(r.File)}:{r.Line}");
                }
            clipper.End();
            ImGui.EndTable();
        }
        if (_shown is { } shown && refs.Contains(shown)) DrawDetails(app, session, shown);
        else ImGui.TextColored(Theme.TextDim, T("Click a line for its details (for SPID and KID: who gets it); double-click opens the record."));
    }

    private static void DrawDetails(EditorApp app, EditorSession session, FrameworkRef r)
    {
        ImGui.SeparatorText($"{r.Framework}: {session.Catalog.Get(r.Form)?.EditorId ?? r.Form.ToString()}");
        ImGui.TextWrapped(r.Text);
        ImGui.TextColored(Theme.TextDim, TF($"{r.Mod} · {r.File}, line {r.Line}"));
        var reaches = (r.Framework == "SPID" && r.Role == "gives") || (r.Framework == "KID" && r.Role == "keyword");
        if (r.Framework == "SPID" && r.Role == "gives") ImGui.TextWrapped(TF($"Who: {SpidFilters(r)}"));
        if (!reaches) return;
        var task = Reach(session, r);
        if (!task.IsCompleted) { ImGui.TextColored(Theme.Warning, T("working out who gets it…")); return; }
        if (!task.IsCompletedSuccessfully) { ImGui.TextColored(Theme.Error, task.Exception?.GetBaseException().Message ?? "?"); return; }
        var (matched, unknown) = task.Result;
        ImGui.Text(r.Framework == "SPID" ? TF($"{matched.Count} NPC(s) get it") : TF($"{matched.Count} record(s) get the keyword"));
        if (unknown > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextDim, TF($"({unknown} by filters the editor cannot check — level following the player, locations, skills…)"));
        }
        if (ImGui.BeginChild("##reach", Vector2.Zero, ImGuiChildFlags.Borders))
        {
            var clipper = new ImGuiListClipper();
            clipper.Begin(matched.Count);
            while (clipper.Step())
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    var key = matched[i];
                    var entry = session.Catalog.Get(key);
                    if (ImGui.Selectable($"{entry?.EditorId ?? key.ToString()}  {entry?.Name}  [{key.ModKey.FileName}]##m{i}")) app.FormWindows.Open(key);
                }
            clipper.End();
        }
        ImGui.EndChild();
    }

    /// <summary>Autotests: how many a line reaches (waits for it).</summary>
    public static (int Matched, int Unknown) ReachForTest(EditorSession session, FrameworkRef r)
    {
        var (matched, unknown) = Reach(session, r).GetAwaiter().GetResult();
        return (matched.Count, unknown);
    }
}

/// <summary>World → Framework configs: every reference the SKSE framework configs make, filtered by framework and text.</summary>
internal sealed class FrameworksWindow
{
    private bool _open;
    private string _filter = "";
    private int _framework;

    public void Show() => _open = true;

    public void Draw(EditorApp app)
    {
        if (!_open || app.Session is not { } session) return;
        ImGui.SetNextWindowSize(app.Scaled(1000, 600), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Framework configs") + "###frameworks", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            var task = session.Frameworks;
            if (!task.IsCompleted) ImGui.TextColored(Theme.Warning, T("reading framework configs…"));
            else if (!task.IsCompletedSuccessfully) ImGui.TextColored(Theme.Error, task.Exception?.GetBaseException().Message ?? "?");
            else
            {
                var index = task.Result;
                ImGui.TextColored(Theme.TextDim, TF($"{index.All.Count:N0} references to {index.ByForm.Count:N0} records in: {string.Join(", ", index.Files.Select(f => $"{f.Key} {f.Value}"))}"));
                string[] names = [T("All frameworks"), .. index.Files.Keys.Order()];
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
                ImGui.Combo("##fwkind", ref _framework, names, names.Length);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##fwfilter", T("Filter: EditorID, line, mod"), ref _filter, 128);
                var kind = _framework > 0 && _framework < names.Length ? names[_framework] : null;
                var shown = index.All.Where(r => (kind is null || r.Framework == kind) && (_filter.Length == 0
                    || r.Text.Contains(_filter, StringComparison.OrdinalIgnoreCase) || r.Mod.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                    || (session.Catalog.Get(r.Form)?.EditorId?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false))).ToList();
                FrameworksView.DrawTable(app, session, shown, true);
            }
        }
        ImGui.End();
    }
}
