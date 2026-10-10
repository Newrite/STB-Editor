using Hexa.NET.ImGui;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Ui;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>Navmesh tools next to the render window: what is selected, triangle flags, operations.</summary>
internal sealed class NavmeshPanel : Panel
{
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
        DrawLegend();
    }

    private static void DrawLegend()
    {
        ImGui.Separator();
        ImGui.TextColored(Theme.TextDim, T("Colours: green walkable · yellow preferred · blue water · orange door · purple no large creatures."));
        ImGui.TextColored(Theme.TextDim, T("Edges: red = open border, light blue = linked to the next navmesh."));
        ImGui.TextColored(Theme.TextDim, T("Generation and finalize (NAVI, door links) are not done here yet."));
    }
}
