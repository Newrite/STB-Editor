using System.Numerics;
using Hexa.NET.ImGui;
using StbEditor.App.Ui;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>Help → Keys and features (F1): every shortcut and what each window does.</summary>
internal sealed class HelpWindow
{
    private bool _open;

    public void Show() => _open = true;

    public void Toggle() => _open = !_open;

    /// <summary>(keys, what they do) per section. Keep in step with RenderWindow.Editing / Navmesh / Landscape.</summary>
    private static readonly (string Section, (string Keys, string Action)[] Rows)[] Sections =
    [
        ("Camera (Render Window)",
        [
            ("RMB + mouse", "Look around"),
            ("RMB + W A S D", "Fly; Q / E down / up, Shift faster, Ctrl slower"),
            ("RMB + wheel", "Flying speed"),
            ("Wheel", "Move forward / back, smoothly (speed in View - Settings)"),
            ("MMB drag, or Space + mouse", "Pan"),
            ("Shift + mouse", "Orbit the selection (look around without one)"),
            ("Alt + LMB drag", "Orbit"),
            ("C, or double-click", "Frame the selection"),
            ("T", "Top view"),
        ]),
        ("Selecting and moving objects",
        [
            ("LMB", "Select; Ctrl adds to the selection; Esc clears it"),
            ("LMB drag on the selection", "Move along the ground; hold Z for height, X / Y to lock an axis. With the Rotate (E) or Scale (R) tool: turn or size it"),
            ("S + LMB drag", "Scale"),
            ("RMB drag on the selection", "Rotate about Z; hold X or Y for those axes"),
            ("Q / W / E / R", "Tool: select / move / rotate / scale (gizmo with arrows and rings)"),
            ("L", "Gizmo axes: world or the object's own"),
            ("G", "Snap to the grid (step next to it in the toolbar); Angle: snap rotation"),
            ("F", "Drop to the ground"),
            ("Del", "Delete the selection"),
            ("Ctrl+D", "Duplicate the selection"),
            ("Ctrl+C / Ctrl+X", "Copy / cut the selected objects (with their properties)"),
            ("Ctrl+V / Ctrl+Shift+V", "Paste under the cursor / in place (same coordinates; works in another cell too)"),
            ("Drag from the Object Window", "Place an object (also from Preview)"),
            ("Inspector: Placement", "Exact position, rotation in degrees and scale: drag the number, or double-click it and type"),
        ]),
        ("Doors and cells",
        [
            ("Double-click a door", "Go through it (asks first): opens the cell of the linked door, camera at its marker"),
            ("Inspector: Go to linked door", "The same from the door's properties"),
            ("Cell View: double-click a cell", "Load it; the list on the right shows its objects (click one to go to it)"),
            ("World menu", "New interior cell, new worldspace"),
            ("grid ±N (toolbar)", "How many exterior cells load around the centre one"),
        ]),
        ("Landscape (H)",
        [
            ("H", "Landscape tool on / off (Landscape panel: brush, radius, strength, textures)"),
            ("LMB drag", "Raise, lower, flatten, smooth or paint"),
            ("Shift + LMB", "Swap raise and lower"),
        ]),
        ("Navmesh (N)",
        [
            ("N", "Navmesh editing on / off"),
            ("LMB", "Select a vertex or triangle; Ctrl adds"),
            ("LMB drag", "Move vertices; hold Z for height"),
            ("Shift + LMB on the ground", "New vertex; with two selected it also makes the triangle"),
            ("Del / F / Esc", "Delete, drop to the ground, clear the selection"),
        ]),
        ("Files and editing",
        [
            ("Ctrl+S", "Save into the active file (asks for one the first time)"),
            ("Ctrl+Z / Ctrl+Y", "Undo / redo"),
            ("Ctrl+O", "Data: game, MO2 profile"),
            ("Ctrl+= / Ctrl+- / Ctrl+0", "Interface larger / smaller / 100 % (View → Settings → Interface size)"),
            ("Ctrl+F", "Search in the Object Window"),
            ("Alt+Left / Alt+Right", "Back / forward through selected records"),
            ("Double-click a record (Object Window)", "Its form in a separate window, tabs as in the CK; several can be open"),
            ("Inspector / form: tabs", "Edit fields right there; lists: + add, x removes a row; «!» = the file stores it differently"),
            ("Inspector: Scripts", "Attach scripts and set their properties (from .pex, no compiling)"),
            ("Ctrl+click / Shift+click", "select several records (Object Window); right-click → Compare selected"),
            ("Object Window: + New", "A new record of the shown type in the active file; right-click → New also works"),
            ("Object Window: Del", "Delete the record from the active file, or remove its override (Ctrl+Z brings it back)"),
            ("Object Window: All records / Overridden / Conflicts", "Which records to list; next to it — only one plugin's records"),
            ("File → Masters", "Add a master to the active file, remove unused ones"),
            ("Right-click a record → Compare versions", "every plugin's version side by side, coloured as in xEdit"),
        ]),
        ("View",
        [
            ("M / B / Shift+W", "Markers / cell borders / wireframe"),
            ("Toolbar: NPC", "Tint NPCs and creatures so they are easy to find"),
            ("Toolbar: FX, Anim", "Particles and animation off for weak PCs"),
            ("View menu", "Windows (Log, Load Order...), Settings (wheel and flying speed, door question), frame limit, language"),
            ("F1", "This window"),
        ]),
    ];

    public void Draw(EditorApp app)
    {
        if (ImGui.IsKeyPressed(ImGuiKey.F1, false)) _open = !_open;
        if (!_open) return;

        ImGui.SetNextWindowSize(app.Scaled(720, 640), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(Title("Keys and features"), ref _open))
        {
            ImGui.TextColored(Theme.TextDim, T("Controls follow the Creation Kit where it has them."));
            foreach (var (section, rows) in Sections)
            {
                if (!ImGui.CollapsingHeader(T(section), ImGuiTreeNodeFlags.DefaultOpen)) continue;
                if (!ImGui.BeginTable("##" + section, 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) continue;
                ImGui.TableSetupColumn("keys", ImGuiTableColumnFlags.WidthStretch, 0.38f);
                ImGui.TableSetupColumn("action", ImGuiTableColumnFlags.WidthStretch, 0.62f);
                foreach (var (keys, action) in rows)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextColored(Theme.Accent, T(keys));
                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(T(action));
                }
                ImGui.EndTable();
            }
            ImGui.Spacing();
            ImGui.TextColored(Theme.TextDim, T("More in README.md (GitHub page of the project)."));
        }
        ImGui.End();
    }
}
