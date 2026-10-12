using System.Diagnostics;
using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using StbEditor.App.Ui;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// Use Info (CK) / Referenced By (xEdit): every record whose winning version points at a record,
/// and every placed reference of it. Hits come from <see cref="UseIndex"/>; record hits are
/// confirmed with Mutagen's own link list a few at a time (a raw 4-byte match can be a number).
/// </summary>
internal sealed class UseInfoWindows
{
    private enum Check { Pending, Confirmed, Rejected, Unchecked }

    private sealed class Row
    {
        public required UseIndex.Use Use { get; init; }
        public HashSet<string> Via { get; } = [];
        public Check State { get; set; }
    }

    private sealed class Entry
    {
        public required int Id { get; init; }
        public required FormKey Target { get; init; }
        public List<Row>? Records { get; set; }
        public List<Row>? Placed { get; set; }
        public bool Focus { get; set; } = true;
        public string Filter = string.Empty;
    }

    private readonly List<Entry> _open = [];
    private int _next;

    public int Count => _open.Count;

    /// <summary>The newest window's rows once checked (autotests): records confirmed, records rejected, placed.</summary>
    public (int Confirmed, int Rejected, int Placed, int Pending)? LastCounts =>
        _open.LastOrDefault() is { Records: { } r, Placed: { } p }
            ? (r.Count(x => x.State == Check.Confirmed), r.Count(x => x.State == Check.Rejected), p.Count, r.Count(x => x.State == Check.Pending))
            : null;

    public IEnumerable<string> LastRecordLines => _open.LastOrDefault()?.Records?.Where(r => r.State is Check.Confirmed or Check.Unchecked)
        .Select(r => $"{r.Use.Type} {r.Use.Source} via {string.Join("/", r.Via)}") ?? [];

    public IReadOnlyList<UseIndex.Use> LastPlaced => _open.LastOrDefault()?.Placed?.Select(r => r.Use).ToList() ?? [];

    public void Open(FormKey target)
    {
        if (_open.FirstOrDefault(e => e.Target == target) is { } existing) { existing.Focus = true; return; }
        _open.Add(new Entry { Id = _next++, Target = target });
    }

    public void CloseAll() => _open.Clear();

    /// <summary>Some window still confirms hits (keeps frames coming).</summary>
    public bool IsBusy => _open.Any(e => e.Records is null || e.Records.Any(r => r.State == Check.Pending));

