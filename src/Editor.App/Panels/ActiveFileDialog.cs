using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// Choose the active file like the Creation Kit: an existing plugin, or a new one (created in
/// MO2's overwrite — or Data without MO2 — on the first save). Also hosts the save prompts.
/// </summary>
internal sealed class ActiveFileDialog
{
    private static string ChooseId => T("Active File") + "###ActiveFile";
    private static string LossyId => T("Save check") + "###SaveLossy";
    private static string ErrorId => T("Save failed") + "###SaveError";
    private static string CloseId => T("Unsaved changes") + "###CloseUnsaved";

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
            ImGui.TextColored(Theme.TextDim, T("Load data first (File → Data)."));
            if (ImGui.Button(T("Close"))) Close();
            ImGui.EndPopup();
            return;
        }

        if (_reason.Length > 0) ImGui.TextColored(Theme.Warning, _reason);
        ImGui.TextWrapped(T("Edits are saved into the active file. Pick one of your plugins, or create a new one — as in the Creation Kit."));
        ImGui.Spacing();

        ImGui.RadioButton(T("New plugin"), ref _mode, 1);
        ImGui.SameLine();
        ImGui.RadioButton(T("Existing plugin"), ref _mode, 0);
        ImGui.Separator();

        var game = session.Game;
        if (_mode == 1)
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##newname", ref _newName, 200);
            ImGui.Checkbox(T("Light (ESL flag) — no load order slot, up to 2048 new records"), ref _newLight);
            ImGui.Checkbox(T("Master (ESM flag)"), ref _newMaster);
            ImGui.TextColored(Theme.TextDim, TF($"Created in: {NewPluginFolder(game)}"));
            if (game.Mo2 is not null)
                ImGui.TextColored(Theme.TextDim, T("MO2 shows it in Overwrite; right-click Overwrite → Create mod to keep it as a mod."));
        }
        else
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##filter", T("Filter"), ref _filter, 128);
            if (ImGui.BeginChild("##plugins", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() * 2.2f), ImGuiChildFlags.Borders))
            {
                foreach (var plugin in session.Workspace.Active)
                {
                    if (plugin.Header is null || plugin.Header.IsLocalized) continue; // Skyrim.esm & co. stay read-only
                    if (_filter.Length > 0 && !plugin.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;

                    var label = $"{plugin.IndexLabel,-6} {plugin.Name}";
                    if (ImGui.Selectable(label, _chosen == plugin)) _chosen = plugin;
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(TF($"{plugin.Location.Path}\n{plugin.Header.RecordCount:N0} records, header {plugin.Header.Version:0.00}"));
                }
            }
            ImGui.EndChild();
        }

        if (_error is not null) ImGui.TextColored(Theme.Error, _error);

        var buttonWidth = 150 * ImGui.GetFontSize() / 16f;
        if (ImGui.Button(_saveAfterChoose ? T("Set active & save") : T("Set active"), new Vector2(buttonWidth, 0))) Apply(session, game);
        ImGui.SameLine();
        if (_app.ActivePlugin is not null && ImGui.Button(T("No active file"), new Vector2(buttonWidth, 0)))
        {
            _app.SetActivePlugin(null);
            Close();
        }
        ImGui.SameLine();
        if (ImGui.Button(T("Cancel"), new Vector2(buttonWidth * 0.7f, 0))) Close();

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

    /// <summary>"RACE 071E6A:Skyrim.esm changed" → "RACE 00071E6A  ElderRaceVampire — changed".</summary>
    private string Describe(string line)
    {
        var parts = line.Split(' ');
        if (parts.Length != 3 || _app.Session is not { } session || !FormKey.TryFactory(parts[1], out var key)) return line;
        return $"{parts[0]} {session.Workspace.FormIdText(key)}  {session.Catalog.Get(key)?.EditorId} — {T(parts[2])}";
    }

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
                var file = Path.GetFileName(result.TargetPath);
                if (result.AddedMasters.Count > 0 && _app.Document.Masters is null)
                {
                    // The usual cause: a new master renumbers the plugin's own FormIDs, so nothing can be kept byte for byte.
                    ImGui.TextWrapped(TF($"Your edits use records of {string.Join(", ", result.AddedMasters)}, which is not a master of {file} yet. With a new master the whole file is written anew, not only the edited records."));
                    ImGui.Spacing();
                }
                else if (result.AddedMasters.Count + result.RemovedMasters.Count > 0)
                {
                    if (result.AddedMasters.Count > 0) ImGui.TextColored(Theme.Good, TF($"Masters added: {string.Join(", ", result.AddedMasters)}"));
                    if (result.RemovedMasters.Count > 0) ImGui.TextColored(Theme.Warning, TF($"Unused masters removed: {string.Join(", ", result.RemovedMasters)}"));
                    ImGui.TextWrapped(T("A change of masters renumbers FormIDs, so the whole file is written anew, not only the edited records."));
                    ImGui.Spacing();
                }
                var count = $"{result.LossyRecords.Count}{(result.LossyRecords.Count >= 50 ? "+" : "")}";
                ImGui.TextWrapped(TF($"{count} record(s) you did not edit would come out slightly different (the writer normalises some data, as the Creation Kit does: for example it drops empty markers). A backup is made either way."));
                ImGui.BeginChild("##lossy", new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * Math.Min(8, result.LossyRecords.Count + 1)), ImGuiChildFlags.Borders);
                foreach (var line in result.LossyRecords) ImGui.TextColored(Theme.TextDim, Describe(line));
                ImGui.EndChild();
                ImGui.TextColored(Theme.TextDim, T("Safer: save the edits into a new plugin instead."));

                if (ImGui.Button(T("Save anyway"))) { saver.Confirm(_app); ImGui.CloseCurrentPopup(); }
                ImGui.SameLine();
                if (ImGui.Button(T("Save to a new plugin"))) { saver.Cancel(); ImGui.CloseCurrentPopup(); Show(saveAfter: true, T("Choose a new plugin for these edits.")); _mode = 1; }
                ImGui.SameLine();
                if (ImGui.Button(T("Cancel"))) { saver.Cancel(); ImGui.CloseCurrentPopup(); }
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
            ImGui.TextColored(Theme.TextDim, T("Nothing was written; your plugin is unchanged."));
            if (ImGui.Button("OK")) { saver.ClearError(); ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }
    }

    private void DrawClosePrompt()
    {
        if (_closePrompt && !ImGui.IsPopupOpen(CloseId)) ImGui.OpenPopup(CloseId);
        if (!ImGui.BeginPopupModal(CloseId, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize)) return;

        ImGui.Text(TF($"{_app.Document.PendingCount} unsaved change(s)."));
        if (ImGui.Button(T("Save")))
        {
            _closePrompt = false;
            _closeDecision = false;
            ImGui.CloseCurrentPopup();
            _app.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button(T("Discard and exit"))) { _closeDecision = true; ImGui.CloseCurrentPopup(); }
        ImGui.SameLine();
        if (ImGui.Button(T("Cancel"))) { _closeDecision = false; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }
}
