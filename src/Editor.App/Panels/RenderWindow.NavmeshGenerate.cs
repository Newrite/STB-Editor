using System.Numerics;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using StbEditor.App.Services;
using StbEditor.Core;
using StbEditor.Nif;
using StbEditor.Render;
using Scene = StbEditor.Render.Scene;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// Generate NavMesh (CK): the walkable surface of the open interior cell (outdoors, of the centre cell) from its
/// meshes and terrain, made on a worker by <see cref="NavmeshGenerator"/> (Recast). The result replaces the triangles
/// of the cell's biggest navmesh — or becomes a new navmesh of the active file — in one undo step, and Finalize runs
/// after it (door triangles, edge links, NAVI) as a second one.
/// </summary>
internal sealed partial class RenderWindow
{
    private Task<GeneratedNavmesh>? _navGen;
    private Scene? _navGenScene;
    private FormKey _navGenCell;
    private DateTime _navGenStarted;
    private string? _navGenSummary;
    private List<WaterPlane> _navGenWater = [];

    /// <summary>The generation still running (the panel shows it, autotests wait for it).</summary>
    public bool NavmeshGenerating => _navGen is not null;
    public TimeSpan NavmeshGenerationTime => DateTime.UtcNow - _navGenStarted;

    /// <summary>What the last generation did (for the panel), null before the first one.</summary>
    public string? NavmeshGenerationSummary => _navGenSummary;

    /// <summary>Starts generating the open cell's navmesh; false (and a log line) when it cannot start.</summary>
    public bool GenerateNavmesh(EditorApp app, NavmeshGenerationSettings settings)
    {
        if (_navGen is not null || _scene is not { } scene || app.Session is not { } session || _document is null) return false;
        if (_cell is not { } cell)
        {
            Log.Warn(T("Generate navmesh: no cell open"));
            return false;
        }
        if (!scene.IsInterior && session.Catalog.Get(cell)?.Winner.Cell is not { IsInterior: false })
        {
            Log.Warn(T("Generate navmesh: the centre cell is not known"));
            return false;
        }

        // Outdoors: the centre cell's square (geometry a little beyond it still blocks and joins the borders).
        Vector3 min = new(-1e7f), max = new(1e7f);
        var clip = false;
        if (!scene.IsInterior && session.Catalog.Get(cell)?.Winner.Cell is { } exterior)
        {
            min = new Vector3(exterior.GridX * TerrainCell.CellSize, exterior.GridY * TerrainCell.CellSize, -1e7f);
            max = new Vector3(min.X + TerrainCell.CellSize, min.Y + TerrainCell.CellSize, 1e7f);
            clip = true;
        }
        var margin = new Vector3(512, 512, 0);
        var objects = scene.Objects.Where(o => !(_renderer?.Options.Hides(o) ?? false)).ToList();
        var terrain = scene.Terrain.ToList();
        var seeds = scene.IsInterior ? objects.Where(o => o.BaseSignature == "DOOR" && !o.IsDeleted).Select(o => o.Position).ToList() : null;
        var assets = session.Assets;
        var interior = scene.IsInterior;

        _navGenScene = scene;
        _navGenCell = cell;
        _navGenStarted = DateTime.UtcNow;
        _navGenSummary = null;
        _navGenWater = scene.Water.ToList();
        _navGen = Task.Run(() =>
        {
            // The NIF's collision is not kept on the GPU model: read each mesh once here.
            var models = new Dictionary<string, NavmeshGeometry.Source?>(StringComparer.OrdinalIgnoreCase);
            NavmeshGeometry.Source? Shapes(SceneObject o)
            {
                if (o.ModelPath is not { } path) return null;
                if (!models.TryGetValue(path, out var found))
                {
                    try { found = assets.ReadAllBytes(path) is { } bytes ? NavmeshGeometry.Source.Of(NifFile.Load(bytes, path)) : null; }
                    catch { found = null; }
                    models[path] = found;
                }
                return found;
            }
            var (vertices, indices) = NavmeshGeometry.Collect(objects, terrain, min - margin, max + margin, Shapes);
            if (indices.Count == 0) return new GeneratedNavmesh([], [], 0);
            var low = vertices.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var high = vertices.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            if (clip)
            {
                low = new Vector3(min.X, min.Y, low.Z);
                high = new Vector3(max.X, max.Y, high.Z);
            }
            var result = NavmeshGenerator.Generate(vertices, indices, low, high, settings, clip, seeds, roofed: interior);
            Log.Info($"navmesh generation: {indices.Count / 3:N0} triangles of {objects.Count} objects in, {result.Triangles.Count:N0} triangles out; {result.DroppedPieces} separate piece(s) left out ({result.DroppedTriangles} triangles)");
            return result;
        });
        Log.Info(TF($"Generating the navmesh of {session.Workspace.FormIdText(cell)}…"));
        return true;
    }

