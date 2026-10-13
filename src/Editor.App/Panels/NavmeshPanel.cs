using Hexa.NET.ImGui;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>Navmesh tools next to the render window: what is selected, triangle flags, operations.</summary>
internal sealed class NavmeshPanel : Panel
{
    private string? _summary;
    private int _findIndex;
    private int _findMesh;
    private NavmeshGenerationSettings _generate = new() { KeepUnreachedArea = 0 };
    private bool _keepUnreached;

    private static readonly (string Label, NavmeshTriangle.Flag Flag, string Tip)[] Flags =
    [
        (T("Water"), NavmeshTriangle.Flag.Water, T("Actors swim here")),
        (T("Preferred"), NavmeshTriangle.Flag.Preferred, T("Pathing prefers these triangles (roads, paths)")),
        (T("No large creatures"), NavmeshTriangle.Flag.NoLargeCreatures, T("Too tight for giants, dragons, mammoths")),
        (T("Overlapping"), NavmeshTriangle.Flag.Overlapping, T("Lies over another triangle (bridges, stairs over floors)")),
    ];

    public override string Name => "Navmesh";

    protected override void DrawContent(EditorApp app)
    {
        if (!RequireSession(app)) return;
        var window = app.RenderWindow;

        var editing = window.NavmeshMode;
        if (editing) ImGui.PushStyleColor(ImGuiCol.Button, Theme.ButtonActive);
        if (ImGui.Button(editing ? T("Editing navmesh (N)") : T("Edit navmesh (N)"))) window.SetNavmeshMode(!editing);
        if (editing) ImGui.PopStyleColor();

        var scene = window.Scene;
        if (scene is null)
        {
            ImGui.TextColored(Theme.TextDim, T("Load a cell first."));
            return;
        }

        ImGui.TextColored(Theme.TextDim, TF($"{scene.Navmeshes.Count} navmesh(es) loaded, {scene.Navmeshes.Sum(n => n.Record.Data?.Triangles.Count ?? 0):N0} triangles"));
        ImGui.TextWrapped(T("Click: vertex or triangle (Ctrl adds). Drag: move (hold Z for height). Shift+click on the ground: new vertex — with 2 selected it also makes the triangle. Del deletes, F drops to the ground."));
        if (ImGui.Button(T("Finalize edited"))) _summary = window.FinalizeNavmeshes(app, true) ?? _summary;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Door triangles and door links, edge links to neighbouring navmeshes, the navmesh info map (NAVI) — for the edited navmeshes (CK: Finalize)"));
        ImGui.SameLine();
        if (ImGui.Button(T("Finalize cell"))) _summary = window.FinalizeNavmeshes(app, false) ?? _summary;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("The same for every navmesh of the open cell (the centre cell outdoors)"));
        if (_summary is not null) ImGui.TextColored(Theme.TextDim, _summary);
        DrawGenerate(app, scene);
        DrawFindByIndex(app, scene);
        ImGui.Separator();

        if (window.NavSelection is not { } selection || selection.IsEmpty)
        {
            ImGui.TextColored(Theme.TextDim, T("Nothing selected."));
            DrawLegend();
            return;
        }

        var view = scene.Navmeshes.FirstOrDefault(n => n.Key == selection.Mesh);
        ImGui.Text(TF($"{app.Session!.Workspace.FormIdText(selection.Mesh)}: {selection.Vertices.Count} vertex(es), {selection.Triangles.Count} triangle(s)"));

        if (selection.Triangles.Count > 0 && view?.Record.Data is { } data)
        {
            foreach (var (label, flag, tip) in Flags)
            {
                var states = selection.Triangles.Where(t => t < data.Triangles.Count).Select(t => data.Triangles[t].Flags.HasFlag(flag)).ToList();
                var on = states.Count > 0 && states.All(s => s);
                var mixed = states.Distinct().Count() > 1;
                if (mixed) ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
                if (ImGui.Checkbox(T(label) + (mixed ? T(" (mixed)") : ""), ref on))
                {
                    var set = on;
                    window.EditNavmesh($"{(set ? "Set" : "Clear")} {label}", (mesh, sel) =>
                    {
                        foreach (var t in sel.Triangles)
                            if (t < mesh.Data!.Triangles.Count)
                                mesh.Data.Triangles[t].Flags = set ? mesh.Data.Triangles[t].Flags | flag : mesh.Data.Triangles[t].Flags & ~flag;
                    });
                }
                if (mixed) ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(T(tip));
            }
        }

