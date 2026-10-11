using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using StbEditor.App.Ui;
using StbEditor.Core;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// Comparison windows, as xEdit's view: every plugin's version of one record side by side, or
/// several records from the Object Window (Ctrl/Shift+click, right-click → Compare).
/// </summary>
internal sealed class CompareWindows
{
    /// <summary>More columns than this do not fit any screen.</summary>
    public const int MaxColumns = 16;

    private sealed class Entry
    {
        public required int Id { get; init; }
        public required FormKey[] Keys { get; init; }
        public required bool Versions { get; init; }
        public CompareView View { get; } = new();
        public List<CompareColumn> Columns { get; set; } = [];
        public int BuiltAt { get; set; } = -1;
        public bool Focus { get; set; } = true;
    }

    private readonly List<Entry> _open = [];
    private int _next;

    public int Count => _open.Count;

    /// <summary>The newest window's rows (autotests).</summary>
    public CompareNode? LastRoot => _open.LastOrDefault()?.View.Root;
    public IReadOnlyList<CompareColumn> LastColumns => _open.LastOrDefault()?.Columns ?? [];

    public void OpenVersions(FormKey key)
    {
        if (_open.FirstOrDefault(e => e.Versions && e.Keys[0] == key) is { } existing) { existing.Focus = true; return; }
        _open.Add(new Entry { Id = _next++, Keys = [key], Versions = true });
    }

    public void OpenRecords(IReadOnlyList<FormKey> keys)
    {
        if (keys.Count == 0) return;
        _open.Add(new Entry { Id = _next++, Keys = keys.Take(MaxColumns).ToArray(), Versions = false });
    }

    public void CloseAll() => _open.Clear();

    public void CloseLast()
    {
        if (_open.Count > 0) _open.RemoveAt(_open.Count - 1);
    }

    public void Draw(EditorApp app)
    {
        if (app.Session is not { } session) return;
        var version = app.Document.History.Version;
        for (var i = 0; i < _open.Count; i++)
        {
            var entry = _open[i];
            if (entry.BuiltAt != version)
            {
                entry.BuiltAt = version;
                entry.Columns = entry.Versions ? VersionColumns(app, entry.Keys[0]) : RecordColumns(app, entry.Keys);
            }

            var first = session.Catalog.Get(entry.Keys[0]);
            var title = entry.Versions
                ? TF($"Versions: {first?.EditorId ?? session.Workspace.FormIdText(entry.Keys[0])} [{first?.Signature}]")
                : TF($"Compare {entry.Keys.Length} records");
            ImGui.SetNextWindowSize(app.Scaled(1100, 720), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(ImGui.GetMainViewport().Pos + ImGui.GetMainViewport().Size * 0.5f + new Vector2(i % 6, i % 6) * 24, ImGuiCond.FirstUseEver, new Vector2(0.5f));
            if (entry.Focus)
            {
                ImGui.SetNextWindowFocus();
                entry.Focus = false;
            }

            var open = true;
            if (ImGui.Begin($"{title}###compare{entry.Id}", ref open, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking))
                entry.View.Draw(app, entry.Columns, entry.Versions);
            ImGui.End();
            if (!open) _open.RemoveAt(i--);
        }
    }

    /// <summary>
    /// Every plugin's version of a record, master first and winner last (as xEdit lays them out);
    /// an unsaved edit is the last column, since saving makes it the winner.
    /// </summary>
    public static List<CompareColumn> VersionColumns(EditorApp app, FormKey key)
    {
        var versions = Versions(app, key);
        var columns = new List<CompareColumn>();
        for (var i = versions.Count - 1; i >= 0; i--)
        {
            var (plugin, record) = versions[i];
            columns.Add(new CompareColumn(plugin, TF($"{plugin}\nload order position {versions.Count - i} of {versions.Count}"), record));
        }
        if (app.Document.Override(key) is { } edited)
            columns.Add(new CompareColumn(T("unsaved edit"), TF($"Your edit, not saved yet (into {app.ActivePlugin?.Name ?? T("the active file")})"), edited.Record));
        if (columns.Count > 1)
            columns[^1] = columns[^1] with { Title = $"{columns[^1].Title} ({T("winner")})", Tooltip = columns[^1].Tooltip + "\n" + T("The winning version: what the game uses") };
        return columns;
    }

    /// <summary>Every plugin's version of a top-level record, winner first; empty when it is not in the catalog.</summary>
    public static List<(string Plugin, IMajorRecordGetter Record)> Versions(EditorApp app, FormKey key)
    {
        var result = new List<(string, IMajorRecordGetter)>();
        var session = app.Session!;
        if (session.Catalog.Get(key)?.Signature is not { } signature || StbEditor.Data.RecordLookup.GetterType(signature) is not { } type) return result;
        try
        {
            foreach (var context in session.Workspace.LinkCache.ResolveAllSimpleContexts(key, type))
                result.Add((context.ModKey.FileName.String, context.Record));
        }
        catch (Exception ex)
        {
            StbEditor.App.Services.Log.Warn($"compare: cannot read {key}: {ex.Message}");
        }
        return result;
    }

    private static List<CompareColumn> RecordColumns(EditorApp app, IReadOnlyList<FormKey> keys)
    {
        var session = app.Session!;
        var columns = new List<CompareColumn>();
        foreach (var key in keys)
        {
            var catalog = session.Catalog.Get(key);
            IMajorRecordGetter? record = app.Document.Override(key)?.Record;
            record ??= Versions(app, key).Select(v => v.Record).FirstOrDefault();
            var id = session.Workspace.FormIdText(key);
            var title = $"{catalog?.EditorId ?? id}";
            var tooltip = $"{catalog?.EditorId}  {catalog?.Name}\n[{catalog?.Signature}:{id}]  {catalog?.WinningPlugin.Name}";
            if (app.Document.Override(key) is not null) tooltip += "\n" + T("with your unsaved edit");
            columns.Add(new CompareColumn(title, tooltip, record));
        }
        return columns;
    }
}
