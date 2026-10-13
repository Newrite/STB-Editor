using System.Numerics;
using System.Text;
using Hexa.NET.ImGui;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// File → Plugin assets: every file a plugin's records use and the textures of its meshes, where each comes from (a mod's
/// loose file, an archive) and which are missing; exported as a list.
/// </summary>
internal sealed class PluginAssetsWindow
{
    private bool _open;
    private string _plugin = "";
    private bool _missingOnly;
    private string _filter = "";
    private Task<List<PluginAssets.Asset>>? _listing;
    private CancellationTokenSource? _cancel;
    private volatile string _progress = "";
    private List<PluginAssets.Asset> _assets = [];
    private string? _result;

    public bool IsBusy => _listing is not null;
    public IReadOnlyList<PluginAssets.Asset> Assets => _assets;

    public void Show(EditorApp app)
    {
        _open = true;
        if (_plugin.Length == 0) _plugin = app.ActivePlugin?.Name ?? "";
    }

    public void List(EditorApp app, string plugin)
    {
        if (_listing is not null || app.Session is not { } session) return;
        var info = session.Workspace.Active.FirstOrDefault(p => p.Name.Equals(plugin, StringComparison.OrdinalIgnoreCase));
        if (info is null) { _result = TF($"{plugin} is not in the load order"); return; }
        _plugin = info.Name;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        var progress = new Progress<string>(m => _progress = m);
        _result = null;
        _listing = Task.Run(() => PluginAssets.List(info, session.Assets, progress, token));
    }

    public void Draw(EditorApp app)
    {
        if (_listing is { IsCompleted: true } done)
        {
            _listing = null;
            if (done.IsCompletedSuccessfully)
            {
                _assets = done.Result;
                _result = TF($"{_assets.Count} file(s), {_assets.Count(a => a.Missing)} missing");
            }
            else _result = TF($"Listing failed: {done.Exception?.GetBaseException().Message}");
            Log.Info($"plugin assets {_plugin}: {_result}");
        }
        if (!_open || app.Session is not { } session) return;
        ImGui.SetNextWindowSize(app.Scaled(900, 560), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Plugin assets") + "###pluginassets", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 16);
            if (ImGui.BeginCombo("##assetplugin", _plugin.Length > 0 ? _plugin : T("(plugin)")))
            {
                foreach (var plugin in session.Workspace.Active)
                    if (ImGui.Selectable(plugin.Name, plugin.Name == _plugin)) _plugin = plugin.Name;
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(_listing is not null || _plugin.Length == 0);
            if (ImGui.Button(_listing is not null ? T("listing…") : T("List"))) List(app, _plugin);
            ImGui.EndDisabled();
            if (_listing is not null)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton(T("Stop"))) _cancel?.Cancel();
                ImGui.SameLine();
                ImGui.TextColored(Theme.Warning, _progress);
            }
            ImGui.SameLine();
            ImGui.Checkbox(T("Missing only"), ref _missingOnly);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12);
            ImGui.InputTextWithHint("##assetfilter", T("Filter paths"), ref _filter, 256);
            ImGui.SameLine();
            ImGui.BeginDisabled(_assets.Count == 0);
            if (ImGui.Button(T("Export list"))) _result = Export();
            ImGui.EndDisabled();
            if (_result is not null) ImGui.TextWrapped(_result);
            ImGui.TextColored(Theme.TextDim, T("Paths the plugin's records name (models, textures, sounds, scripts…) and the textures inside its meshes. Voice files and FaceGen are not in this list (see Voice files and Check and clean)."));

            var shown = _assets.Where(a => (!_missingOnly || a.Missing) && (_filter.Length == 0 || a.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase))).ToList();
            if (ImGui.BeginTable("##assets", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn(T("Path"), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn(T("Kind"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 8);
                ImGui.TableSetupColumn(T("From"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 14);
                ImGui.TableSetupColumn(T("Used by"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 14);
                ImGui.TableHeadersRow();
                var clipper = new ImGuiListClipper();
                clipper.Begin(shown.Count);
                while (clipper.Step())
                    for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        var a = shown[i];
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        if (a.Missing) ImGui.TextColored(Theme.Error, a.Path); else ImGui.TextUnformatted(a.Path);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(T(a.Kind));
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(a.Source is null ? T("missing") : a.Source.ToString());
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(string.Join(", ", a.UsedBy.Take(3)) + (a.UsedBy.Count > 3 ? "…" : ""));
                        if (ImGui.IsItemHovered() && a.UsedBy.Count > 3) ImGui.SetTooltip(string.Join("\n", a.UsedBy));
                    }
                clipper.End();
                ImGui.EndTable();
            }
        }
        ImGui.End();
    }

    private string Export()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "STB Editor", "Export", Path.GetFileNameWithoutExtension(_plugin), $"{Path.GetFileNameWithoutExtension(_plugin)} - Assets.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = new StringBuilder("PATH\tKIND\tFROM\tUSED BY\r\n");
            foreach (var a in _assets) text.Append(a.Path).Append('\t').Append(a.Kind).Append('\t').Append(a.Source?.ToString() ?? "MISSING").Append('\t').Append(string.Join(", ", a.UsedBy)).Append("\r\n");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
            return TF($"{_assets.Count} rows written to {path}");
        }
        catch (Exception ex) { return TF($"Cannot write {path}: {ex.Message}"); }
    }
}
