using System.Numerics;
using Hexa.NET.ImGui;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data;

namespace StbEditor.App.Panels;

/// <summary>
/// Choose the active file like the Creation Kit: an existing plugin, or a new one (created in
/// MO2's overwrite — or Data without MO2 — on the first save). Also hosts the save prompts.
/// </summary>
internal sealed class ActiveFileDialog
{
    private const string ChooseId = "Active File###ActiveFile";
    private const string LossyId = "Save check###SaveLossy";
    private const string ErrorId = "Save failed###SaveError";
    private const string CloseId = "Unsaved changes###CloseUnsaved";

    private readonly EditorApp _app;
    private bool _open;
    private bool _saveAfterChoose;
    private string _reason = "";
    private int _mode = 1; // 0 existing, 1 new
    private string _filter = "";
    private PluginInfo? _chosen;
    private string _newName = "MyEdits.esp";
    private bool _newLight;
    private bool _newMaster;
    private string? _error;
    private bool _closePrompt;
    private bool? _closeDecision;

    public ActiveFileDialog(EditorApp app) => _app = app;

    public void Show(bool saveAfter, string reason = "")
    {
        _open = true;
        _saveAfterChoose = saveAfter;
        _reason = reason;
        _error = null;
        _chosen = null;
    }

    /// <summary>Starts the close prompt; returns null while the user decides.</summary>
    public bool? PollClose()
    {
        if (_closeDecision is { } d)
        {
            _closeDecision = null;
            _closePrompt = false;
            return d;
        }

        if (!_closePrompt)
        {
            if (_app.Document.PendingCount == 0) return true;
            _closePrompt = true;
        }

        return null;
    }

    public void Draw()
    {
        DrawChooser();
        DrawSavePrompts();
        DrawClosePrompt();
    }

    private void DrawChooser()
    {
        if (_open && !ImGui.IsPopupOpen(ChooseId)) ImGui.OpenPopup(ChooseId);

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(640, 520) * (ImGui.GetFontSize() / 16f), ImGuiCond.Appearing);

        var open = true;
        if (!ImGui.BeginPopupModal(ChooseId, ref open, ImGuiWindowFlags.NoSavedSettings))
        {
            if (!open) _open = false;
            return;
        }

        var session = _app.Session;
        if (session is null)
        {
            ImGui.TextColored(Theme.TextDim, "Load data first (File → Data).");
            if (ImGui.Button("Close")) Close();
            ImGui.EndPopup();
            return;
        }

        if (_reason.Length > 0) ImGui.TextColored(Theme.Warning, _reason);
        ImGui.TextWrapped("Edits are saved into the active file. Pick one of your plugins, or create a new one — as in the Creation Kit.");
        ImGui.Spacing();

        ImGui.RadioButton("New plugin", ref _mode, 1);
        ImGui.SameLine();
        ImGui.RadioButton("Existing plugin", ref _mode, 0);
        ImGui.Separator();

