using System.Numerics;
using Hexa.NET.ImGui;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// File → Data: which game, which MO2 instance and profile. Unlike the Creation Kit this never
/// needs to be started through MO2 — the profile is read directly.
/// </summary>
internal sealed class DataDialog
{
    private static string PopupId => T("Data") + "###DataDialog";

    private readonly EditorApp _app;
    private bool _visible;
    private string _gameFolder;
    private int _mo2Mode; // 0 auto, 1 none, 2 custom
    private string _mo2Folder;
    private string? _profile;
    private GameInstall? _detected;
    private string? _detectError;
    private string? _loadError;

    // Plugin selection (as the Creation Kit's Data dialog): the enabled plugins of the profile.
    private List<(string Name, bool Master)> _plugins = [];
    private HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    private bool _loadAll = true;
    private string _pluginFilter = string.Empty;

    public DataDialog(EditorApp app)
    {
        _app = app;
        var settings = app.Settings;
        _gameFolder = settings.GameFolder ?? GameInstall.FindInstalledGame() ?? @"C:\Skyrim";
        _mo2Mode = settings.Mo2Folder switch { null => 0, "" => 1, _ => 2 };
        _mo2Folder = settings.Mo2Folder ?? string.Empty;
        _profile = settings.Mo2Profile;
        _loadAll = settings.LoadOnly is null;
        if (settings.LoadOnly is { } only) _checked = new HashSet<string>(only, StringComparer.OrdinalIgnoreCase);
        Detect();
    }

    public void Show()
    {
        _visible = true;
        _loadError = null;
        Detect();
    }

    /// <summary>Loads with the saved settings without showing the dialog (startup / --open).</summary>
    public void OpenImmediately()
    {
        Detect();
        if (_detected is null)
        {
            Show();
            return;
        }

        Open();
    }

    public void OnLoaded() => _visible = false;

    public void OnFailed(Exception error)
    {
        _loadError = error.Message;
        _visible = true;
    }

    private void Detect()
    {
        try
        {
            string? mo2 = _mo2Mode switch { 0 => null, 1 => "", _ => _mo2Folder };
            _detected = GameInstall.Detect(_gameFolder, mo2, _profile);
            _profile = _detected.Mo2?.ProfileName;
            if (_mo2Mode == 0 && _detected.Mo2 is not null) _mo2Folder = _detected.Mo2.Folder;
            _detectError = null;
            _plugins = LoadOrderReader.Read(_detected).Where(l => l.Enabled && l.Exists)
                .Select(l => (l.Name, PluginFileName.IsEsm(l.Name) || PluginFileName.IsEsl(l.Name))).ToList();
        }
        catch (Exception ex)
        {
            _detected = null;
            _detectError = ex.Message;
            _plugins = [];
        }
    }

