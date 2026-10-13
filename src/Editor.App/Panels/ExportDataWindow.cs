using Hexa.NET.ImGui;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// File → Export data (xEdit / CK: Export): a plugin's names, descriptions, books, messages, quests, dialogue, other texts
/// and game settings as tab-separated files (UTF-8) in Documents\STB Editor\Export\&lt;plugin&gt;.
/// </summary>
internal sealed class ExportDataWindow
{
    private bool _open;
    private string _plugin = "";
    private readonly HashSet<string> _kinds = [.. DataExport.Kinds];
    private Task<string>? _export;
    private volatile string _progress = "";
    private string? _result;

    public bool IsBusy => _export is not null;
    public string? Result => _result;

    public static string Folder(string plugin) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "STB Editor", "Export", Path.GetFileNameWithoutExtension(plugin));

    public void Show(EditorApp app)
    {
        _open = true;
        if (_plugin.Length == 0) _plugin = app.ActivePlugin?.Name ?? "";
    }

    public void Start(EditorApp app, string plugin, IReadOnlySet<string> kinds, string folder)
    {
        if (_export is not null || app.Session is not { } session) return;
        var info = session.Workspace.Active.FirstOrDefault(p => p.Name.Equals(plugin, StringComparison.OrdinalIgnoreCase));
        if (info is null) { _result = TF($"{plugin} is not in the load order"); return; }
        var progress = new Progress<string>(m => _progress = m);
        _result = null;
        _export = Task.Run(() =>
        {
            var files = DataExport.Write(info, session.Catalog, session.Workspace.FormIdText, kinds, folder, progress);
            return files.Count == 0 ? T("Nothing to export.") : TF($"{files.Sum(f => f.Rows)} row(s) in {files.Count} file(s) in {folder}");
        });
    }

    public void Draw(EditorApp app)
    {
        if (_export is { IsCompleted: true } done)
        {
            _export = null;
            _result = done.IsCompletedSuccessfully ? done.Result : TF($"Export failed: {done.Exception?.GetBaseException().Message}");
            Log.Info($"export data: {_result}");
        }
        if (!_open || app.Session is not { } session) return;
        ImGui.SetNextWindowSize(app.Scaled(560, 360), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Export data") + "###exportdata", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##exportplugin", _plugin.Length > 0 ? _plugin : T("(plugin)")))
            {
                foreach (var plugin in session.Workspace.Active)
                    if (ImGui.Selectable(plugin.Name, plugin.Name == _plugin)) _plugin = plugin.Name;
                ImGui.EndCombo();
            }
            foreach (var kind in DataExport.Kinds)
            {
                var on = _kinds.Contains(kind);
                if (ImGui.Checkbox(T(kind), ref on)) { if (on) _kinds.Add(kind); else _kinds.Remove(kind); }
            }
            var folder = _plugin.Length > 0 ? Folder(_plugin) : "";
            ImGui.TextColored(Theme.TextDim, TF($"Tab-separated files go to {folder}"));
            ImGui.BeginDisabled(_export is not null || _plugin.Length == 0 || _kinds.Count == 0);
            if (ImGui.Button(_export is not null ? T("exporting…") : T("Export"))) Start(app, _plugin, _kinds, folder);
            ImGui.EndDisabled();
            if (_export is not null) { ImGui.SameLine(); ImGui.TextColored(Theme.Warning, _progress); }
            if (_result is not null) ImGui.TextWrapped(_result);
        }
        ImGui.End();
    }
}
