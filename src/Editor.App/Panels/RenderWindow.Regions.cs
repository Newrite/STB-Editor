using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using StbEditor.App.Services;
using StbEditor.Core;
using StbEditor.Data;
using StbEditor.Render;
using Scene = StbEditor.Render.Scene;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// Regions (REGN) outdoors (CK: Region Editor): the borders of the open worldspace's regions drawn on the land in their map
/// colours, and the Region tool — click inside a region to select it, drag a corner of the selected one, Shift+click on its
/// border to add a corner, Del removes the selected corner (an area keeps three). Each change is one undo step on the region.
/// </summary>
internal sealed partial class RenderWindow
{
    private bool _regionsShown;
    private FormKey? _region;
    private (int Area, int Point)? _regionPoint;
    private Region? _regionDrag;
    private List<(FormKey Key, IRegionGetter Record)>? _regions;
    private (int Version, FormKey? World, object? Session) _regionsFor;

    public bool RegionsShown { get => _regionsShown; set => _regionsShown = value; }
    public bool RegionMode => _tool == EditTool.Region;
    public FormKey? SelectedRegion { get => _region; set { _region = value; _regionPoint = null; } }
    public (int Area, int Point)? SelectedRegionPoint => _regionPoint;

    public void SetRegionMode(bool on)
    {
        _tool = on ? EditTool.Region : EditTool.Select;
        if (on) _regionsShown = true;
    }

    /// <summary>The open worldspace's regions as they are now (the editor's edits over the winning versions).</summary>
    public IReadOnlyList<(FormKey Key, IRegionGetter Record)> Regions(EditorApp app)
    {
        if (app.Session is not { } session || _world is not { } world) return [];
        if (_regions is not null && _regionsFor == (app.Document.Version, world, session)) return _regions;
        var lookup = new RecordLookup(session.Workspace, session.Catalog);
        var keys = (session.Catalog.BySignature.GetValueOrDefault("REGN") ?? []).Select(r => r.FormKey)
            .Concat(app.Document.Drafts.Values.OfType<OverrideDraft>().Where(d => d.Record is IRegionGetter).Select(d => d.Key)).Distinct();
        _regions = [];
        foreach (var key in keys)
        {
            var record = app.Document.Override(key)?.Record as IRegionGetter ?? lookup.Resolve<IRegionGetter>(key);
            if (record is not null && record.Worldspace.FormKeyNullable == world) _regions.Add((key, record));
        }
        _regions.Sort((a, b) => string.Compare(a.Record.EditorID, b.Record.EditorID, StringComparison.OrdinalIgnoreCase));
        _regionsFor = (app.Document.Version, world, session);
        return _regions;
    }

    private IRegionGetter? RegionRecord(EditorApp app, FormKey key) =>
        _regionDrag is { } drag && drag.FormKey == key ? drag : Regions(app).FirstOrDefault(r => r.Key == key).Record;

    private static uint LineColour(System.Drawing.Color? c, float bright = 1)
    {
        var colour = c ?? System.Drawing.Color.FromArgb(255, 220, 220, 220);
        uint R(int v) => (uint)Math.Clamp((int)(v * bright), 0, 255);
        return 0xFF000000u | R(colour.B) << 16 | R(colour.G) << 8 | R(colour.R);
    }

    /// <summary>Region borders following the land (added to the overlay lines each frame).</summary>
    private void RegionOverlay(EditorApp app)
    {
        if (!_regionsShown || _scene is not { IsInterior: false } scene || _renderer is null) return;
        var lines = _renderer.OverlayLines;
        foreach (var (key, stored) in Regions(app))
        {
            var record = RegionRecord(app, key) ?? stored;
            var selected = key == _region;
            var colour = LineColour(record.MapColor, selected ? 1.25f : 0.8f);
            for (var a = 0; a < record.RegionAreas.Count; a++)
            {
                var points = record.RegionAreas[a].RegionPointListData;
                for (var i = 0; i < points.Count; i++)
                {
                    var p = points[i];
                    var q = points[(i + 1) % points.Count];
                    Segment(lines, scene, new Vector2(p.X, p.Y), new Vector2(q.X, q.Y), colour, selected ? 30 : 20);
                    if (selected && _tool == EditTool.Region)
                    {
                        var at = new Vector3(p.X, p.Y, (TerrainHeight(scene, p.X, p.Y) ?? 0) + 30);
                        var size = _regionPoint == (a, i) ? 90f : 45f;
                        var mark = _regionPoint == (a, i) ? 0xFF40FFFFu : 0xFFFFFFFFu;
                        lines.Add(new LineVertex(at, mark));
                        lines.Add(new LineVertex(at + new Vector3(0, 0, size * 3), mark));
                        lines.Add(new LineVertex(at - new Vector3(size, 0, 0), mark));
                        lines.Add(new LineVertex(at + new Vector3(size, 0, 0), mark));
                        lines.Add(new LineVertex(at - new Vector3(0, size, 0), mark));
                        lines.Add(new LineVertex(at + new Vector3(0, size, 0), mark));
                    }
                }
            }
        }
    }