    /// <summary>Which plugins to load: all, or the checked ones (their masters load with them).</summary>
    private void DrawPlugins()
    {
        ImGui.SeparatorText(T("Plugins"));
        if (ImGui.Checkbox(T("Load the whole load order (as the game)"), ref _loadAll) && !_loadAll && _checked.Count == 0)
            foreach (var p in _plugins.Where(p => p.Master)) _checked.Add(p.Name);
        if (_loadAll)
        {
            ImGui.TextColored(Theme.TextDim, TF($"{_plugins.Count} enabled plugins will load."));
            return;
        }

        ImGui.TextColored(Theme.TextDim, T("Only the checked plugins load; the masters they need come along. Fewer plugins: faster loading, less memory."));
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.5f);
        ImGui.InputTextWithHint("##pluginfilter", T("Filter plugins"), ref _pluginFilter, 128);
        ImGui.SameLine();
        if (ImGui.SmallButton(T("All"))) foreach (var p in _plugins) _checked.Add(p.Name);
        ImGui.SameLine();
        if (ImGui.SmallButton(T("Masters only"))) { _checked.Clear(); foreach (var p in _plugins.Where(p => p.Master)) _checked.Add(p.Name); }
        ImGui.SameLine();
        if (ImGui.SmallButton(T("None"))) _checked.Clear();
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextDim, TF($"{_checked.Count(c => _plugins.Any(p => p.Name.Equals(c, StringComparison.OrdinalIgnoreCase)))} of {_plugins.Count} checked"));

        if (ImGui.BeginChild("##plugins", new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 14), ImGuiChildFlags.Borders))
        {
            var filter = _pluginFilter.Trim();
            for (var i = 0; i < _plugins.Count; i++)
            {
                var (name, master) = _plugins[i];
                if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                var on = _checked.Contains(name);
                ImGui.PushID(i);
                if (ImGui.Checkbox("##p", ref on)) { if (on) _checked.Add(name); else _checked.Remove(name); }
                ImGui.SameLine();
                ImGui.TextColored(Theme.TextDim, $"{i:X2}");
                ImGui.SameLine();
                if (master) ImGui.TextColored(Theme.Accent, name); else ImGui.Text(name);
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
    }

    private void Open()
    {
        if (_detected is null) return;

        var settings = _app.Settings;
        settings.GameFolder = _gameFolder;
        settings.Mo2Folder = _mo2Mode switch { 0 => null, 1 => "", _ => _mo2Folder };
        settings.Mo2Profile = _profile;
        settings.LoadOnly = _loadAll ? null : _checked.Where(c => _plugins.Any(p => p.Name.Equals(c, StringComparison.OrdinalIgnoreCase))).ToList();
        settings.Save();

        _loadError = null;
        _app.StartLoading(_detected);
    }

    public void Draw()
    {
        if (_visible && !ImGui.IsPopupOpen(PopupId)) ImGui.OpenPopup(PopupId);

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(720, 0) * (ImGui.GetFontSize() / 16f), ImGuiCond.Appearing);

        var open = true;
        if (!ImGui.BeginPopupModal(PopupId, ref open, ImGuiWindowFlags.NoSavedSettings))
        {
            if (_visible && !open) _visible = false;
            return;
        }

        var loading = _app.Loader is not null;
        ImGui.BeginDisabled(loading);

        ImGui.SeparatorText(T("Game"));
        ImGui.SetNextItemWidth(-130);
        if (ImGui.InputText("##game", ref _gameFolder, 512)) Detect();
        ImGui.SameLine();
        if (ImGui.Button(T("Find installed"))) { _gameFolder = GameInstall.FindInstalledGame() ?? _gameFolder; Detect(); }

        if (_detected is { } game)
        {
            ImGui.TextColored(Theme.Good, game.DisplayName);
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextDim, TF($"· language {game.Language} · light plugins {(game.SupportsLightPlugins ? "yes" : "no")} · header {game.MaxHeaderVersion:0.00}{(game.HasBees ? " (BEES)" : "")}"));
        }
        else
        {
            ImGui.TextColored(Theme.Error, _detectError ?? T("Not a game folder"));
        }

        ImGui.SeparatorText(T("Mod Organizer 2"));
        if (ImGui.RadioButton(T("Find next to the game"), ref _mo2Mode, 0)) Detect();
        ImGui.SameLine();
        if (ImGui.RadioButton(T("No MO2 (plain Data)"), ref _mo2Mode, 1)) Detect();
        ImGui.SameLine();
        if (ImGui.RadioButton(T("Folder:"), ref _mo2Mode, 2)) Detect();

        if (_mo2Mode == 2)
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##mo2", ref _mo2Folder, 512)) Detect();
        }

        if (_detected?.Mo2 is { } mo2)
        {
            ImGui.TextColored(Theme.TextDim, mo2.Folder);
            ImGui.SetNextItemWidth(260);
            if (ImGui.BeginCombo(T("Profile"), _profile ?? T("(none)")))
            {
                foreach (var profile in mo2.Profiles)
                {
                    if (ImGui.Selectable(profile, profile == _profile))
                    {
                        _profile = profile;
                        Detect();
                    }
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextDim, $"INI: {(_detected.IniFolder == mo2.ProfileFolder ? "profile" : "My Games")}");
        }
        else if (_mo2Mode != 1)
        {
            ImGui.TextColored(Theme.Warning, T("No MO2 instance found — plain Data folder will be used."));
        }

        DrawPlugins();

        ImGui.EndDisabled();
        ImGui.Spacing();

        if (_loadError is not null) ImGui.TextColored(Theme.Error, _loadError);

        if (loading)
        {
            ImGui.TextColored(Theme.Accent, _app.Loader!.Status + "...");
            ImGui.ProgressBar(-1f * (float)ImGui.GetTime(), new Vector2(-1, 0), "");
            if (ImGui.Button(T("Cancel load"))) _app.Loader.Cancel();
        }
        else
        {
            var settings = _app.Settings;
            var startup = settings.OpenOnStartup;
            if (ImGui.Checkbox(T("Open these data on startup"), ref startup)) settings.OpenOnStartup = startup;

            ImGui.BeginDisabled(_detected is null);
            if (ImGui.Button(T("Open"), new Vector2(120, 0) * (ImGui.GetFontSize() / 16f))) Open();
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button(T("Close"), new Vector2(120, 0) * (ImGui.GetFontSize() / 16f)))
            {
                _visible = false;
                ImGui.CloseCurrentPopup();
            }
        }

        if (!open) _visible = false;
        if (!_visible) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}
