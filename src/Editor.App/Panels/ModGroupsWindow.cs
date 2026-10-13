using System.Numerics;
using Hexa.NET.ImGui;
using StbEditor.App.Ui;
using StbEditor.Core;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// File → ModGroups (xEdit): groups of plugins that belong together; with every plugin of a group loaded, the Object Window's
/// conflict filter sees only the group's last version of a record. Saved in Documents\STB Editor\modgroups.json.
/// </summary>
internal sealed class ModGroupsWindow
{
    private bool _open;
    private int _selected = -1;
    private string _newName = "";
    private string _filter = "";

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "STB Editor", "modgroups.json");

    public void Show() => _open = true;

    public void Draw(EditorApp app)
    {
        if (!_open || app.Session is not { } session) return;
        ImGui.SetNextWindowSize(app.Scaled(640, 520), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("ModGroups") + "###modgroups", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            var groups = app.ModGroupList;
            var loaded = session.Workspace.Active.Select(p => p.Name).ToList();
            var active = ModGroups.Active(groups, loaded);
            ImGui.TextWrapped(T("Plugins that belong together (a mod and its patches): when all of a group are loaded, conflicts between them are not reported — only the group's last version of a record is compared."));
            ImGui.BeginChild("##groups", new Vector2(ImGui.GetFontSize() * 14, 0), ImGuiChildFlags.Borders);
            for (var i = 0; i < groups.Count; i++)
            {
                var on = active.Contains(groups[i]);
                if (!on) ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
                if (ImGui.Selectable($"{groups[i].Name} ({groups[i].Plugins.Count})##g{i}", i == _selected)) _selected = i;
                if (!on) ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(on ? T("Active: every plugin is loaded") : T("Not active: not every plugin is loaded, or fewer than two"));
            }
            ImGui.Separator();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##groupname", T("new group name"), ref _newName, 96);
            if (ImGui.SmallButton(T("Add group")) && _newName.Trim().Length > 0)
            {
                groups.Add(new ModGroup(_newName.Trim(), []));
                _selected = groups.Count - 1;
                _newName = "";
                Changed(app);
            }
            ImGui.EndChild();
            ImGui.SameLine();
            ImGui.BeginGroup();
            if (_selected >= 0 && _selected < groups.Count)
            {
                var group = groups[_selected];
                ImGui.Text(group.Name);
                ImGui.SameLine();
                if (ImGui.SmallButton(T("Delete group")))
                {
                    groups.RemoveAt(_selected);
                    _selected = -1;
                    Changed(app);
                }
                else
                {
                    ImGui.SetNextItemWidth(-1);
                    ImGui.InputTextWithHint("##pluginfilter", T("Filter plugins"), ref _filter, 128);
                    ImGui.BeginChild("##groupplugins", Vector2.Zero, ImGuiChildFlags.Borders);
                    foreach (var name in group.Plugins.Where(p => !loaded.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList())
                    {
                        var member = true;
                        if (ImGui.Checkbox($"{name}  {T("(not loaded)")}##m{name}", ref member) && !member) { group.Plugins.Remove(name); Changed(app); }
                    }
                    foreach (var name in loaded)
                    {
                        if (_filter.Length > 0 && !name.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;
                        var member = group.Plugins.Contains(name, StringComparer.OrdinalIgnoreCase);
                        if (ImGui.Checkbox($"{name}##p{name}", ref member))
                        {
                            if (member) group.Plugins.Add(name);
                            else group.Plugins.RemoveAll(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
                            // In load order, as xEdit keeps them.
                            group.Plugins.Sort((a, b) => loaded.FindIndex(x => x.Equals(a, StringComparison.OrdinalIgnoreCase)).CompareTo(loaded.FindIndex(x => x.Equals(b, StringComparison.OrdinalIgnoreCase))));
                            Changed(app);
                        }
                    }
                    ImGui.EndChild();
                }
            }
            else ImGui.TextColored(Theme.TextDim, T("Choose or add a group."));
            ImGui.EndGroup();
        }
        ImGui.End();
    }

    private static void Changed(EditorApp app)
    {
        ModGroups.Save(FilePath, app.ModGroupList);
        app.ObjectWindow.ResetConflicts();
    }
}
