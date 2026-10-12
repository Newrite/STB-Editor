using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using StbEditor.App.Ui;
using StbEditor.Core;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// File → Masters: the active file's masters, which ones nothing points at, Add Master and
/// Clean Masters as in xEdit. Changes are a pending edit (Ctrl+Z undoes them) applied on save.
/// </summary>
internal sealed class MastersWindow
{
    private bool _open;
    private string _addFilter = string.Empty;
    private Task<HashSet<ModKey>>? _scan;
    private ModKey? _scannedFor;
    private object? _scannedSession;

    public void Show() => _open = true;

    public void Draw(EditorApp app)
    {
        if (!_open) return;
        ImGui.SetNextWindowSize(app.Scaled(560, 0), ImGuiCond.Appearing);
        if (ImGui.Begin(Title("Masters of the active file"), ref _open, ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.AlwaysAutoResize))
            DrawContent(app);
        ImGui.End();
    }

    private void DrawContent(EditorApp app)
    {
        if (app.Session is not { } session)
        {
            ImGui.TextColored(Theme.TextDim, T("Load data first (File → Data)."));
            return;
        }
        if (app.ActivePlugin is not { } active)
        {
            ImGui.TextColored(Theme.TextDim, T("No active file."));
            if (ImGui.Button(T("Active File..."))) app.ShowActiveFileDialog();
            return;
        }

        var plugin = session.Workspace.Active.FirstOrDefault(p => p.ModKey == active.ModKey);
        var current = !active.IsNew && File.Exists(active.Path) ? StbEditor.Data.Raw.PluginHeader.Read(active.Path).Masters : [];
        var pending = app.Document.Masters;

        // Which masters the plugin's records use: once per active file, in the background.
        if (plugin?.Mod is { } mod && (_scannedFor != active.ModKey || !ReferenceEquals(_scannedSession, session)))
        {
            _scannedFor = active.ModKey;
            _scannedSession = session;
            _scan = Task.Run(() => PluginWriter.UsedMasters(mod, active.ModKey));
        }
        var used = _scan is { IsCompletedSuccessfully: true } done ? done.Result : null;

        ImGui.Text(active.Name);
        if (active.IsNew)
            ImGui.TextColored(Theme.TextDim, T("A new plugin gets the masters its records use when it is saved; add more here if you need them."));

        if (current.Count > 0 && ImGui.BeginTable("##masters", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 2);
            ImGui.TableSetupColumn(T("Master"), ImGuiTableColumnFlags.WidthStretch, 0.65f);
            ImGui.TableSetupColumn(T("Status"), ImGuiTableColumnFlags.WidthStretch, 0.35f);
            ImGui.TableHeadersRow();
            for (var i = 0; i < current.Count; i++)
            {
                var key = ModKey.FromFileName(current[i]);
                var isUsed = used?.Contains(key);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, i.ToString("X2"));
                ImGui.TableNextColumn();
                ImGui.Text(current[i]);
                ImGui.TableNextColumn();
                if (isUsed == false && pending?.Clean == true) ImGui.TextColored(Theme.Error, T("removed on save"));
                else if (isUsed == false) ImGui.TextColored(Theme.Warning, T("not used"));
                else if (isUsed == true) ImGui.TextColored(Theme.TextDim, T("used"));
                else ImGui.TextColored(Theme.TextDim, T("checking…"));
            }
            foreach (var add in pending?.Add ?? [])
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TableNextColumn();
                ImGui.Text(add.FileName);
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.Good, T("added on save"));
            }
            ImGui.EndTable();
        }
        else if (pending?.Add is { Count: > 0 } adds)
        {
            foreach (var add in adds) ImGui.TextColored(Theme.Good, TF($"{add.FileName} — added on save"));
        }

        // Add: plugins loaded before the active file (a master must load earlier).
        var order = session.Workspace.Active.Select(p => p.ModKey).ToList();
        var self = order.IndexOf(active.ModKey);
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 16);
        if (ImGui.BeginCombo("##add", T("Add master..."), ImGuiComboFlags.HeightLarge))
        {
            ImGui.SetNextItemWidth(-1);
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
            ImGui.InputTextWithHint("##af", T("Filter plugins"), ref _addFilter, 128);
            foreach (var candidate in session.Workspace.Active)
            {
                var index = order.IndexOf(candidate.ModKey);
                if (candidate.ModKey == active.ModKey || (self >= 0 && index > self)) continue;
                if (current.Contains(candidate.Name, StringComparer.OrdinalIgnoreCase) || pending?.Add.Contains(candidate.ModKey) == true) continue;
                if (_addFilter.Length > 0 && !candidate.Name.Contains(_addFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (ImGui.Selectable($"{candidate.IndexLabel,-6} {candidate.Name}"))
                    app.Document.EditMasters(TF($"Add master {candidate.Name}"), new MasterEdit([.. pending?.Add ?? [], candidate.ModKey], pending?.Clean ?? false));
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Only plugins that load before the active file can be its masters"));

        ImGui.SameLine();
        var unused = used is null ? 0 : current.Count(m => !used.Contains(ModKey.FromFileName(m)));
        ImGui.BeginDisabled(unused == 0 || pending?.Clean == true);
        if (ImGui.Button(TF($"Remove unused masters ({unused})")))
            app.Document.EditMasters(T("Clean masters"), new MasterEdit(pending?.Add ?? [], true));
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(T("Masters no record of the active file points at (xEdit: Clean Masters)"));

        if (pending is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button(T("Cancel changes"))) app.Document.EditMasters(T("Cancel master changes"), null);
            ImGui.Spacing();
            ImGui.TextColored(Theme.Warning, T("Applied when you save (Ctrl+S); Ctrl+Z undoes it."));
            ImGui.TextColored(Theme.TextDim, T("A change of masters renumbers FormIDs, so the whole file is written anew; the save check lists anything that would come out differently."));
        }
    }
}
