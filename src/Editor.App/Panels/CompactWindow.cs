using Hexa.NET.ImGui;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// File → Compact FormIDs / make light (xEdit: Compact FormIDs for ESL; CK: Convert to light): the active
/// file's own records renumbered into 0x800–0xFFF, links inside it updated, optionally the light flag, loose
/// FaceGen and voice files renamed. The file is backed up and reloaded; plugins that use it are listed —
/// they point at the old numbers.
/// </summary>
internal sealed class CompactWindow
{
    private bool _open;
    private bool _light = true;
    private bool _renameAssets = true;
    private string? _result;

    public void Show()
    {
        _open = true;
        _result = null;
    }

    public void Draw(EditorApp app)
    {
        if (!_open || app.Session is not { } session) return;
        ImGui.SetNextWindowSize(app.Scaled(620, 400), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Compact FormIDs") + "###compact", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            if (app.ActivePlugin is not { IsNew: false } active || session.Workspace.Active.FirstOrDefault(p => p.Name == active.Name) is not { Mod: { } mod } plugin)
            {
                ImGui.TextColored(Theme.Warning, T("Choose a saved active file first."));
                ImGui.End();
                return;
            }
            var plan = FormIdCompactor.Plan(mod, plugin.ModKey, session.Workspace.Active);
            ImGui.TextWrapped(TF($"{active.Name}: {plan.Own} own record(s), {plan.Moved} outside 0x800–0xFFF."));
            ImGui.TextColored(plan.Fits ? Theme.TextDim : Theme.Error, plan.Fits
                ? T("Fits a light plugin (at most 2048 own records).")
                : TF($"Too many own records for a light plugin ({plan.Own} > 2048): numbers can still be compacted, the light flag cannot be set."));
            if (plan.Dependents.Count > 0)
                ImGui.TextColored(Theme.Warning, TF($"These plugins use it and would point at the old numbers: {string.Join(", ", plan.Dependents)}"));
            if (app.Document.PendingCount > 0) ImGui.TextColored(Theme.Warning, T("Save the unsaved changes first (Ctrl+S)."));
            if (!plan.Fits) _light = false;
            ImGui.BeginDisabled(!plan.Fits);
            ImGui.Checkbox(T("Mark as light plugin (ESL flag)"), ref _light);
            ImGui.EndDisabled();
            ImGui.Checkbox(T("Rename FaceGen and voice files next to it"), ref _renameAssets);
            ImGui.BeginDisabled(app.Document.PendingCount > 0 || (plan.Moved == 0 && !_light));
            if (ImGui.Button(T("Compact"))) _result = Run(app, session, active, plan, _light, _renameAssets);
            ImGui.EndDisabled();
            if (_result is not null) ImGui.TextWrapped(_result);
            ImGui.TextColored(Theme.TextDim, T("The file is backed up first (as on save) and reloaded."));
        }
        ImGui.End();
    }

    /// <summary>Compacts the active file and reloads; returns a summary.</summary>
    public static string Run(EditorApp app, EditorSession session, StbEditor.Core.ActivePlugin active, CompactPlan plan, bool light, bool renameAssets)
    {
        string temp;
        int restored;
        try
        {
            var writer = new PluginWriter(session.Workspace, session.Catalog);
            temp = writer.Compact(active, plan.Map, light);
            restored = writer.LastCompactRestored;
        }
        catch (Exception ex)
        {
            Log.Error($"compact: {ex.Message}");
            return TF($"Not compacted: {ex.Message}");
        }
        var view = app.RenderWindow.CaptureView();
        var game = session.Game;
        app.CloseSession();
        var renamed = 0;
        try
        {
            Log.Info($"backup: {SaveController.Backup(active.Path)}");
            File.Move(temp, active.Path, overwrite: true);
            if (renameAssets) renamed = FormIdCompactor.RenameAssets(active.Path, plan.Map);
        }
        catch (Exception ex)
        {
            Log.Error($"compact: could not replace {active.Path}: {ex.Message} (the new file is {temp})");
        }
        var summary = TF($"{plan.Moved} record(s) renumbered, {restored} kept byte for byte, {renamed} FaceGen / voice file(s) renamed{(light ? T(", marked light") : "")}");
        Log.Info($"compact {active.Name}: {summary}");
        app.AfterSave(game, view, active.Path);
        return summary;
    }
}
