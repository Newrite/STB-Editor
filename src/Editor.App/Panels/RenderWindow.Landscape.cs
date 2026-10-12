using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using StbEditor.App.Services;
using StbEditor.Core;
using StbEditor.Render;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

internal enum LandBrushMode { Raise, Lower, Flatten, Smooth, Paint }

/// <summary>Landscape brush settings, shared by the Landscape panel and the render window.</summary>
internal sealed class LandscapeBrush
{
    public LandBrushMode Mode { get; set; } = LandBrushMode.Raise;
    public float Radius { get; set; } = 512f;

    /// <summary>Height brushes: units per second at the centre. Paint/flatten/smooth: 1–100 (% per ~0.1 s).</summary>
    public float Strength { get; set; } = 200f;

    public FormKey? Texture { get; set; }
}

/// <summary>
/// Landscape editing (CK: H). LMB drag paints the brush on the land under the cursor; one stroke is
/// one undo step. Edited cells become <see cref="LandDraft"/>s; shared edges between cells are
/// kept equal, so no seams open.
/// </summary>
internal sealed partial class RenderWindow
{
    public LandscapeBrush Brush { get; } = new();

    public bool LandscapeMode => _tool == EditTool.Land;

    public void SetLandscapeMode(bool on) => _tool = on ? EditTool.Land : EditTool.Select;

    private readonly Dictionary<(int X, int Y), (LandDraft Draft, LandscapeData? Before)> _stroke = new();
    private Vector3? _brushPoint;
    private float _flattenHeight;
    private bool _strokeFull;

    private void HandleLandscape(EditorApp app, Vector2 mouse, Vector2 size, bool hovered, float delta)
    {
        if (_scene is not { IsInterior: false, Worldspace: { } world } scene || app.Session is not { } session)
        {
            _brushPoint = null;
            return;
        }

        var (origin, direction) = _camera.Ray(mouse, size);
        _brushPoint = hovered ? Picking.PickTerrain(scene, origin, direction) : null;
        var io = ImGui.GetIO();

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !io.KeyAlt && _brushPoint is { } start)
        {
            _stroke.Clear();
            _strokeFull = false;
            _flattenHeight = start.Z;
        }