        if (ImGui.Button(T("Delete (Del)"))) window.DeleteNavmeshSelection();
        ImGui.SameLine();
        if (ImGui.Button(T("Drop to ground (F)"))) window.DropNavmeshVertices();
        ImGui.BeginDisabled(selection.Vertices.Count != 3);
        if (ImGui.Button(T("Triangle from 3 vertices"))) window.CreateNavmeshTriangle();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(selection.Vertices.Count < 2);
        if (ImGui.Button(T("Merge vertices"))) window.MergeNavmeshVertices();
        ImGui.EndDisabled();
        ImGui.BeginDisabled(selection.Triangles.Count == 0);
        if (ImGui.Button(T("Move to a new navmesh"))) window.MoveNavmeshSelectionToNew(app);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(T("The selected triangles become a navmesh of their own in the same cell (CK: Move Selection to Separate NavMesh); press Finalize afterwards"));
        ImGui.EndDisabled();
        DrawLegend();
    }

    /// <summary>CK: Generate NavMesh for the open cell, with the actor's size and how closely it follows the geometry.</summary>
    private void DrawGenerate(EditorApp app, Render.Scene scene)
    {
        var window = app.RenderWindow;
        if (window.NavmeshGenerating)
        {
            ImGui.TextColored(Theme.TextDim, TF($"Generating navmesh… {window.NavmeshGenerationTime.TotalSeconds:0} s"));
            return;
        }
        if (ImGui.Button(scene.IsInterior ? T("Generate navmesh (cell)") : T("Generate navmesh (centre cell)")))
            window.GenerateNavmesh(app, _generate with { KeepUnreachedArea = _keepUnreached || !scene.IsInterior ? 40000 : 0 });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(T("CK: Generate NavMesh — the walkable ground of the cell from its meshes (with collision) and terrain. It replaces the triangles of the cell's biggest navmesh, or makes a new one in the active file; Finalize follows. One Ctrl+Z each."));
        if (window.NavmeshGenerationSummary is { } done) ImGui.TextColored(Theme.TextDim, done);
        if (!ImGui.TreeNode(T("Generation settings"))) return;
        var width = ImGui.GetFontSize() * 6;
        void Number(string label, float value, string tip, Func<float, NavmeshGenerationSettings> set)
        {
            ImGui.SetNextItemWidth(width);
            if (Fields.Float(label, value, out var v) && v > 0) _generate = set(v);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
        }
        Number(T("Actor radius"), _generate.AgentRadius, T("How far the navmesh keeps from walls (units; 70 = 1 m)"), v => _generate with { AgentRadius = v });
        Number(T("Actor height"), _generate.AgentHeight, T("Free room above the ground an actor needs"), v => _generate with { AgentHeight = v });
        Number(T("Step height"), _generate.AgentClimb, T("Highest step an actor walks up"), v => _generate with { AgentClimb = v });
        Number(T("Max slope"), _generate.MaxSlope, T("Steepest ground that is still walkable (degrees)"), v => _generate with { MaxSlope = Math.Min(v, 89) });
        Number(T("Cell size"), _generate.CellSize, T("Voxel size: smaller follows the walls closer, takes longer and makes more triangles"), v => _generate with { CellSize = Math.Max(v, 2), CellHeight = Math.Max(v, 2) / 2 });
        if (scene.IsInterior)
        {
            ImGui.Checkbox(T("Keep pieces no door reaches"), ref _keepUnreached);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Off: only the ground connected to a door is kept (no table tops, beams or the tops of the walls). On: big separate pieces stay too (rooms reached by a jump or a drop)."));
        }
        if (ImGui.SmallButton(T("Defaults"))) (_generate, _keepUnreached) = (new NavmeshGenerationSettings { KeepUnreachedArea = 0 }, false);
        ImGui.TreePop();
    }

    /// <summary>CK: Select Triangle by Index — and a vertex — in one of the loaded navmeshes.</summary>
    private void DrawFindByIndex(EditorApp app, Render.Scene scene)
    {
        if (scene.Navmeshes.Count == 0) return;
        var window = app.RenderWindow;
        if (window.NavSelection is { } selected && scene.Navmeshes.FindIndex(n => n.Key == selected.Mesh) is >= 0 and var at) _findMesh = at;
        _findMesh = Math.Clamp(_findMesh, 0, scene.Navmeshes.Count - 1);
        if (!ImGui.TreeNode(T("Find by index"))) return;
        var names = scene.Navmeshes.Select(n => app.Session!.Workspace.FormIdText(n.Key)).ToArray();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 9);
        ImGui.Combo("##findmesh", ref _findMesh, names, names.Length);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6);
        ImGui.InputInt("##findindex", ref _findIndex, 0);
        ImGui.SameLine();
        var mesh = scene.Navmeshes[_findMesh].Key;
        if (ImGui.SmallButton(T("triangle")) && !window.SelectNavmeshIndex(mesh, _findIndex, true)) Log.Warn(TF($"navmesh: no triangle {_findIndex}"));
        ImGui.SameLine();
        if (ImGui.SmallButton(T("vertex")) && !window.SelectNavmeshIndex(mesh, _findIndex, false)) Log.Warn(TF($"navmesh: no vertex {_findIndex}"));
        ImGui.TreePop();
    }

    private static void DrawLegend()
    {
        ImGui.Separator();
        ImGui.TextColored(Theme.TextDim, T("Colours: green walkable · yellow preferred · blue water · orange door · purple no large creatures."));
        ImGui.TextColored(Theme.TextDim, T("Edges: red = open border, light blue = linked to the next navmesh."));
        ImGui.TextColored(Theme.TextDim, T("After editing press Finalize."));
    }
}