        var game = session.Game;
        if (_mode == 1)
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##newname", ref _newName, 200);
            ImGui.Checkbox("Light (ESL flag) — no load order slot, up to 2048 new records", ref _newLight);
            ImGui.Checkbox("Master (ESM flag)", ref _newMaster);
            ImGui.TextColored(Theme.TextDim, $"Created in: {NewPluginFolder(game)}");
            if (game.Mo2 is not null)
                ImGui.TextColored(Theme.TextDim, "MO2 shows it in Overwrite; right-click Overwrite → Create mod to keep it as a mod.");
        }
        else
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##filter", "Filter", ref _filter, 128);
            if (ImGui.BeginChild("##plugins", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() * 2.2f), ImGuiChildFlags.Borders))
            {
                foreach (var plugin in session.Workspace.Active)
                {
                    if (plugin.Header is null || plugin.Header.IsLocalized) continue; // Skyrim.esm & co. stay read-only
                    if (_filter.Length > 0 && !plugin.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;

                    var label = $"{plugin.IndexLabel,-6} {plugin.Name}";
                    if (ImGui.Selectable(label, _chosen == plugin)) _chosen = plugin;
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{plugin.Location.Path}\n{plugin.Header.RecordCount:N0} records, header {plugin.Header.Version:0.00}");
                }
            }
            ImGui.EndChild();
        }

        if (_error is not null) ImGui.TextColored(Theme.Error, _error);

        var buttonWidth = 150 * ImGui.GetFontSize() / 16f;
        if (ImGui.Button(_saveAfterChoose ? "Set active & save" : "Set active", new Vector2(buttonWidth, 0))) Apply(session, game);
        ImGui.SameLine();
        if (_app.ActivePlugin is not null && ImGui.Button("No active file", new Vector2(buttonWidth, 0)))
        {
            _app.SetActivePlugin(null);
            Close();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(buttonWidth * 0.7f, 0))) Close();

        if (!open) Close();
        if (!_open) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void Apply(EditorSession session, GameInstall game)
    {
        try
        {
            ActivePlugin active;
            if (_mode == 1)
            {
                var name = _newName.Trim();
                if (!PluginFileName.IsPlugin(name)) name += _newMaster && !_newLight ? ".esm" : ".esp";
                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidOperationException("invalid file name");
                if (session.Workspace.Plugins.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"{name} already exists in the load order — choose it under Existing plugin");

                var path = Path.Combine(NewPluginFolder(game), name);
                if (File.Exists(path)) throw new InvalidOperationException($"{path} already exists");
                active = ActivePlugin.ForNew(path, _newLight && game.SupportsLightPlugins, _newMaster, game, session.Workspace.DefaultEncoding);
            }
            else
            {
                if (_chosen is null) throw new InvalidOperationException("choose a plugin");
                active = ActivePlugin.ForExisting(_chosen, game);
            }

            _app.SetActivePlugin(active);
            Close();
            if (_saveAfterChoose) _app.Save();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private void Close()
    {
        _open = false;
        ImGui.CloseCurrentPopup();
    }

    public static string NewPluginFolder(GameInstall game) => game.Mo2?.OverwriteFolder ?? game.DataFolder;

    private void DrawSavePrompts()
    {
        var saver = _app.Saver;

        if (saver.AwaitingConfirmation is { } pending && !ImGui.IsPopupOpen(LossyId)) ImGui.OpenPopup(LossyId);
        ImGui.SetNextWindowSize(new Vector2(620, 0) * (ImGui.GetFontSize() / 16f), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal(LossyId, ImGuiWindowFlags.NoSavedSettings))
        {
            var result = saver.AwaitingConfirmation;
            if (result is not null)
            {
                ImGui.TextWrapped($"Writing {Path.GetFileName(result.TargetPath)} would change {result.LossyRecords.Count}{(result.LossyRecords.Count >= 50 ? "+" : "")} record(s) you did not edit " +
                    "(the writer normalises some data, as the Creation Kit does). A backup is made either way.");
                ImGui.BeginChild("##lossy", new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 8), ImGuiChildFlags.Borders);
                foreach (var line in result.LossyRecords) ImGui.TextColored(Theme.TextDim, line);
                ImGui.EndChild();
                ImGui.TextColored(Theme.TextDim, "Safer: save the edits into a new plugin instead.");

                if (ImGui.Button("Save anyway")) { saver.Confirm(_app); ImGui.CloseCurrentPopup(); }
                ImGui.SameLine();
                if (ImGui.Button("Save to a new plugin")) { saver.Cancel(); ImGui.CloseCurrentPopup(); Show(saveAfter: true, "Choose a new plugin for these edits."); _mode = 1; }
                ImGui.SameLine();
                if (ImGui.Button("Cancel")) { saver.Cancel(); ImGui.CloseCurrentPopup(); }
            }
            else
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (saver.LastError is not null && !ImGui.IsPopupOpen(ErrorId)) ImGui.OpenPopup(ErrorId);
        if (ImGui.BeginPopupModal(ErrorId, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextColored(Theme.Error, saver.LastError ?? "");
            ImGui.TextColored(Theme.TextDim, "Nothing was written; your plugin is unchanged.");
            if (ImGui.Button("OK")) { saver.ClearError(); ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
    }

    private void DrawClosePrompt()
    {
        if (_closePrompt && !ImGui.IsPopupOpen(CloseId)) ImGui.OpenPopup(CloseId);
        if (!ImGui.BeginPopupModal(CloseId, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize)) return;

        ImGui.Text($"{_app.Document.PendingCount} unsaved change(s).");
        if (ImGui.Button("Save"))
        {
            _closePrompt = false;
            _closeDecision = false;
            ImGui.CloseCurrentPopup();
            _app.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Discard and exit")) { _closeDecision = true; ImGui.CloseCurrentPopup(); }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) { _closeDecision = false; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }
}