    /// <summary>Each frame: a finished generation goes into the cell's navmesh (one undo step), then Finalize.</summary>
    private void FinishNavmeshGeneration(EditorApp app)
    {
        if (_navGen is not { IsCompleted: true } task) return;
        _navGen = null;
        if (!task.IsCompletedSuccessfully)
        {
            _navGenSummary = TF($"Navmesh generation failed: {task.Exception?.GetBaseException().Message}");
            Log.Error(_navGenSummary);
            return;
        }
        if (_scene is null || _scene != _navGenScene || _cell != _navGenCell || app.Session is not { } session || _document is null)
        {
            _navGenSummary = T("Navmesh generation: another cell was opened meanwhile — the result was dropped");
            Log.Warn(_navGenSummary);
            return;
        }
        var generated = task.Result;
        if (generated.Triangles.Count == 0)
        {
            _navGenSummary = T("Navmesh generation: no walkable ground found (are the meshes loaded?)");
            Log.Warn(_navGenSummary);
            return;
        }
        if (generated.Vertices.Count > short.MaxValue)
        {
            _navGenSummary = TF($"Navmesh generation: {generated.Vertices.Count:N0} vertices is more than a navmesh holds ({short.MaxValue:N0}) — use a bigger cell size");
            Log.Warn(_navGenSummary);
            return;
        }

        var cell = _cell!.Value;
        var interior = _scene.IsInterior;
        var mine = _scene.Navmeshes.Where(v => v.Record.Data is not null && (interior || v.Cell == cell)).ToList();
        var target = mine.OrderByDescending(v => v.Record.Data!.Triangles.Count).FirstOrDefault();
        NavigationMesh mesh;
        if (target is not null) mesh = (NavigationMesh)_document.WorkingCopy(target.Key, target.Record);
        else if (NewNavmesh(app, cell) is { } fresh) mesh = fresh;
        else return;

        Fill(mesh.Data!, generated, _navGenWater);
        NavmeshTools.Rebuild(mesh);
        var name = TF($"Generate navmesh ({generated.Triangles.Count:N0} triangles)");
        if (target is not null) CommitWithNeighbours(name, target.Key, mesh, []);
        else _document.CommitEdit(name, mesh, RecordPlace.Navmesh, cell: cell, world: interior ? null : _scene.Worldspace);
        RefreshNavmeshes(app);
        _navSelection = null;
        if (_renderer is not null) _renderer.Options.ShowNavmesh = true;

        var others = mine.Count - (target is null ? 0 : 1);
        _navGenSummary = TF($"Generated {generated.Triangles.Count:N0} triangles in {NavmeshGenerationTime.TotalSeconds:0.0} s into {session.Workspace.FormIdText(mesh.FormKey)}")
            + (generated.DroppedPieces > 0 ? TF($"; {generated.DroppedPieces} separate piece(s) left out (table tops, roofs, ledges)") : "")
            + (others > 0 ? TF($"; the cell has {others} more navmesh(es) — delete what overlaps") : "");
        Log.Info(_navGenSummary);
        if (FinalizeNavmeshes(app, true) is { } finalized) _navGenSummary += "\n" + finalized;
    }

    /// <summary>The generated vertices and triangles in place of the old ones (door triangles and links: Finalize).</summary>
    private static void Fill(NavigationMeshData data, GeneratedNavmesh generated, List<WaterPlane> water)
    {
        data.Vertices.Clear();
        data.Vertices.AddRange(generated.Vertices.Select(v => new P3Float(v.X, v.Y, v.Z)));
        data.Triangles.Clear();
        data.EdgeLinks.Clear();
        data.DoorTriangles.Clear();
        foreach (var (a, b, c) in generated.Triangles)
        {
            var centre = (generated.Vertices[a] + generated.Vertices[b] + generated.Vertices[c]) / 3;
            var wet = water.Any(w => centre.X >= w.Min.X && centre.X <= w.Max.X && centre.Y >= w.Min.Y && centre.Y <= w.Max.Y && centre.Z < w.Height);
            data.Triangles.Add(new NavmeshTriangle
            {
                Vertices = new P3Int16((short)a, (short)b, (short)c),
                EdgeLink_0_1 = -1,
                EdgeLink_1_2 = -1,
                EdgeLink_2_0 = -1,
                // Every triangle of the game's navmeshes has Found.
                Flags = NavmeshTriangle.Flag.Found | (wet ? NavmeshTriangle.Flag.Water : 0),
            });
        }
    }

    /// <summary>An empty navmesh of the active file for <paramref name="cell"/> (not committed); null when there is no active file.</summary>
    private NavigationMesh? NewNavmesh(EditorApp app, FormKey cell)
    {
        if (app.ActivePlugin is not { } active || app.Session is not { } session)
        {
            app.ShowActiveFileDialog(T("A new navmesh goes into the active file — choose it first."));
            return null;
        }
        ANavmeshParent parent;
        if (_scene!.IsInterior || session.Catalog.Get(cell)?.Winner.Cell is not { IsInterior: false } exterior)
        {
            var cellParent = new CellNavmeshParent();
            cellParent.Parent.SetTo(cell);
            parent = cellParent;
        }
        else
        {
            var worldParent = new WorldspaceNavmeshParent { Coordinates = new P2Int16((short)exterior.GridX, (short)exterior.GridY) };
            worldParent.Parent.SetTo(_scene.Worldspace!.Value);
            parent = worldParent;
        }
        return new NavigationMesh(active.Allocate(), session.Game.Release)
        {
            Data = new NavigationMeshData { NavmeshVersion = 12, Parent = parent, NavmeshGridDivisor = 1 },
        };
    }

    /// <summary>Autotests: the whole open cell from above.</summary>
    public void TopViewForTest()
    {
        if (_scene is null) return;
        var points = _scene.Navmeshes.SelectMany(n => n.Record.Data?.Vertices ?? []).Select(P).ToList();
        if (points.Count == 0) return;
        var low = points.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var high = points.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        var centre = (low + high) / 2;
        var height = Math.Max(high.X - low.X, high.Y - low.Y) * 1.1f + 500;
        _camera.Pitch = -1.5533f;
        _camera.Position = new Vector3(centre.X, centre.Y - height * 0.0175f, high.Z + height);
    }
}