    private void Segment(List<LineVertex> lines, Scene scene, Vector2 a, Vector2 b, uint colour, float lift)
    {
        var steps = Math.Clamp((int)(Vector2.Distance(a, b) / 128f), 1, 256);
        Vector3 At(int i)
        {
            var p = Vector2.Lerp(a, b, i / (float)steps);
            return new Vector3(p, (TerrainHeight(scene, p.X, p.Y) ?? 0) + lift);
        }
        var previous = At(0);
        for (var i = 1; i <= steps; i++)
        {
            var next = At(i);
            lines.Add(new LineVertex(previous, colour));
            lines.Add(new LineVertex(next, colour));
            previous = next;
        }
    }

    /// <summary>The Region tool's mouse and keys.</summary>
    private void HandleRegions(EditorApp app, Vector2 mouse, Vector2 size, bool hovered)
    {
        if (_scene is not { IsInterior: false } scene || app.Session is null || _document is null) return;
        var io = ImGui.GetIO();
        var (origin, direction) = _camera.Ray(mouse, size);
        var viewProj = _camera.View * _camera.Projection(size.X / size.Y);
        Vector2? Screen(Vector2 p)
        {
            var clip = Vector4.Transform(new Vector4(p.X, p.Y, (TerrainHeight(scene, p.X, p.Y) ?? 0) + 30, 1), viewProj);
            return clip.W <= 0 ? null : new Vector2((clip.X / clip.W * 0.5f + 0.5f) * size.X, (0.5f - clip.Y / clip.W * 0.5f) * size.Y);
        }

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !io.KeyAlt)
        {
            var record = _region is { } k ? RegionRecord(app, k) : null;
            (int, int)? near = null;
            var best = 12f;
            if (record is not null)
                for (var a = 0; a < record.RegionAreas.Count; a++)
                    for (var i = 0; i < record.RegionAreas[a].RegionPointListData.Count; i++)
                    {
                        var p = record.RegionAreas[a].RegionPointListData[i];
                        if (Screen(new Vector2(p.X, p.Y)) is { } s && Vector2.Distance(s, mouse) < best) { best = Vector2.Distance(s, mouse); near = (a, i); }
                    }
            var ground = Picking.PickTerrain(scene, origin, direction);
            if (near is { } hit)
            {
                _regionPoint = hit;
                _regionDrag = (Region)_document.WorkingCopy(_region!.Value, record!);
            }
            else if (io.KeyShift && record is not null && ground is { } g && NearestEdge(record, new Vector2(g.X, g.Y), Screen, mouse) is { } edge)
            {
                var region = (Region)_document.WorkingCopy(_region!.Value, record);
                region.RegionAreas[edge.Area].RegionPointListData.Insert(edge.After + 1, new P2Float(g.X, g.Y));
                _document.CommitEdit(T("Add region point"), region, RecordPlace.TopLevel);
                _regionPoint = (edge.Area, edge.After + 1);
            }
            else if (ground is { } at)
            {
                // Select the region the click is inside (the smallest, when they overlap).
                var inside = Regions(app).Where(r => r.Record.RegionAreas.Any(area => Inside(area.RegionPointListData, new Vector2(at.X, at.Y))))
                    .OrderBy(r => r.Record.RegionAreas.Sum(area => Area(area.RegionPointListData))).Select(r => (FormKey?)r.Key).FirstOrDefault();
                _region = inside;
                _regionPoint = null;
                if (inside is { } picked) app.Select(picked);
            }
        }

