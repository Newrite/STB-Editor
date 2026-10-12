using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using StbEditor.App.Services;
using StbEditor.Core;
using StbEditor.Render;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// Manual navmesh editing (CK's navmesh mode, without generation): click selects a vertex or a
/// triangle (Ctrl adds), LMB drag moves the selection (Z: height), Shift+click on the ground adds a
/// vertex — with two vertices selected it also makes the triangle, so strips grow click by click.
/// Del deletes, F drops vertices to the ground, and the Navmesh panel has merge, flags and more.
/// Each operation is one undo step on an edited copy of the NAVM.
/// </summary>
internal sealed partial class RenderWindow
{
    private NavmeshSelection? _navSelection;
    private NavigationMesh? _navDrag;
    private Vector3 _navDragStart;
    private Dictionary<int, Vector3>? _navDragOrigin;
    private bool _navPressed;
    private Vector2 _navPressMouse;

    public bool NavmeshMode => _tool == EditTool.Navmesh;
    public NavmeshSelection? NavSelection => _navSelection;

    public void SetNavmeshMode(bool on)
    {
        _tool = on ? EditTool.Navmesh : EditTool.Select;
        if (on && _renderer is not null) _renderer.Options.ShowNavmesh = true;
    }

    /// <summary>The navmesh a view shows, as a record for reading.</summary>
    private NavmeshView? View(FormKey key) => _scene?.Navmeshes.FirstOrDefault(v => v.Key == key);

    private void HandleNavmesh(EditorApp app, Vector2 mouse, Vector2 size, bool hovered)
    {
        if (_scene is null || app.Session is null) return;
        if (_renderer is not null) _renderer.Options.ShowNavmesh = true;
        var io = ImGui.GetIO();
        var (origin, direction) = _camera.Ray(mouse, size);

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !io.KeyAlt)
        {
            _navPressed = true;
            _navPressMouse = io.MousePos;

            if (io.KeyShift)
            {
                AddVertex(app, origin, direction);
                _navPressed = false;
            }
            else
            {
                var hit = PickNavmesh(mouse, size, origin, direction);
                var keep = io.KeyCtrl;
                if (hit is { } h)
                {
                    if (_navSelection is null || _navSelection.Mesh != h.Mesh || !keep)
                    {
                        var alreadySelected = _navSelection is { } s && s.Mesh == h.Mesh
                            && (h.Vertex is { } v0 ? s.Vertices.Contains(v0) : s.Triangles.Contains(h.Triangle));
                        if (!alreadySelected) _navSelection = new NavmeshSelection { Mesh = h.Mesh };
                    }
                    var sel = _navSelection!;
                    if (h.Vertex is { } vertex)
                    {
                        if (keep && sel.Vertices.Contains(vertex)) sel.Vertices.Remove(vertex);
                        else sel.Vertices.Add(vertex);
                    }
                    else if (keep && sel.Triangles.Contains(h.Triangle)) sel.Triangles.Remove(h.Triangle);
                    else sel.Triangles.Add(h.Triangle);
                    _navDragStart = h.Point;
                }
                else if (!keep)
                {
                    _navSelection = null;
                    _navPressed = false;
                }
            }
        }