    public void Draw(EditorApp app)
    {
        if (app.Session is not { } session) return;
        for (var i = 0; i < _open.Count; i++)
        {
            var entry = _open[i];
            var record = session.Catalog.Get(entry.Target);
            var name = record?.EditorId ?? app.RenderWindow.Scene?.Objects.FirstOrDefault(o => o.Reference == entry.Target)?.BaseEditorId ?? session.Workspace.FormIdText(entry.Target);
            ImGui.SetNextWindowSize(app.Scaled(820, 560), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(ImGui.GetMainViewport().Pos + ImGui.GetMainViewport().Size * 0.5f + new Vector2(i % 6, i % 6) * 24, ImGuiCond.FirstUseEver, new Vector2(0.5f));
            if (entry.Focus) { ImGui.SetNextWindowFocus(); entry.Focus = false; }
            var open = true;
            if (ImGui.Begin($"{TF($"Use Info: {name}")}###useinfo{entry.Id}", ref open, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking))
                DrawEntry(app, session, entry);
            ImGui.End();
            if (!open) _open.RemoveAt(i--);
        }
    }

    private static void DrawEntry(EditorApp app, Services.EditorSession session, Entry entry)
    {
        var uses = session.Uses;
        if (!uses.IsCompleted)
        {
            ImGui.TextColored(Theme.Warning, T("Building the index of links (once per session, cached on disk)…"));
            return;
        }
        if (!uses.IsCompletedSuccessfully)
        {
            ImGui.TextColored(Theme.Error, TF($"Index failed: {uses.Exception?.GetBaseException().Message}"));
            entry.Records ??= [];
            entry.Placed ??= [];
            return;
        }

        if (entry.Records is null) Collect(uses.Result, entry, session);
        Confirm(session, entry);

        var records = entry.Records!.Where(r => r.State != Check.Rejected).ToList();
        var pending = records.Count(r => r.State == Check.Pending);
        ImGui.Text(TF($"{records.Count - pending} record(s) and {entry.Placed!.Count} placed reference(s) use {session.Catalog.Get(entry.Target)?.EditorId ?? session.Workspace.FormIdText(entry.Target)}"));
        if (pending > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.Warning, TF($"(checking {pending}…)"));
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", T("Filter: EditorID, type, plugin, cell"), ref entry.Filter, 128);
        var filter = entry.Filter.Trim();

        if (ImGui.BeginTabBar("##usetabs"))
        {
            if (ImGui.BeginTabItem(TF($"Records ({records.Count - pending})###records")))
            {
                DrawRecords(app, session, records.Where(r => r.State != Check.Pending && Matches(session, r, filter)).ToList());
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(TF($"Placed in the world ({entry.Placed.Count})###placed")))
            {
                DrawPlaced(app, session, entry.Placed.Where(r => Matches(session, r, filter)).ToList());
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private static bool Matches(Services.EditorSession session, Row row, string filter)
    {
        if (filter.Length == 0) return true;
        bool Has(string? s) => s?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;
        var catalog = session.Catalog;
        return Has(row.Use.Type) || Has(row.Use.Plugin.Name) || Has(catalog.Get(row.Use.Source)?.EditorId) || Has(catalog.Get(row.Use.Source)?.Name)
            || (row.Use.Parent is { } p && (Has(catalog.Get(p)?.EditorId) || Has(catalog.Get(p)?.Name))) || Has(session.Workspace.FormIdText(row.Use.Source));
    }

    /// <summary>One row per source record (a leveled list holding the item twice is one user), placed references apart.</summary>
    private static void Collect(UseIndex index, Entry entry, Services.EditorSession session)
    {
        var records = new Dictionary<FormKey, Row>();
        var placed = new Dictionary<FormKey, Row>();
        foreach (var use in index.UsesOf(entry.Target))
        {
            var rows = use.IsPlaced ? placed : records;
            if (!rows.TryGetValue(use.Source, out var row))
            {
                rows[use.Source] = row = new Row
                {
                    Use = use,
                    // Placed references are only read for their link subrecords (base, enable parent, linked refs…): already exact.
                    State = use.IsPlaced ? Check.Confirmed : Check.Pending,
                };
            }
            row.Via.Add(use.Subrecord);
        }
        entry.Records = [.. records.Values.OrderBy(r => r.Use.Type).ThenBy(r => session.Catalog.Get(r.Use.Source)?.EditorId, StringComparer.OrdinalIgnoreCase)];
        entry.Placed = [.. placed.Values.OrderBy(r => r.Use.Parent is { } p ? session.Catalog.Get(p)?.EditorId : null, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Confirms record hits with Mutagen for a few milliseconds per frame.</summary>
    private static void Confirm(Services.EditorSession session, Entry entry)
    {
        var clock = Stopwatch.StartNew();
        var lookup = new RecordLookup(session.Workspace, session.Catalog);
        foreach (var row in entry.Records!)
        {
            if (row.State != Check.Pending) continue;
            if (clock.Elapsed.TotalMilliseconds > 6) break;
            row.State = Verify(session, lookup, row.Use, entry.Target);
        }
    }

    private static Check Verify(Services.EditorSession session, RecordLookup lookup, UseIndex.Use use, FormKey target)
    {
        try
        {
            IMajorRecordGetter? record = null;
            if (session.Catalog.Get(use.Source) is not null) record = lookup.Resolve(use.Source);
            // A topic's later overrides list only their own lines: take the line from the plugin whose version of it wins.
            else if (use.Type == "INFO" && use.Parent is { } topic && use.Plugin.Mod is { } mod && mod.DialogTopics.ContainsKey(topic))
                record = mod.DialogTopics[topic].Responses.FirstOrDefault(r => r.FormKey == use.Source);
            if (record is null) return Check.Unchecked;
            return record.EnumerateFormLinks().Any(l => l.FormKey == target) ? Check.Confirmed : Check.Rejected;
        }
        catch
        {
            return Check.Unchecked;
        }
    }

    private static void DrawRecords(EditorApp app, Services.EditorSession session, List<Row> rows)
    {
        if (rows.Count == 0) { ImGui.TextColored(Theme.TextDim, T("No records use it.")); return; }
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##records", 5, flags)) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("Type"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 3);
        ImGui.TableSetupColumn(T("Editor ID"), ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn(T("Name"), ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(T("Plugin"), ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(T("Where"), ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableHeadersRow();
        var clipper = new ImGuiListClipper();
        clipper.Begin(rows.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var row = rows[i];
                var source = session.Catalog.Get(row.Use.Source);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, row.Use.Type);
                ImGui.TableNextColumn();
                ImGui.PushID(i);
                var label = source?.EditorId ?? (row.Use.Type == "INFO" && row.Use.Parent is { } topic
                    ? TF($"line {session.Workspace.FormIdText(row.Use.Source)} of {session.Catalog.Get(topic)?.EditorId}")
                    : session.Workspace.FormIdText(row.Use.Source));
                if (ImGui.Selectable(label, false, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    // A dialogue line opens its topic (lines are edited from their topic).
                    var open = source is not null ? row.Use.Source : row.Use.Parent;
                    if (open is { } key && session.Catalog.Get(key) is not null)
                    {
                        app.Select(key);
                        app.FormWindows.Open(key);
                    }
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{session.Workspace.FormIdText(row.Use.Source)}\n{T("Double-click to open")}{(row.State == Check.Unchecked ? "\n" + T("Not checked field by field (the record is not in the catalog)") : "")}");
                ImGui.PopID();
                ImGui.TableNextColumn();
                ImGui.Text(source?.Name ?? "");
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, row.Use.Plugin.Name);
                ImGui.TableNextColumn();
                ImGui.TextColored(row.State == Check.Unchecked ? Theme.Warning : Theme.TextDim, string.Join(", ", row.Via));
            }
        }
        clipper.End();
        ImGui.EndTable();
    }

    private static void DrawPlaced(EditorApp app, Services.EditorSession session, List<Row> rows)
    {
        if (rows.Count == 0) { ImGui.TextColored(Theme.TextDim, T("Not placed in the world.")); return; }
        ImGui.TextColored(Theme.TextDim, T("Double-click: open the cell and select the reference."));
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##placed", 4, flags)) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("Reference"), ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn(T("Cell"), ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn(T("Plugin"), ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(T("Where"), ImGuiTableColumnFlags.WidthStretch, 0.5f);
        ImGui.TableHeadersRow();
        var clipper = new ImGuiListClipper();
        clipper.Begin(rows.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var row = rows[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.PushID(i);
                if (ImGui.Selectable($"{row.Use.Type} {session.Workspace.FormIdText(row.Use.Source)}", false, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick)
                    && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && row.Use.Parent is { } cell)
                    app.RenderWindow.GoToReference(app, row.Use.Source, cell);
                ImGui.PopID();
                ImGui.TableNextColumn();
                ImGui.Text(CellName(session, row.Use.Parent));
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, row.Use.Plugin.Name);
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, string.Join(", ", row.Via));
            }
        }
        clipper.End();
        ImGui.EndTable();
    }

    private static string CellName(Services.EditorSession session, FormKey? cell)
    {
        if (cell is not { } key) return "—";
        var record = session.Catalog.Get(key);
        if (record is null) return session.Workspace.FormIdText(key);
        if (record.Winner.Cell is { IsPersistentCell: true, Worldspace: { } persistentWorld })
            return $"{session.Catalog.Get(persistentWorld)?.EditorId ?? "?"} <persistent>";
        if (record.Winner.Cell is { IsInterior: false, Worldspace: { } world } placement)
        {
            var worldName = session.Catalog.Get(world)?.EditorId ?? "?";
            var name = record.Name is { Length: > 0 } n ? n : record.EditorId;
            return name is null ? $"{worldName} ({placement.GridX}, {placement.GridY})" : $"{name} — {worldName} ({placement.GridX}, {placement.GridY})";
        }
        return record.Name is { Length: > 0 } title ? $"{title} ({record.EditorId})" : record.EditorId ?? session.Workspace.FormIdText(key);
    }
}
