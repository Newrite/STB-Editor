using Hexa.NET.ImGui;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// File → Compare with another file (xEdit: Compare to…): a plugin of the load order against another version of the
/// same file — records only in the plugin, only in the other file, and changed ones with the fields that differ.
/// A row opens the record's form.
/// </summary>
internal sealed class CompareFileWindow
{
    private bool _open;
    private string _plugin = "";
    private string _other = "";
    private string _filter = "";
    private int _kind = -1;
    private Task<List<PluginDiff.Entry>>? _task;
    private volatile string _progress = "";
    private List<PluginDiff.Entry>? _result;
    private string? _error;

    public bool IsBusy => _task is not null;
    public IReadOnlyList<PluginDiff.Entry>? Result => _result;

    public void Show(EditorApp app)
    {
        _open = true;
        if (_plugin.Length == 0 && app.ActivePlugin?.Name is { } active && app.Session?.Workspace.Active.Any(p => p.Name == active) == true) _plugin = active;
    }

    public void Start(EditorApp app, string plugin, string other)
    {
        if (_task is not null || app.Session is not { } session) return;
        _plugin = plugin;
        _other = other;
        _error = null;
        var info = session.Workspace.Active.FirstOrDefault(p => p.Name.Equals(plugin, StringComparison.OrdinalIgnoreCase));
        if (info is null) { _error = TF($"{plugin} is not in the load order"); return; }
        if (!File.Exists(other)) { _error = TF($"{other}: no such file"); return; }
        var progress = new Progress<string>(m => _progress = m);
        var release = session.Game.Release;
        _task = Task.Run(() => PluginDiff.Compare(info.Location.Path, other, release, progress));
    }

    private static string KindText(PluginDiff.Kind kind) => kind switch
    {
        PluginDiff.Kind.Added => T("only in the plugin"),
        PluginDiff.Kind.Removed => T("only in the other file"),
        _ => T("changed record"),
    };

    public void Draw(EditorApp app)
    {
        if (_task is { IsCompleted: true } done)
        {
            _task = null;
            if (done.IsCompletedSuccessfully) _result = done.Result;
            else _error = TF($"Compare failed: {done.Exception?.GetBaseException().Message}");
            Log.Info(_error ?? $"compare file: {_result?.Count} difference(s)");
        }
        if (!_open || app.Session is not { } session) return;
        ImGui.SetNextWindowSize(app.Scaled(900, 560), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Compare with another file") + "###comparefile", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 16);
            if (ImGui.BeginCombo("##plugin", _plugin.Length > 0 ? _plugin : T("(plugin)")))
            {
                foreach (var plugin in session.Workspace.Active)
                    if (ImGui.Selectable(plugin.Name, plugin.Name == _plugin)) _plugin = plugin.Name;
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-ImGui.GetFontSize() * 7);
            ImGui.InputTextWithHint("##other", T("the other version of the file: full path"), ref _other, 1024);
            ImGui.SameLine();
            ImGui.BeginDisabled(_task is not null || _plugin.Length == 0 || _other.Length == 0);
            if (ImGui.Button(_task is not null ? T("comparing…") : T("Compare"))) Start(app, _plugin, _other.Trim().Trim('"'));
            ImGui.EndDisabled();
            if (_task is not null) ImGui.TextColored(Theme.Warning, _progress);
            if (_error is not null) ImGui.TextColored(Theme.Error, _error);
            if (_result is { } result) DrawResult(app, result);
        }
        ImGui.End();
    }

    private void DrawResult(EditorApp app, List<PluginDiff.Entry> result)
    {
        var counts = Enum.GetValues<PluginDiff.Kind>().ToDictionary(k => k, k => result.Count(e => e.Kind == k));
        ImGui.Text(TF($"{result.Count} difference(s):"));
        ImGui.SameLine();
        if (ImGui.RadioButton(T("all"), _kind < 0)) _kind = -1;
        foreach (var (kind, count) in counts)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton($"{KindText(kind)} ({count})", _kind == (int)kind)) _kind = (int)kind;
        }
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 16);
        ImGui.InputTextWithHint("##filter", T("Filter: EditorID, type or field"), ref _filter, 128);
        var rows = result.Where(e => (_kind < 0 || (int)e.Kind == _kind)
            && (_filter.Length == 0 || (e.EditorId?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false) || e.Signature.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || e.Fields.Any(f => f.Contains(_filter, StringComparison.OrdinalIgnoreCase)))).ToList();
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##diff", 4, flags)) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("Difference"), ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn(T("Type"), ImGuiTableColumnFlags.WidthStretch, 0.4f);
        ImGui.TableSetupColumn(T("Record"), ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn(T("Fields that differ"), ImGuiTableColumnFlags.WidthStretch, 2.4f);
        ImGui.TableHeadersRow();
        {
            var clipper = new ImGuiListClipper();
            clipper.Begin(rows.Count);
            while (clipper.Step())
            {
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    var e = rows[i];
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var color = e.Kind switch { PluginDiff.Kind.Added => Theme.Accent, PluginDiff.Kind.Removed => Theme.Warning, _ => Theme.Text };
                    ImGui.PushStyleColor(ImGuiCol.Text, color);
                    var open = ImGui.Selectable($"{KindText(e.Kind)}##d{i}", false, ImGuiSelectableFlags.SpanAllColumns);
                    ImGui.PopStyleColor();
                    if (open && e.Kind != PluginDiff.Kind.Removed && app.Session!.Catalog.Get(e.Key) is not null) app.FormWindows.Open(e.Key);
                    ImGui.TableNextColumn();
                    ImGui.Text(e.Signature);
                    ImGui.TableNextColumn();
                    ImGui.Text($"{e.EditorId ?? "—"}  {app.Session!.Workspace.FormIdText(e.Key)}");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.TextDim, string.Join(", ", e.Fields));
                }
            }
            clipper.End();
        }
        ImGui.EndTable();
    }
}
