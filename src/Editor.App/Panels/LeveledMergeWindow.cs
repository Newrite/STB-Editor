using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// File → Merge leveled lists (xEdit: Create Merged Patch, leveled lists): finds the lists two or more plugins change,
/// shows what merging would add or remove against the winning version, and writes the ticked ones into the active file
/// (which must load after those plugins) in one undo step.
/// </summary>
internal sealed class LeveledMergeWindow
{
    private bool _open;
    private Task<List<LeveledMerge.Result>>? _task;
    private volatile string _progress = "";
    private List<LeveledMerge.Result>? _result;
    private readonly HashSet<FormKey> _excluded = [];
    private string? _message;

    public bool IsBusy => _task is not null;
    public IReadOnlyList<LeveledMerge.Result>? Result => _result;

    public void Show() => _open = true;

    public void Start(EditorApp app)
    {
        if (_task is not null || app.Session is not { } session) return;
        var catalog = session.Catalog;
        var lists = new[] { "LVLI", "LVLN", "LVSP" }.SelectMany(s => catalog.BySignature.GetValueOrDefault(s) ?? []).Select(r => r.FormKey).ToList();
        // The active file's own versions are what is being regenerated.
        var skip = app.ActivePlugin is { } active ? new HashSet<ModKey> { ModKey.FromFileName(active.Name) } : null;
        var progress = new Progress<string>(m => _progress = m);
        _message = null;
        _task = Task.Run(() => LeveledMerge.Plan(session.Workspace, lists, k => catalog.Get(k)!.Signature, skip, progress));
    }

    /// <summary>The ticked merged lists into the active file; returns how many.</summary>
    public int Apply(EditorApp app)
    {
        if (_result is null || app.Session is not { } session) return 0;
        if (app.ActivePlugin is not { } active)
        {
            app.ShowActiveFileDialog(T("Merged lists go into the active file — choose it first."));
            return 0;
        }
        // The patch must win: a plugin of the merge loading after the active file would hide it.
        var order = session.Workspace.Active.Select(p => p.Name).ToList();
        var at = order.FindIndex(n => n.Equals(active.Name, StringComparison.OrdinalIgnoreCase));
        var chosen = _result.Where(r => !_excluded.Contains(r.Key)).ToList();
        if (at >= 0 && chosen.SelectMany(r => r.Plugins).Distinct().Any(p => order.FindIndex(n => n.Equals(p, StringComparison.OrdinalIgnoreCase)) > at))
        {
            _message = TF($"{active.Name} loads before some of these plugins: move it to the end of the load order first.");
            return 0;
        }
        var edits = chosen.Select(r => (r.Merged, RecordPlace.TopLevel, (RefContext?)null)).ToList();
        if (edits.Count == 0) return 0;
        app.Document.CommitEdits(TF($"Merge {edits.Count} leveled list(s)"), edits);
        _message = TF($"{edits.Count} merged list(s) put into {active.Name} — save to keep them.");
        Log.Info($"leveled merge: {_message}");
        return edits.Count;
    }

    public void Draw(EditorApp app)
    {
        if (_task is { IsCompleted: true } done)
        {
            _task = null;
            _result = done.IsCompletedSuccessfully ? done.Result : null;
            _excluded.Clear();
            _message = done.IsCompletedSuccessfully ? null : TF($"Search failed: {done.Exception?.GetBaseException().Message}");
            Log.Info($"leveled merge: {_result?.Count} list(s) to merge");
        }
        if (!_open || app.Session is not { } session) return;
        ImGui.SetNextWindowSize(app.Scaled(960, 560), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Merge leveled lists") + "###levelmerge", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.TextWrapped(T("When several plugins change one leveled list, only the last one counts in the game. Merging keeps each plugin's change, item by item: for every item the entries of the last plugin that changed it win. Check the additions and removals: a plugin may have left something out on purpose."));
            ImGui.BeginDisabled(_task is not null);
            if (ImGui.Button(_task is not null ? T("searching…") : T("Find lists to merge"))) Start(app);
            ImGui.EndDisabled();
            if (_task is not null) { ImGui.SameLine(); ImGui.TextColored(Theme.Warning, _progress); }
            if (_result is { } result)
            {
                ImGui.SameLine();
                ImGui.BeginDisabled(result.Count == _excluded.Count);
                if (ImGui.Button(app.ActivePlugin is { } active ? TF($"Put {result.Count - _excluded.Count} into {active.Name}") : T("Put into the active file"))) Apply(app);
                ImGui.EndDisabled();
                DrawTable(app, result);
            }
            if (_message is not null) ImGui.TextWrapped(_message);
        }
        ImGui.End();
    }

    private void DrawTable(EditorApp app, List<LeveledMerge.Result> result)
    {
        if (result.Count == 0)
        {
            ImGui.TextColored(Theme.TextDim, T("No leveled list needs merging."));
            return;
        }
        string Name(FormKey key) => app.Session!.Catalog.Get(key)?.EditorId ?? app.Session.Workspace.FormIdText(key);
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##merge", 4, flags)) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
        ImGui.TableSetupColumn(T("List"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn(T("Plugins"), ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn(T("Merge adds / removes against the winner"), ImGuiTableColumnFlags.WidthStretch, 2.6f);
        ImGui.TableHeadersRow();
        for (var i = 0; i < result.Count; i++)
        {
            var r = result[i];
            ImGui.TableNextRow();
            ImGui.PushID(i);
            ImGui.TableNextColumn();
            var on = !_excluded.Contains(r.Key);
            if (ImGui.Checkbox("##on", ref on)) { if (on) _excluded.Remove(r.Key); else _excluded.Add(r.Key); }
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{r.EditorId} [{r.Signature}]##l", false)) app.FormWindows.Open(r.Key);
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.TextDim, string.Join(" > ", r.Plugins));
            ImGui.TableNextColumn();
            var parts = r.AddedEntries.Select(a => $"+ {Name(a.Item)} @{a.Level}{(a.Count > 1 ? $" x{a.Count}" : "")}")
                .Concat(r.RemovedEntries.Select(a => $"- {Name(a.Item)} @{a.Level}{(a.Count > 1 ? $" x{a.Count}" : "")}"));
            ImGui.TextWrapped(string.Join(", ", parts));
            ImGui.PopID();
        }
        ImGui.EndTable();
    }
}