        if (_regionDrag is { } drag && _regionPoint is { } point)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (Picking.PickTerrain(scene, origin, direction) is { } g)
                    drag.RegionAreas[point.Area].RegionPointListData[point.Point] = new P2Float(g.X, g.Y);
            }
            else
            {
                _regionDrag = null;
                var before = RegionRecord(app, drag.FormKey)?.RegionAreas[point.Area].RegionPointListData[point.Point];
                if (before is null || before.Value.X != drag.RegionAreas[point.Area].RegionPointListData[point.Point].X
                    || before.Value.Y != drag.RegionAreas[point.Area].RegionPointListData[point.Point].Y)
                    _document.CommitEdit(T("Move region point"), drag, RecordPlace.TopLevel);
            }
        }

        if ((ImGui.IsWindowFocused() || hovered) && !io.WantTextInput)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Delete, false)) DeleteRegionPoint(app);
            if (ImGui.IsKeyPressed(ImGuiKey.Escape, false)) { _regionPoint = null; _region = null; }
        }
    }

    public bool DeleteRegionPoint(EditorApp app)
    {
        if (_region is not { } key || _regionPoint is not { } point || _document is null || RegionRecord(app, key) is not { } record) return false;
        if (record.RegionAreas[point.Area].RegionPointListData.Count <= 3) { Log.Warn(T("A region area keeps at least three corners")); return false; }
        var region = (Region)_document.WorkingCopy(key, record);
        region.RegionAreas[point.Area].RegionPointListData.RemoveAt(point.Point);
        _document.CommitEdit(T("Delete region point"), region, RecordPlace.TopLevel);
        _regionPoint = null;
        return true;
    }

    /// <summary>A new square area (one cell wide) around a point: added to the selected region, or a new region of the active file.</summary>
    public FormKey? AddRegionArea(EditorApp app, Vector2 centre, bool newRegion)
    {
        if (app.Session is not { } session || _document is null || _world is not { } world) return null;
        var half = TerrainCell.CellSize / 2;
        var area = new RegionArea
        {
            EdgeFallOff = 0,
            RegionPointListData = [new P2Float(centre.X - half, centre.Y - half), new P2Float(centre.X + half, centre.Y - half),
                new P2Float(centre.X + half, centre.Y + half), new P2Float(centre.X - half, centre.Y + half)],
        };
        if (!newRegion && _region is { } key && RegionRecord(app, key) is { } record)
        {
            var region = (Region)_document.WorkingCopy(key, record);
            region.RegionAreas.Add(area);
            _document.CommitEdit(T("Add region area"), region, RecordPlace.TopLevel);
            return key;
        }
        if (app.ActivePlugin is not { } active)
        {
            app.ShowActiveFileDialog(T("A new region goes into the active file — choose it first."));
            return null;
        }
        var id = "NewRegion";
        for (var n = 2; session.Catalog.ByKey.Values.Any(r => string.Equals(r.EditorId, id, StringComparison.OrdinalIgnoreCase)); n++) id = $"NewRegion{n}";
        var created = (Region)NewRecords.Blank(typeof(Region), active.Allocate(), session.Game.Release, id);
        created.Worldspace.SetTo(world);
        created.MapColor = System.Drawing.Color.FromArgb(255, 255, 160, 40);
        created.RegionAreas.Add(area);
        _document.CommitEdit(TF($"New region {id}"), created, RecordPlace.TopLevel);
        _region = created.FormKey;
        _regionPoint = null;
        return created.FormKey;
    }

    /// <summary>The ground point in the middle of the view (where new areas go).</summary>
    public Vector2 ViewCentreOnGround()
    {
        if (_scene is { } scene && Picking.PickTerrain(scene, _camera.Position, _camera.Forward) is { } hit) return new Vector2(hit.X, hit.Y);
        var ahead = _camera.Position + _camera.Forward * 2000;
        return new Vector2(ahead.X, ahead.Y);
    }

    private static (int Area, int After)? NearestEdge(IRegionGetter record, Vector2 at, Func<Vector2, Vector2?> screen, Vector2 mouse)
    {
        (int, int)? best = null;
        var bestPixels = 14f;
        for (var a = 0; a < record.RegionAreas.Count; a++)
        {
            var points = record.RegionAreas[a].RegionPointListData;
            for (var i = 0; i < points.Count; i++)
            {
                var p = new Vector2(points[i].X, points[i].Y);
                var q = new Vector2(points[(i + 1) % points.Count].X, points[(i + 1) % points.Count].Y);
                var d = q - p;
                var t = d.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(at - p, d) / d.LengthSquared(), 0, 1) : 0;
                if (screen(p + d * t) is { } s && Vector2.Distance(s, mouse) < bestPixels) { bestPixels = Vector2.Distance(s, mouse); best = (a, i); }
            }
        }
        return best;
    }

    public static bool Inside(IReadOnlyList<P2Float> polygon, Vector2 p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    private static float Area(IReadOnlyList<P2Float> polygon)
    {
        var sum = 0f;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++) sum += (polygon[j].X + polygon[i].X) * (polygon[j].Y - polygon[i].Y);
        return MathF.Abs(sum / 2);
    }

    /// <summary>Autotests: a corner of the selected region moved by an offset, as a drag ends.</summary>
    public bool MoveRegionPointForTest(EditorApp app, int area, int point, Vector2 offset)
    {
        if (_region is not { } key || _document is null || RegionRecord(app, key) is not { } record) return false;
        var region = (Region)_document.WorkingCopy(key, record);
        var p = region.RegionAreas[area].RegionPointListData[point];
        region.RegionAreas[area].RegionPointListData[point] = new P2Float(p.X + offset.X, p.Y + offset.Y);
        _document.CommitEdit(T("Move region point"), region, RecordPlace.TopLevel);
        _regionPoint = (area, point);
        return true;
    }

    /// <summary>Autotests: the camera high above a ground point, looking down.</summary>
    public void TopViewForRegionTest(Vector2 at)
    {
        var height = 30000f;
        _camera.Pitch = -1.5533f;
        _camera.Position = new Vector3(at.X, at.Y - height * 0.0175f, (_scene is { } s ? TerrainHeight(s, at.X, at.Y) ?? 0 : 0) + height);
    }
}
