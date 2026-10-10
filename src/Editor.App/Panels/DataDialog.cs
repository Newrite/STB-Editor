using System.Numerics;
using Hexa.NET.ImGui;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Data;

namespace StbEditor.App.Panels;

/// <summary>
/// File → Data: which game, which MO2 instance and profile. Unlike the Creation Kit this never
/// needs to be started through MO2 — the profile is read directly.
/// </summary>
internal sealed class DataDialog
{
    private const string PopupId = "Data###DataDialog";

    private readonly EditorApp _app;
    private bool _visible;
    private string _gameFolder;
    private int _mo2Mode; // 0 auto, 1 none, 2 custom
    private string _mo2Folder;
    private string? _profile;
    private GameInstall? _detected;
    private string? _detectError;
    private string? _loadError;

    public DataDialog(EditorApp app)
    {
        _app = app;
        var settings = app.Settings;
        _gameFolder = settings.GameFolder ?? GameInstall.FindInstalledGame() ?? @"C:\Skyrim";
        _mo2Mode = settings.Mo2Folder switch { null => 0, "" => 1, _ => 2 };
        _mo2Folder = settings.Mo2Folder ?? string.Empty;
        _profile = settings.Mo2Profile;
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
        }
        catch (Exception ex)
        {
            _detected = null;
            _detectError = ex.Message;
        }
    }

    private void Open()
    {
        if (_detected is null) return;

        var settings = _app.Settings;
        settings.GameFolder = _gameFolder;
        settings.Mo2Folder = _mo2Mode switch { 0 => null, 1 => "", _ => _mo2Folder };
        settings.Mo2Profile = _profile;
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

        ImGui.SeparatorText("Game");
        ImGui.SetNextItemWidth(-130);
        if (ImGui.InputText("##game", ref _gameFolder, 512)) Detect();
        ImGui.SameLine();
        if (ImGui.Button("Find installed")) { _gameFolder = GameInstall.FindInstalledGame() ?? _gameFolder; Detect(); }

        if (_detected is { } game)
        {
            ImGui.TextColored(Theme.Good, game.DisplayName);
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextDim, $"· language {game.Language} · light plugins {(game.SupportsLightPlugins ? "yes" : "no")} · header {game.MaxHeaderVersion:0.00}{(game.HasBees ? " (BEES)" : "")}");
        }
        else
        {
            ImGui.TextColored(Theme.Error, _detectError ?? "Not a game folder");
        }

        ImGui.SeparatorText("Mod Organizer 2");
        if (ImGui.RadioButton("Find next to the game", ref _mo2Mode, 0)) Detect();
        ImGui.SameLine();
        if (ImGui.RadioButton("No MO2 (plain Data)", ref _mo2Mode, 1)) Detect();
        ImGui.SameLine();
        if (ImGui.RadioButton("Folder:", ref _mo2Mode, 2)) Detect();

        if (_mo2Mode == 2)
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##mo2", ref _mo2Folder, 512)) Detect();
        }

        if (_detected?.Mo2 is { } mo2)
        {
            ImGui.TextColored(Theme.TextDim, mo2.Folder);
            ImGui.SetNextItemWidth(260);
            if (ImGui.BeginCombo("Profile", _profile ?? "(none)"))
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
            ImGui.TextColored(Theme.Warning, "No MO2 instance found — plain Data folder will be used.");
        }

        ImGui.EndDisabled();
        ImGui.Spacing();

        if (_loadError is not null) ImGui.TextColored(Theme.Error, _loadError);

        if (loading)
        {
            ImGui.TextColored(Theme.Accent, _app.Loader!.Status + "...");
            ImGui.ProgressBar(-1f * (float)ImGui.GetTime(), new Vector2(-1, 0), "");
            if (ImGui.Button("Cancel load")) _app.Loader.Cancel();
        }
        else
        {
            var settings = _app.Settings;
            var startup = settings.OpenOnStartup;
            if (ImGui.Checkbox("Open these data on startup", ref startup)) settings.OpenOnStartup = startup;

            ImGui.BeginDisabled(_detected is null);
            if (ImGui.Button("Open", new Vector2(120, 0) * (ImGui.GetFontSize() / 16f))) Open();
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Close", new Vector2(120, 0) * (ImGui.GetFontSize() / 16f)))
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