        if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && !io.KeyAlt && _brushPoint is { } point && _drag == DragKind.None)
            ApplyBrush(session, scene, world, point, delta, io.KeyShift);

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && _stroke.Count > 0) EndStroke();
    }

    private void ApplyBrush(EditorSession session, Scene scene, FormKey world, Vector3 point, float delta, bool invert)
    {
        var brush = Brush;
        var radius = Math.Max(brush.Radius, 16f);
        var mode = brush.Mode;
        if (invert && mode == LandBrushMode.Raise) mode = LandBrushMode.Lower;
        else if (invert && mode == LandBrushMode.Lower) mode = LandBrushMode.Raise;
        if (mode == LandBrushMode.Paint && brush.Texture is null) return;

        var builder = Builder(session);
        var touched = new List<int>();

        for (var t = 0; t < scene.Terrain.Count; t++)
        {
            var terrain = scene.Terrain[t];
            if (terrain.Data is null) continue;
            var o = terrain.Origin;
            if (point.X + radius < o.X || point.X - radius > o.X + TerrainCell.CellSize
                || point.Y + radius < o.Y || point.Y - radius > o.Y + TerrainCell.CellSize) continue;

            var data = StrokeData(builder, terrain, world);
            var changed = false;

            // Smooth reads the neighbourhood before writing.
            var source = mode == LandBrushMode.Smooth ? (float[])data.Heights.Clone() : data.Heights;

            for (var vy = 0; vy < TerrainCell.Size; vy++)
            {
                for (var vx = 0; vx < TerrainCell.Size; vx++)
                {
                    var dx = o.X + vx * TerrainCell.Spacing - point.X;
                    var dy = o.Y + vy * TerrainCell.Spacing - point.Y;
                    var distance = MathF.Sqrt(dx * dx + dy * dy);
                    if (distance >= radius) continue;

                    var f = 1f - distance / radius;
                    var falloff = f * f * (3f - 2f * f);
                    var i = vy * TerrainCell.Size + vx;
                    var rate = Math.Clamp(brush.Strength / 100f * delta * 10f * falloff, 0f, 1f);

                    switch (mode)
                    {
                        case LandBrushMode.Raise:
                            data.Heights[i] += brush.Strength * delta * falloff;
                            break;
                        case LandBrushMode.Lower:
                            data.Heights[i] -= brush.Strength * delta * falloff;
                            break;
                        case LandBrushMode.Flatten:
                            data.Heights[i] += (_flattenHeight - data.Heights[i]) * rate;
                            break;
                        case LandBrushMode.Smooth:
                        {
                            float sum = 0;
                            var count = 0;
                            for (var ny = Math.Max(vy - 1, 0); ny <= Math.Min(vy + 1, TerrainCell.Size - 1); ny++)
                                for (var nx = Math.Max(vx - 1, 0); nx <= Math.Min(vx + 1, TerrainCell.Size - 1); nx++)
                                {
                                    sum += source[ny * TerrainCell.Size + nx];
                                    count++;
                                }
                            data.Heights[i] += (sum / count - source[i]) * rate;
                            break;
                        }
                        case LandBrushMode.Paint:
                            if (!data.Paint(vx, vy, brush.Texture!.Value, rate)) _strokeFull = true;
                            break;
                    }
                    changed = true;
                }
            }

            if (!changed) continue;
            if (mode != LandBrushMode.Paint) data.HeightsEdited = true;
            touched.Add(t);
        }

        if (touched.Count == 0) return;
        Stitch(scene, touched);

        // Every cell of the stroke: stitching may have moved a neighbour's edge too.
        for (var t = 0; t < scene.Terrain.Count; t++)
        {
            var terrain = scene.Terrain[t];
            if (!_stroke.TryGetValue((terrain.X, terrain.Y), out var entry)) continue;
            var draft = entry.Draft;
            if (mode != LandBrushMode.Paint) draft.Data.RecomputeNormals();
            scene.Terrain[t] = builder.TerrainFrom(draft.Data, terrain.X, terrain.Y, draft.Key, draft.Cell);
        }
        _renderer!.Forget(scene);
    }

    /// <summary>The draft land this stroke edits for a terrain cell (made on first touch).</summary>
    private LandscapeData StrokeData(SceneBuilder builder, TerrainCell terrain, FormKey world)
    {
        if (_stroke.TryGetValue((terrain.X, terrain.Y), out var entry)) return entry.Draft.Data;

        LandDraft draft;
        LandscapeData? before;
        if (_document!.LandDrafts(world).FirstOrDefault(l => l.GridX == terrain.X && l.GridY == terrain.Y) is { } existing)
        {
            draft = existing;
            before = existing.Data.Clone();
        }
        else
        {
            var original = builder.LandRecord(terrain.Cell);
            draft = new LandDraft(terrain.Land, terrain.Cell, world, terrain.X, terrain.Y, terrain.Data!.Clone(), original);
            before = null;
        }

        _stroke[(terrain.X, terrain.Y)] = (draft, before);
        return draft.Data;
    }

    /// <summary>Neighbouring cells share their edge row/column: make both sides agree.</summary>
    private void Stitch(Scene scene, List<int> touched)
    {
        var byGrid = new Dictionary<(int, int), LandscapeData>();
        foreach (var terrain in scene.Terrain)
        {
            if (_stroke.TryGetValue((terrain.X, terrain.Y), out var entry)) byGrid[(terrain.X, terrain.Y)] = entry.Draft.Data;
            else if (terrain.Data is not null) byGrid[(terrain.X, terrain.Y)] = terrain.Data;
        }

        const int n = TerrainCell.Size;
        foreach (var t in touched)
        {
            var cell = scene.Terrain[t];
            var data = byGrid[(cell.X, cell.Y)];

            void Pull(int dx, int dy)
            {
                if (!byGrid.TryGetValue((cell.X + dx, cell.Y + dy), out var other) || !_stroke.ContainsKey((cell.X + dx, cell.Y + dy))) return;
                for (var k = 0; k < n; k++)
                {
                    int mine, theirs;
                    if (dx == 1) { mine = k * n + (n - 1); theirs = k * n; }
                    else if (dx == -1) { mine = k * n; theirs = k * n + (n - 1); }
                    else if (dy == 1) { mine = (n - 1) * n + k; theirs = k; }
                    else { mine = k; theirs = (n - 1) * n + k; }
                    var h = (data.Heights[mine] + other.Heights[theirs]) * 0.5f;
                    data.Heights[mine] = h;
                    other.Heights[theirs] = h;
                }
            }

            Pull(1, 0);
            Pull(-1, 0);
            Pull(0, 1);
            Pull(0, -1);
        }
    }

    private void EndStroke()
    {
        var name = Brush.Mode switch
        {
            LandBrushMode.Paint => T("Paint landscape"),
            LandBrushMode.Flatten => T("Flatten landscape"),
            LandBrushMode.Smooth => T("Smooth landscape"),
            _ => T("Sculpt landscape"),
        };
        var cells = _stroke.Values.Select(e => (e.Draft, e.Before, e.Draft.Data.Clone())).ToList();
        _stroke.Clear();
        _document!.Record(new LandStrokeCommand(name, cells));
        if (_strokeFull) Log.Warn("landscape: a quadrant already has 6 texture layers — erase one by painting its base texture, or paint a texture it already uses");
    }

    /// <summary>CK Heightmap Editor export: the loaded cells' heights as a .r16 with its .json; returns a summary.</summary>
    public string ExportHeightmap(EditorApp app, string path)
    {
        if (_scene is not { IsInterior: false, Worldspace: { } world } scene || app.Session is not { } session) return T("Open an exterior first.");
        var cells = scene.Terrain.Where(t => t.Data is not null).Select(t => (t.X, t.Y, t.Data!.Heights)).ToList();
        if (cells.Count == 0) return T("No land loaded.");
        var (info, samples) = Heightmap.Build(session.Catalog.Get(world)?.EditorId ?? world.ToString(), cells);
        Heightmap.Write(path, info, samples);
        var summary = TF($"{info.Width} x {info.Height} samples, cells {info.MinCellX}, {info.MinCellY} … {info.MinCellX + info.CellsX - 1}, {info.MinCellY + info.CellsY - 1}, heights {info.MinHeight:0} … {info.MaxHeight:0} → {path}");
        Log.Info($"heightmap: {summary}");
        return summary;
    }

    /// <summary>
    /// CK Heightmap Editor import: the loaded cells inside the map (written by <see cref="ExportHeightmap"/>, edited
    /// elsewhere) take its heights — one undo step, like a brush stroke.
    /// </summary>
    public string ImportHeightmap(EditorApp app, string path)
    {
        if (_scene is not { IsInterior: false, Worldspace: { } world } scene || app.Session is not { } session) return T("Open an exterior first.");
        HeightmapInfo info;
        ushort[] samples;
        try { (info, samples) = Heightmap.Read(path); }
        catch (Exception ex) { return TF($"Cannot read {path}: {ex.Message}"); }
        var name = session.Catalog.Get(world)?.EditorId ?? world.ToString();
        if (!string.Equals(info.World, name, StringComparison.OrdinalIgnoreCase)) return TF($"The heightmap is of {info.World}, the open worldspace is {name}.");
        var builder = Builder(session);
        _stroke.Clear();
        for (var t = 0; t < scene.Terrain.Count; t++)
        {
            var terrain = scene.Terrain[t];
            if (terrain.Data is null || Heightmap.CellHeights(info, samples, terrain.X, terrain.Y) is not { } heights) continue;
            var data = StrokeData(builder, terrain, world);
            Array.Copy(heights, data.Heights, heights.Length);
            data.HeightsEdited = true;
            data.RecomputeNormals();
            var draft = _stroke[(terrain.X, terrain.Y)].Draft;
            scene.Terrain[t] = builder.TerrainFrom(data, terrain.X, terrain.Y, draft.Key, draft.Cell);
        }
        if (_stroke.Count == 0) return T("None of the loaded cells is inside the heightmap.");
        _renderer!.Forget(scene);
        var cells = _stroke.Values.Select(e => (e.Draft, e.Before, e.Draft.Data.Clone())).ToList();
        _stroke.Clear();
        _document!.Record(new LandStrokeCommand(TF($"Import heightmap {Path.GetFileName(path)}"), cells));
        var summary = TF($"{cells.Count} cell(s) took their heights from {Path.GetFileName(path)}");
        Log.Info($"heightmap: {summary}");
        return summary;
    }

    /// <summary>Autotests: one stroke of the current brush at a point, as if held for <paramref name="seconds"/>.</summary>
    public bool StrokeForTest(EditorApp app, Vector3 point, float seconds)
    {
        if (_scene is not { IsInterior: false, Worldspace: { } world } scene || app.Session is not { } session) return false;
        _stroke.Clear();
        _flattenHeight = point.Z;
        for (var t = 0f; t < seconds; t += 1f / 30f) ApplyBrush(session, scene, world, point, 1f / 30f, invert: false);
        if (_stroke.Count == 0) return false;
        EndStroke();
        return true;
    }

    public void RefreshTerrainNow(EditorApp app)
    {
        _terrainDirty = false;
        RefreshTerrain(app);
    }

    /// <summary>Autotests: absolute camera "x,y,z,yawDeg,pitchDeg" right now.</summary>
    public void PendingCameraPoseNow(string pose)
    {
        var v = pose.Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        _camera.Position = new Vector3(v[0], v[1], v[2]);
        _camera.Yaw = v[3] * MathF.PI / 180f;
        _camera.Pitch = v[4] * MathF.PI / 180f;
    }

    public float? HeightForTest(float x, float y) => _scene is null ? null : TerrainHeight(_scene, x, y);

    /// <summary>After undo/redo or a new land: rebuild the terrain of the loaded cells from drafts and plugins.</summary>
    private void RefreshTerrain(EditorApp app)
    {
        if (_scene is not { IsInterior: false, Worldspace: { } world } scene || app.Session is not { } session || _stroke.Count > 0) return;
        var builder = Builder(session);
        var (cx, cy) = scene.CenterCell;
        scene.Terrain.Clear();
        for (var y = cy - _gridRadius; y <= cy + _gridRadius; y++)
            for (var x = cx - _gridRadius; x <= cx + _gridRadius; x++)
                if (builder.TerrainAt(world, x, y) is { } terrain) scene.Terrain.Add(terrain);
        _renderer?.Forget(scene);
    }

    /// <summary>Landscape panel: flat land for every loaded grid cell that has none (cells are made too).</summary>
    public int CreateFlatLand(EditorApp app, float height)
    {
        if (_scene is not { IsInterior: false, Worldspace: { } world } scene || app.Session is not { } session || _document is null) return 0;
        if (app.ActivePlugin is not { } active)
        {
            app.ShowActiveFileDialog(T("New land goes into the active file — choose it first."));
            return 0;
        }

        var builder = Builder(session);
        var drafts = new List<Draft>();
        var (cx, cy) = scene.CenterCell;
        for (var y = cy - _gridRadius; y <= cy + _gridRadius; y++)
        {
            for (var x = cx - _gridRadius; x <= cx + _gridRadius; x++)
            {
                if (scene.Terrain.Any(t => t.X == x && t.Y == y)) continue;
                var cell = builder.CellAt(world, x, y);
                if (cell is null)
                {
                    var cellDraft = NewRecords.ExteriorCell(active.Allocate(), session.Game.Release, world, x, y);
                    drafts.Add(cellDraft);
                    cell = cellDraft.Key;
                }
                drafts.Add(new LandDraft(active.Allocate(), cell.Value, world, x, y, LandscapeData.Flat(height), null));
            }
        }

        if (drafts.Count == 0) return 0;
        _document.Execute(T("Create flat land"), [], drafts);
        return drafts.OfType<LandDraft>().Count();
    }

    /// <summary>Land height at a world position (bilinear within the cell's grid), null off the land.</summary>
    private static float? TerrainHeight(Scene scene, float x, float y)
    {
        foreach (var terrain in scene.Terrain)
        {
            var lx = (x - terrain.Origin.X) / TerrainCell.Spacing;
            var ly = (y - terrain.Origin.Y) / TerrainCell.Spacing;
            if (lx < 0 || ly < 0 || lx > TerrainCell.Size - 1 || ly > TerrainCell.Size - 1) continue;
            var x0 = Math.Min((int)lx, TerrainCell.Size - 2);
            var y0 = Math.Min((int)ly, TerrainCell.Size - 2);
            var fx = lx - x0;
            var fy = ly - y0;
            var h00 = terrain.HeightAt(x0, y0);
            var h10 = terrain.HeightAt(x0 + 1, y0);
            var h01 = terrain.HeightAt(x0, y0 + 1);
            var h11 = terrain.HeightAt(x0 + 1, y0 + 1);
            return (h00 * (1 - fx) + h10 * fx) * (1 - fy) + (h01 * (1 - fx) + h11 * fx) * fy;
        }
        return null;
    }

    /// <summary>Brush outline following the land.</summary>
    private void BrushOverlay()
    {
        var lines = _renderer!.OverlayLines;
        lines.Clear();
        if (!LandscapeMode || _brushPoint is not { } p || _scene is null) return;

        const int segments = 64;
        var radius = Brush.Radius;
        var color = Brush.Mode == LandBrushMode.Paint ? 0xFF40C0FFu : 0xFF30E0A0u;
        Vector3 At(int i)
        {
            var a = i * MathF.Tau / segments;
            var x = p.X + MathF.Cos(a) * radius;
            var y = p.Y + MathF.Sin(a) * radius;
            return new Vector3(x, y, (TerrainHeight(_scene, x, y) ?? p.Z) + 6f);
        }

        var previous = At(0);
        for (var i = 1; i <= segments; i++)
        {
            var next = At(i);
            lines.Add(new LineVertex(previous, color));
            lines.Add(new LineVertex(next, color));
            previous = next;
        }
        lines.Add(new LineVertex(p, color));
        lines.Add(new LineVertex(p + new Vector3(0, 0, Math.Max(radius * 0.15f, 24f)), color));
    }
}