        // Drag: the selection follows the cursor on the plane of the press point (Z: height).
        if (_navPressed && ImGui.IsMouseDown(ImGuiMouseButton.Left) && _navSelection is { IsEmpty: false } selection
            && Vector2.Distance(io.MousePos, _navPressMouse) > 3)
        {
            if (_navDrag is null && View(selection.Mesh) is { } view)
            {
                _navDrag = (NavigationMesh)_document!.WorkingCopy(selection.Mesh, view.Record);
                _navDragOrigin = DragVertices(selection, _navDrag).ToDictionary(i => i, i => P(_navDrag.Data!.Vertices[i]));
            }

            if (_navDrag is not null && _navDragOrigin is not null)
            {
                Vector3 delta;
                if (ImGui.IsKeyDown(ImGuiKey.Z))
                {
                    delta = new Vector3(0, 0, -(io.MousePos.Y - _navPressMouse.Y) * Math.Max(Vector3.Distance(_camera.Position, _navDragStart), 100f) * 0.002f);
                }
                else
                {
                    var plane = _navDragStart.Z;
                    var t = MathF.Abs(direction.Z) > 1e-4f ? (plane - origin.Z) / direction.Z : -1;
                    var at = t > 0 ? origin + direction * t : _navDragStart;
                    delta = new Vector3(at.X - _navDragStart.X, at.Y - _navDragStart.Y, 0);
                }

                foreach (var (index, start) in _navDragOrigin)
                {
                    var p = start + delta;
                    _navDrag.Data!.Vertices[index] = new P3Float(p.X, p.Y, p.Z);
                }
                if (View(selection.Mesh) is { } live)
                {
                    live.Record = _navDrag;
                    live.Version++;
                }
            }
        }

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (_navDrag is not null && _navSelection is { } done)
            {
                NavmeshTools.Rebuild(_navDrag);
                CommitNavmesh(T("Move navmesh vertices"), done.Mesh, _navDrag);
            }
            _navDrag = null;
            _navDragOrigin = null;
            _navPressed = false;
        }

        if ((ImGui.IsWindowFocused() || hovered) && !io.WantTextInput && _navSelection is { IsEmpty: false })
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Delete, false)) DeleteNavmeshSelection();
            if (ImGui.IsKeyPressed(ImGuiKey.F, false)) DropNavmeshVertices();
            if (ImGui.IsKeyPressed(ImGuiKey.Escape, false)) _navSelection = null;
        }

        if (_renderer is not null) _renderer.NavSelection = _navSelection;
    }

    private static Vector3 P(P3Float p) => new(p.X, p.Y, p.Z);

    /// <summary>Vertices the selection moves: selected ones and the corners of selected triangles.</summary>
    private static HashSet<int> DragVertices(NavmeshSelection selection, INavigationMeshGetter mesh)
    {
        var result = new HashSet<int>(selection.Vertices);
        foreach (var t in selection.Triangles)
        {
            if (t >= mesh.Data!.Triangles.Count) continue;
            var v = mesh.Data.Triangles[t].Vertices;
            result.Add(v.X);
            result.Add(v.Y);
            result.Add(v.Z);
        }
        return result;
    }

    private readonly record struct NavHit(FormKey Mesh, int? Vertex, int Triangle, Vector3 Point);

    /// <summary>A vertex within 10 px of the cursor wins; else the triangle under it.</summary>
    private NavHit? PickNavmesh(Vector2 mouse, Vector2 size, Vector3 origin, Vector3 direction)
    {
        var viewProj = _camera.View * _camera.Projection(size.X / size.Y);
        NavHit? best = null;
        var bestPixels = 10f;
        float? bestDistance = null;

        foreach (var view in _scene!.Navmeshes)
        {
            var data = view.Record.Data!;
            for (var i = 0; i < data.Vertices.Count; i++)
            {
                var p = P(data.Vertices[i]);
                var clip = Vector4.Transform(new Vector4(p, 1), viewProj);
                if (clip.W <= 0) continue;
                var screen = new Vector2((clip.X / clip.W * 0.5f + 0.5f) * size.X, (0.5f - clip.Y / clip.W * 0.5f) * size.Y);
                var pixels = Vector2.Distance(screen, mouse);
                if (pixels < bestPixels)
                {
                    bestPixels = pixels;
                    best = new NavHit(view.Key, i, -1, p);
                }
            }
        }
        if (best is not null) return best;

        foreach (var view in _scene.Navmeshes)
        {
            var data = view.Record.Data!;
            for (var t = 0; t < data.Triangles.Count; t++)
            {
                var v = data.Triangles[t].Vertices;
                var hit = Picking.RayTriangle(origin, direction, P(data.Vertices[v.X]), P(data.Vertices[v.Y]), P(data.Vertices[v.Z]));
                if (hit is { } d && (bestDistance is null || d < bestDistance))
                {
                    bestDistance = d;
                    best = new NavHit(view.Key, null, t, origin + direction * d);
                }
            }
        }
        return best;
    }

    private void CommitNavmesh(string name, FormKey key, NavigationMesh edited)
    {
        var view = View(key);
        var interior = _scene?.IsInterior ?? true;
        _document!.CommitEdit(name, edited, RecordPlace.Navmesh, cell: view?.Cell ?? _cell, world: interior ? null : _scene?.Worldspace);
    }

    /// <summary>Applies an operation to a working copy of the selected navmesh and commits it.</summary>
    public void EditNavmesh(string name, Action<NavigationMesh, NavmeshSelection> operation)
    {
        if (_navSelection is not { } selection || View(selection.Mesh) is not { } view || _document is null) return;
        var copy = (NavigationMesh)_document.WorkingCopy(selection.Mesh, view.Record);
        operation(copy, selection);
        NavmeshTools.Rebuild(copy);
        CommitNavmesh(name, selection.Mesh, copy);
    }

    public void DeleteNavmeshSelection()
    {
        if (_navSelection is not { } selection || View(selection.Mesh) is not { } view || _document is null) return;
        var copy = (NavigationMesh)_document.WorkingCopy(selection.Mesh, view.Record);
        var doomed = new HashSet<int>(selection.Triangles);
        for (var t = 0; t < copy.Data!.Triangles.Count; t++)
        {
            var v = copy.Data.Triangles[t].Vertices;
            if (selection.Vertices.Contains(v.X) || selection.Vertices.Contains(v.Y) || selection.Vertices.Contains(v.Z)) doomed.Add(t);
        }
        if (doomed.Count == 0) return;
        if (copy.Data.DoorTriangles.Count > 0)
            Log.Info("navmesh: it has door triangles — press Finalize after editing (door links and NAVI)");

        var map = NavmeshTools.DeleteTriangles(copy, doomed);
        CommitWithNeighbours(TF($"Delete {doomed.Count} navmesh triangle(s)"), selection.Mesh, copy, map);
        _navSelection = null;
    }

    /// <summary>One undo step for the edited navmesh and every loaded neighbour whose links into it were renumbered.</summary>
    private void CommitWithNeighbours(string name, FormKey key, NavigationMesh edited, int[] map)
    {
        var steps = new List<IEditCommand>();
        void Step(FormKey k, NavigationMesh mesh)
        {
            var view = View(k);
            var before = _document!.Override(k);
            var after = before?.With(mesh) ?? new OverrideDraft(mesh, RecordPlace.Navmesh, null, view?.Cell, view?.World);
            steps.Add(new RecordEditCommand(name, before?.With((IMajorRecord)before.Record.DeepCopy()), after));
        }

        Step(key, edited);
        var neighbours = 0;
        foreach (var other in _scene!.Navmeshes.Where(v => v.Key != key))
        {
            if (other.Record.Data?.EdgeLinks.Any(l => l.Mesh.FormKey == key) != true) continue;
            var copy = (NavigationMesh)_document!.WorkingCopy(other.Key, other.Record);
            if (!NavmeshTools.RemapLinksInto(copy, key, map)) continue;
            NavmeshTools.Rebuild(copy);
            Step(other.Key, copy);
            neighbours++;
        }
        _document!.Record(new CompositeCommand(name, steps));
        if (neighbours > 0) Log.Info($"navmesh: links of {neighbours} neighbouring navmesh(es) renumbered");
    }

    public void DropNavmeshVertices()
    {
        if (_scene is null) return;
        var scene = _scene;
        EditNavmesh(T("Drop navmesh vertices"), (mesh, selection) =>
        {
            foreach (var i in DragVertices(selection, mesh))
            {
                var p = P(mesh.Data!.Vertices[i]);
                if (Picking.CastDown(scene, p + new Vector3(0, 0, 64), new HashSet<SceneObject>(), _renderer!.Options) is { } z)
                    mesh.Data.Vertices[i] = new P3Float(p.X, p.Y, z);
            }
        });
    }

    /// <summary>Selected vertices become one (at the first one's place); triangles that collapse go.</summary>
    public void MergeNavmeshVertices()
    {
        if (_navSelection is not { Vertices.Count: >= 2 } selection) return;
        var keep = selection.Vertices.Min();
        if (View(selection.Mesh) is not { } view || _document is null) return;
        var mesh = (NavigationMesh)_document.WorkingCopy(selection.Mesh, view.Record);
        var data = mesh.Data!;
        var doomed = new HashSet<int>();
        for (var t = 0; t < data.Triangles.Count; t++)
        {
            var tri = data.Triangles[t];
            short Map(short v) => selection.Vertices.Contains(v) ? (short)keep : v;
            var v2 = new P3Int16(Map(tri.Vertices.X), Map(tri.Vertices.Y), Map(tri.Vertices.Z));
            tri.Vertices = v2;
            if (v2.X == v2.Y || v2.Y == v2.Z || v2.X == v2.Z) doomed.Add(t);
        }
        var map = NavmeshTools.DeleteTriangles(mesh, doomed);
        CommitWithNeighbours(T("Merge navmesh vertices"), selection.Mesh, mesh, map);
        _navSelection = new NavmeshSelection { Mesh = selection.Mesh };
    }

    /// <summary>Three selected vertices → a triangle, counter-clockwise seen from above (as the game expects).</summary>
    public void CreateNavmeshTriangle()
    {
        if (_navSelection is not { Vertices.Count: 3 } selection) return;
        var corners = selection.Vertices.ToArray();
        EditNavmesh(T("Create navmesh triangle"), (mesh, _) => AddTriangle(mesh, corners[0], corners[1], corners[2]));
    }

    private static void AddTriangle(NavigationMesh mesh, int a, int b, int c)
    {
        var data = mesh.Data!;
        var pa = P(data.Vertices[a]);
        var pb = P(data.Vertices[b]);
        var pc = P(data.Vertices[c]);
        if (Vector3.Cross(pb - pa, pc - pa).Z < 0) (b, c) = (c, b);
        data.Triangles.Add(new NavmeshTriangle
        {
            Vertices = new P3Int16((short)a, (short)b, (short)c),
            EdgeLink_0_1 = -1,
            EdgeLink_1_2 = -1,
            EdgeLink_2_0 = -1,
        });
    }

    /// <summary>Shift+click: a vertex on the ground; with two selected, also the triangle (strip building).</summary>
    private void AddVertex(EditorApp app, Vector3 origin, Vector3 direction)
    {
        if (_scene is null || _document is null) return;
        var point = Picking.Pick(_scene, origin, direction, _renderer!.Options)?.Point ?? Picking.PickTerrain(_scene, origin, direction);
        if (point is not { } p)
        {
            Log.Warn("navmesh: nothing under the cursor to put a vertex on");
            return;
        }

        var target = _navSelection?.Mesh is { } selected && View(selected) is { } current ? current : NearestNavmesh(p);
        if (target is null)
        {
            CreateNavmesh(app, p);
            return;
        }

        var previous = _navSelection is { } sel && sel.Mesh == target.Key ? sel.Vertices.ToList() : [];
        var copy = (NavigationMesh)_document.WorkingCopy(target.Key, target.Record);
        var index = copy.Data!.Vertices.Count;
        copy.Data.Vertices.Add(new P3Float(p.X, p.Y, p.Z));
        if (previous.Count == 2) AddTriangle(copy, previous[0], previous[1], index);
        NavmeshTools.Rebuild(copy);
        CommitNavmesh(previous.Count == 2 ? "Add navmesh triangle" : "Add navmesh vertex", target.Key, copy);

        // Keep growing a strip: the last two vertices stay selected.
        _navSelection = new NavmeshSelection { Mesh = target.Key };
        if (previous.Count == 2) _navSelection.Vertices.Add(previous[1]);
        _navSelection.Vertices.Add(index);
    }

    private NavmeshView? NearestNavmesh(Vector3 point)
    {
        NavmeshView? best = null;
        var bestDistance = float.MaxValue;
        foreach (var view in _scene!.Navmeshes)
        {
            foreach (var v in view.Record.Data!.Vertices)
            {
                var d = Vector3.DistanceSquared(P(v), point);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = view;
                }
            }
        }
        // Only the navmesh of the cell the point is in, when there is one.
        return best is not null && bestDistance < 4096f * 4096f ? best : null;
    }

    /// <summary>First vertex of a cell without navmesh: a new NAVM of the active file.</summary>
    private void CreateNavmesh(EditorApp app, Vector3 point)
    {
        if (app.ActivePlugin is not { } active || app.Session is not { } session)
        {
            app.ShowActiveFileDialog(T("A new navmesh goes into the active file — choose it first."));
            return;
        }

        FormKey cell;
        ANavmeshParent parent;
        if (_scene!.IsInterior)
        {
            cell = _cell ?? FormKey.Null;
            var cellParent = new CellNavmeshParent();
            cellParent.Parent.SetTo(cell);
            parent = cellParent;
        }
        else
        {
            var gx = (int)MathF.Floor(point.X / TerrainCell.CellSize);
            var gy = (int)MathF.Floor(point.Y / TerrainCell.CellSize);
            if (Builder(session).CellAt(_scene.Worldspace!.Value, gx, gy) is not { } c)
            {
                Log.Warn("navmesh: no cell there");
                return;
            }
            cell = c;
            var worldParent = new WorldspaceNavmeshParent { Coordinates = new P2Int16((short)gx, (short)gy) };
            worldParent.Parent.SetTo(_scene.Worldspace!.Value);
            parent = worldParent;
        }

        var mesh = new NavigationMesh(active.Allocate(), session.Game.Release)
        {
            Data = new NavigationMeshData { NavmeshVersion = 12, Parent = parent, NavmeshGridDivisor = 1 },
        };
        mesh.Data.Vertices.Add(new P3Float(point.X, point.Y, point.Z));
        NavmeshTools.Rebuild(mesh);
        _document!.CommitEdit(T("New navmesh"), mesh, RecordPlace.Navmesh, cell: cell, world: _scene.IsInterior ? null : _scene.Worldspace);
        _navSelection = new NavmeshSelection { Mesh = mesh.FormKey };
        _navSelection.Vertices.Add(0);
        Log.Info("navmesh: new navmesh started — press Finalize when it is done (links to neighbours, NAVI entry)");
    }

    /// <summary>Autotests: select parts of a navmesh directly.</summary>
    public void SelectNavmeshForTest(FormKey mesh, IEnumerable<int> vertices, IEnumerable<int> triangles)
    {
        _navSelection = new NavmeshSelection { Mesh = mesh };
        _navSelection.Vertices.UnionWith(vertices);
        _navSelection.Triangles.UnionWith(triangles);
    }

    public void RefreshNavmeshesNow(EditorApp app) => RefreshNavmeshes(app);

    /// <summary>After undo/redo: navmesh views of the loaded cells from the plugins and the session's edits.</summary>
    private void RefreshNavmeshes(EditorApp app)
    {
        if (_scene is null || app.Session is not { } session || _navDrag is not null) return;
        var builder = Builder(session);
        var cells = _scene.Navmeshes.Select(n => (n.Cell, n.World)).ToHashSet();
        if (_scene.IsInterior && _cell is { } interior) cells.Add((interior, null));
        foreach (var draft in _document?.Drafts.Values.OfType<OverrideDraft>().Where(d => d.Place == RecordPlace.Navmesh) ?? [])
            if (draft.Cell is { } c && (draft.World == _scene.Worldspace || (_scene.IsInterior && c == _cell))) cells.Add((c, draft.World));

        _scene.Navmeshes.Clear();
        foreach (var (cell, world) in cells)
            _scene.Navmeshes.AddRange(builder.NavmeshesOf(cell, world));

        if (_navSelection is { } sel && _scene.Navmeshes.All(v => v.Key != sel.Mesh)) _navSelection = null;
    }
}
