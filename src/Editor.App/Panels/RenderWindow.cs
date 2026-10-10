using System.Diagnostics;
using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Data.Catalog;
using StbEditor.Core;
using StbEditor.Render;

namespace StbEditor.App.Panels;

/// <summary>
/// The 3D view. Camera: RMB + mouse to look, WASD/QE to fly while RMB is held (Shift faster,
/// wheel changes speed), MMB drag to pan, wheel to dolly, Alt + LMB to orbit, F to frame the
/// selection, LMB to select.
/// </summary>
internal sealed partial class RenderWindow : Panel
{
    private SceneRenderer? _renderer;
    private ViewportTarget? _target;
    private readonly Camera _camera = new();
    private Scene? _scene;
    private Task<Scene>? _loading;
    private string? _loadingTitle;
    private readonly HashSet<SceneObject> _selection = [];
    private FormKey? _cell;
    private FormKey? _world;
    private int _gridRadius = 2;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrame;
    private float _fps;
    private int _lastSyncedSelection = -1;

    public override string Name => "Render Window";

    protected override ImGuiWindowFlags WindowFlags => ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    /// <summary>Keep frames coming while assets stream in or the camera moves.</summary>
    public bool NeedsRedraw => _loading is not null
        || (_renderer is { } r && (r.Meshes.Pending > 0 || r.Textures.Pending > 0))
        || ImGui.IsMouseDown(ImGuiMouseButton.Right) || ImGui.IsMouseDown(ImGuiMouseButton.Middle)
        || _drag != DragKind.None || _gizmoWasUsing
        || ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.Space)
        || (_renderer is { } a && a.Options.Animate && a.HasAnimation && IsOpen)
        || LandscapeMode
        || (NavmeshMode && ImGui.IsMouseDown(ImGuiMouseButton.Left));

    /// <summary>Scene loaded and every mesh/texture it asked for has arrived (for --screenshot).</summary>
    public bool IsSettled => _scene is not null && _loading is null && _renderer is { } r && r.Meshes.Pending == 0 && r.Textures.Pending == 0
        && _framesRendered > 5 && _scene.Objects.All(o => o.ModelPath is null || o.Model is not null || o.ModelFailed);

    private int _framesRendered;
    private bool _settledLogged;
    private readonly HashSet<string> _renderErrors = [];

    public Scene? Scene => _scene;

    public MeshCache? Meshes => _renderer?.Meshes;
    public TextureCache? Textures => _renderer?.Textures;

    public string? PendingCameraPose { get; set; }

    public void HideMarkers()
    {
        if (_renderer is not null) _renderer.Options.ShowMarkers = false;
    }

    /// <summary>--render opaque=0,blended=0,markers=0,shading=3 for isolating render problems.</summary>
    public void ApplyRenderDebug(string spec)
    {
        if (_renderer is null) return;
        var o = _renderer.Options;
        foreach (var part in spec.Split(','))
        {
            var kv = part.Split('=');
            if (kv.Length != 2) continue;
            var on = kv[1] != "0";
            switch (kv[0])
            {
                case "opaque": o.DrawOpaquePass = on; break;
                case "blended": o.DrawBlendedPass = on; break;
                case "markers": o.DrawMarkerPass = on; break;
                case "shading": o.Shading = (ShadingMode)int.Parse(kv[1]); break;
                case "particles": o.ShowParticles = on; break;
                case "navmesh": o.ShowNavmesh = on; break;
                case "psys": o.ParticleFilter = kv[1]; break;
                case "animate": o.Animate = on; break;
                case "logblended": _renderer.DebugLog = Log.Info; break;
            }
        }
    }

    /// <summary>--pick fx,fy: select what is under that fraction of the viewport once settled.</summary>
    public string? PendingPick { get; set; }
    public IReadOnlySet<SceneObject> Selection => _selection;

    public override void OnSessionChanged(EditorApp app)
    {
        _renderer?.Dispose();
        _renderer?.Meshes.Dispose();
        _renderer?.Textures.Dispose();
        _scene = null;
        _selection.Clear();

        var device = app.Graphics.Device;
        var assets = app.Session!.Assets;
        _renderer = new SceneRenderer(device, app.Graphics.Context, new MeshCache(device, assets), new TextureCache(device, assets));
        _target ??= new ViewportTarget(device);
    }

    private SceneBuilder Builder(EditorSession session) => new(session.Workspace, session.Catalog) { Document = _document };

    /// <summary>Loads an interior, or the exterior grid around an exterior cell.</summary>
    public void OpenCell(EditorApp app, FormKey cellKey)
    {
        var session = app.Session;
        if (session is null) return;

        // Cells created in this session.
        if (_document?.Draft(cellKey) is CellDraft draft)
        {
            if (draft.World is { } draftWorld && draft.Cell.Grid is { } g)
            {
                OpenExterior(app, draftWorld, g.Point.X, g.Point.Y);
                return;
            }
            var draftBuilder = Builder(session);
            _loadingTitle = draft.EditorId;
            _streaming = false;
            _cell = cellKey;
            _world = null;
            _loading = Task.Run(() => draftBuilder.BuildInterior(cellKey));
            Log.Info($"loading new cell {_loadingTitle}");
            IsOpen = true;
            return;
        }

        var record = session.Catalog.Get(cellKey);
        var placement = record?.Winner.Cell;
        if (placement is null) return;

        var builder = Builder(session);
        _loadingTitle = record!.EditorId ?? session.Workspace.FormIdText(cellKey);
        _streaming = false;
        _cell = cellKey;

        if (placement.IsInterior)
        {
            _world = null;
            _loading = Task.Run(() => builder.BuildInterior(cellKey));
        }
        else
        {
            var world = placement.Worldspace!.Value;
            _world = world;
            var (x, y, radius) = (placement.GridX, placement.GridY, _gridRadius);
            _loading = Task.Run(() => builder.BuildExterior(world, x, y, radius));
        }

        Log.Info($"loading cell {_loadingTitle}");
        IsOpen = true;
    }

    /// <summary>Loads the exterior grid of a worldspace around a cell position (also new, empty worldspaces).</summary>
    public void OpenExterior(EditorApp app, FormKey world, int x, int y)
    {
        if (app.Session is not { } session) return;
        var builder = Builder(session);
        _loadingTitle = $"{(_document?.Draft(world)?.EditorId ?? session.Catalog.Get(world)?.EditorId ?? world.ToString())} [{x}, {y}]";
        _streaming = false;
        _world = world;
        _cell = builder.CellAt(world, x, y);
        var radius = _gridRadius;
        _loading = Task.Run(() => builder.BuildExterior(world, x, y, radius));
        Log.Info($"loading {_loadingTitle}");
        IsOpen = true;
    }

    /// <summary>
    /// Exteriors follow the camera: once it leaves the centre cell the grid is rebuilt around the
    /// cell it is in. Meshes and textures stay cached, so only new cells cost anything.
    /// </summary>
    private void StreamExterior(EditorApp app)
    {
        if (_loading is not null || _scene is not { IsInterior: false, Worldspace: { } world } || app.Session is not { } session) return;

        var cx = (int)MathF.Floor(_camera.Position.X / TerrainCell.CellSize);
        var cy = (int)MathF.Floor(_camera.Position.Y / TerrainCell.CellSize);
        if (cx == _scene.CenterCell.X && cy == _scene.CenterCell.Y) return;

        var builder = Builder(session);
        var radius = _gridRadius;
        _loadingTitle = $"cells around {cx}, {cy}";
        _streaming = true;
        _loading = Task.Run(() => builder.BuildExterior(world, cx, cy, radius));
    }

    private bool _streaming;
    private readonly HashSet<string> _reportedFailures = new(StringComparer.OrdinalIgnoreCase);
    private int _failureVersion = -1;

    /// <summary>Missing or unreadable meshes/textures go to the log once each.</summary>
    private void ReportAssetFailures()
    {
        var version = _renderer!.Meshes.Version + _renderer.Textures.Version;
        if (version == _failureVersion) return;
        _failureVersion = version;

        foreach (var (path, error) in _renderer.Meshes.Failures.Concat(_renderer.Textures.Failures))
            if (_reportedFailures.Add(path)) Log.Warn($"asset {path}: {error}");
    }

    protected override void DrawContent(EditorApp app)
    {
        if (!RequireSession(app) || _renderer is null || _target is null) return;

        PollLoading(app);
        if (_terrainDirty && _loading is null)
        {
            _terrainDirty = false;
            RefreshTerrain(app);
            RefreshNavmeshes(app);
        }
        StreamExterior(app);
        ReportAssetFailures();
        SyncSelectionFromApp(app);
        DrawToolbar(app);

        var size = ImGui.GetContentRegionAvail();
        if (size.X < 8 || size.Y < 8) return;

        _target.EnsureSize((int)size.X, (int)size.Y);

        var now = _clock.Elapsed.TotalSeconds;
        var delta = (float)Math.Min(now - _lastFrame, 0.1);
        _lastFrame = now;
        if (delta > 0) _fps = _fps * 0.9f + 0.1f / delta;

        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##viewport", size,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();

        HandleInput(app, origin, size, hovered, active, delta);

        if (IsSettled && !_settledLogged)
        {
            _settledLogged = true;
            _renderer.ResetDebugLog();
        }

        if (PendingPick is { } pick && _scene is not null && _framesRendered > 3 && _renderer.Meshes.Pending == 0)
        {
            var f = pick.Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var (o, d) = _camera.Ray(new Vector2(f[0] * size.X, f[1] * size.Y), size);
            if (Picking.Pick(_scene, o, d, _renderer.Options) is { } hit)
            {
                _selection.Clear();
                _selection.Add(hit.Object);
                Log.Info($"pick: {hit.Object.BaseEditorId} [{hit.Object.BaseSignature}] {hit.Object.ModelPath} at {hit.Point}");
            }
            PendingPick = null;
        }

        BrushOverlay();
        var background = _scene?.IsInterior == true ? new Vector4(0.05f, 0.05f, 0.06f, 1) : new Vector4(0.50f, 0.60f, 0.72f, 1);
        try
        {
            _renderer.Render(_scene, _camera, _target, _selection, background);
            if (_scene is not null) _framesRendered++;
        }
        catch (Exception ex)
        {
            // A bad asset must not take the editor down; report each distinct failure once.
            if (_renderErrors.Add(ex.Message)) Log.Error($"render: {ex}");
        }

        var drawList = ImGui.GetWindowDrawList();
        unsafe
        {
            drawList.AddImage(new ImTextureRef(null, new ImTextureID((ulong)_target.ShaderView!.NativePointer)), origin, origin + size);
        }

        DrawGizmo(origin, size);
        DrawOverlay(drawList, origin, size);
        AcceptDrop(app, origin, size);
    }

    private void PollLoading(EditorApp app)
    {
        if (_loading is null || !_loading.IsCompleted) return;

        if (_loading.IsCompletedSuccessfully && _streaming)
        {
            // Same place, more cells: keep the camera and whatever was selected.
            _streaming = false;
            var selected = _selection.Select(o => o.Reference).ToHashSet();
            _scene = _loading.Result;
            if (_document is not null) _scene.ApplyDocument(_document);
            RestoreNewReferences(app);
            _renderer!.Forget(_scene);
            _selection.Clear();
            foreach (var o in _scene.Objects.Where(o => selected.Contains(o.Reference) && !o.IsDeleted)) _selection.Add(o);
            _loading = null;
            return;
        }

        _streaming = false;
        if (_loading.IsCompletedSuccessfully)
        {
            _scene = _loading.Result;
            if (_document is not null) _scene.ApplyDocument(_document);
            RestoreNewReferences(app);
            _framesRendered = 0;
            _renderer!.Forget(_scene);
            _selection.Clear();
            foreach (var warning in _scene.Warnings.Take(20)) Log.Warn($"scene: {warning}");
            Log.Info($"cell {_scene.Title}: {_scene.Objects.Count} objects, {_scene.Terrain.Count} terrain cells, {_scene.Water.Count} water planes");

            if (_scene.IsInterior)
            {
                // Inside the room at eye height, looking slightly down — framing from outside
                // only shows the roof.
                _camera.Yaw = 0.6f;
                _camera.Pitch = -0.25f;
                _camera.Position = _scene.Focus + new Vector3(0, 0, 120);
            }
            else
            {
                // Overview from the south-west, high enough to see the whole centre cell.
                _camera.Position = _scene.Focus + new Vector3(-1800, -3600, 2600);
                _camera.LookAt(_scene.Focus);
            }
        }
        else
        {
            Log.Error($"cell load failed: {_loading.Exception?.GetBaseException()}");
        }

        if (_loading.IsCompletedSuccessfully && _pendingRestore is { } restore)
        {
            _camera.Position = restore.Position;
            _camera.Yaw = restore.Yaw;
            _camera.Pitch = restore.Pitch;
            foreach (var o in _scene!.Objects.Where(o => restore.Selected.Contains(o.Reference))) _selection.Add(o);
            _pendingRestore = null;
        }

        if (_loading.IsCompletedSuccessfully && PendingCameraPose is { } pose)
        {
            // --camera x,y,z,yawDeg,pitchDeg (z relative to the scene focus) for automated checks.
            var v = pose.Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            _camera.Position = new Vector3(_scene!.Focus.X + v[0], _scene.Focus.Y + v[1], _scene.Focus.Z + v[2]);
            _camera.Yaw = v[3] * MathF.PI / 180f;
            _camera.Pitch = v[4] * MathF.PI / 180f;
            PendingCameraPose = null;
        }

        _loading = null;
    }

    private void DrawToolbar(EditorApp app)
    {
        var options = _renderer!.Options;

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7);
        var shading = (int)options.Shading;
        if (ImGui.Combo("##shading", ref shading, "Lit\0Unlit\0Normals\0Untextured\0")) options.Shading = (ShadingMode)shading;
        ImGui.SameLine();

        Toggle("Wire", () => options.Wireframe, v => options.Wireframe = v, "Wireframe (CK: W)");
        Toggle("Markers", () => options.ShowMarkers, v => options.ShowMarkers = v, "Editor markers, model-less lights, actors (CK: M)");
        Toggle("Disabled", () => options.ShowInitiallyDisabled, v => options.ShowInitiallyDisabled = v, "Initially disabled references (tinted purple)");
        Toggle("Water", () => options.ShowWater, v => options.ShowWater = v, null);
        Toggle("Land", () => options.ShowTerrain, v => options.ShowTerrain = v, null);
        Toggle("Fog", () => options.Fog, v => options.Fog = v, null);
        Toggle("Borders", () => options.CellBorders, v => options.CellBorders = v, "Cell borders (CK: B)");
        Toggle("Navmesh", () => options.ShowNavmesh, v => options.ShowNavmesh = v, "Show navmeshes (N edits them)");
        Toggle("FX", () => options.ShowParticles, v => options.ShowParticles = v, "Particles: fire, smoke, dust, sparks");
        Toggle("Anim", () => options.Animate, v => options.Animate = v, "Play flames, scrolling water and particles (off: frozen frame, idle CPU)");

        if (_world is not null)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
            if (ImGui.SliderInt("##radius", ref _gridRadius, 0, 5, "grid ±%d"))
            {
                // Applied on the next load; reload now for convenience.
                if (_cell is { } cell) OpenCell(app, cell);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Cells loaded around the centre cell");
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6.5f);
        var brightness = options.Brightness;
        if (ImGui.SliderFloat("##bright", ref brightness, 0.3f, 4f, "light %.1f")) options.Brightness = brightness;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Ambient and directional light multiplier");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6);
        var speed = _camera.Speed;
        if (ImGui.DragFloat("##speed", ref speed, 10f, 50f, 20000f, "speed %.0f")) _camera.Speed = speed;

        DrawEditToolbar();
    }

    private static void Toggle(string label, Func<bool> get, Action<bool> set, string? tooltip)
    {
        var value = get();
        if (value) ImGui.PushStyleColor(ImGuiCol.Button, Theme.ButtonActive);
        if (ImGui.Button(label)) set(!value);
        if (value) ImGui.PopStyleColor();
        if (tooltip is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        ImGui.SameLine();
    }

    /// <summary>A reference picked in another panel (or by link) selects it here when it is loaded.</summary>
    private void SyncSelectionFromApp(EditorApp app)
    {
        if (_lastSyncedSelection == app.SelectionVersion || _scene is null) return;
        _lastSyncedSelection = app.SelectionVersion;
        if (app.Selected is not { } key) return;

        var matches = _scene.Objects.Where(o => o.Reference == key).ToList();
        if (matches.Count == 0) return;
        _selection.Clear();
        foreach (var m in matches) _selection.Add(m);
        FrameSelection();
    }

    private Vector3 SelectionCenter()
    {
        var sum = Vector3.Zero;
        foreach (var o in _selection) sum += o.BoundsCenter;
        return sum / Math.Max(1, _selection.Count);
    }

    private void FrameSelection()
    {
        if (_selection.Count == 0) return;
        var center = SelectionCenter();
        var radius = _selection.Max(o => Vector3.Distance(o.BoundsCenter, center) + o.BoundsRadius);
        _camera.Frame(center, radius);
    }

    private void DrawOverlay(ImDrawListPtr drawList, Vector2 origin, Vector2 size)
    {
        var stats = _renderer!.Stats;
        var lines = new List<string>();

        if (_loading is not null) lines.Add($"Loading {_loadingTitle}…");
        else if (_scene is null) lines.Add("Double-click a cell in Cell View to load it");
        else
        {
            lines.Add($"{_scene.Title}");
            lines.Add($"{stats.Visible:N0}/{stats.Objects:N0} objects · {stats.DrawCalls:N0} draws · {stats.Triangles / 1000:N0}k tris · {stats.Lights} lights · {_fps:0} fps");
            var pendingMeshes = _renderer.Meshes.Pending;
            var pendingTextures = _renderer.Textures.Pending;
            if (pendingMeshes + pendingTextures > 0) lines.Add($"streaming: {pendingMeshes} meshes, {pendingTextures} textures");
            var p = _camera.Position;
            lines.Add($"camera {p.X:0} {p.Y:0} {p.Z:0}" + (_scene.IsInterior ? "" : $"  cell {(int)MathF.Floor(p.X / 4096)}, {(int)MathF.Floor(p.Y / 4096)}"));
        }

        if (_selection.Count == 1)
        {
            var o = _selection.First();
            lines.Add($"selected: {o.BaseEditorId ?? "?"} [{o.BaseSignature}] {o.ModelPath ?? ""}");
        }
        else if (_selection.Count > 1)
        {
            lines.Add($"selected: {_selection.Count} objects");
        }

        if (_document is { PendingCount: > 0 } doc)
            lines.Add($"{doc.PendingCount} unsaved change(s) — Ctrl+S saves into the active file");

        var help = HelpLine;
        var helpSize = ImGui.CalcTextSize(help);
        if (helpSize.X < size.X - 16)
        {
            var hp = new Vector2(origin.X + 8, origin.Y + size.Y - helpSize.Y - 6);
            drawList.AddText(hp + new Vector2(1, 1), 0xC0000000, help);
            drawList.AddText(hp, 0xB0D8D2C4, help);
        }

        var y = origin.Y + 6;
        foreach (var line in lines)
        {
            drawList.AddText(new Vector2(origin.X + 9, y + 1), 0xC0000000, line);
            drawList.AddText(new Vector2(origin.X + 8, y), 0xFFE8E8E8, line);
            y += ImGui.GetTextLineHeight();
        }
    }

    /// <summary>Forms dragged from the Object Window (list or thumbnails) are placed where they are dropped.</summary>
    private unsafe void AcceptDrop(EditorApp app, Vector2 origin, Vector2 size)
    {
        if (!ImGui.BeginDragDropTarget()) return;
        var payload = ImGui.AcceptDragDropPayload(FormDrag.PayloadType);
        if (payload.Handle != null && FormDrag.Current is { } form)
        {
            FormDrag.Current = null;
            PlaceAt(app, form, ImGui.GetIO().MousePos - origin, size);
        }
        ImGui.EndDragDropTarget();
    }

    public void Shutdown()
    {
        _renderer?.Dispose();
        _renderer?.Meshes.Dispose();
        _renderer?.Textures.Dispose();
        _target?.Dispose();
    }

    /// <summary>Resolves --cell: FormID, FormKey or interior EditorID.</summary>
    public static FormKey? FindCell(EditorApp app, string text)
    {
        var session = app.Session!;
        if (session.Workspace.ParseFormReference(text) is { } key && session.Catalog.Get(key)?.Signature == "CELL") return key;

        return (session.Catalog.BySignature.GetValueOrDefault("CELL") ?? [])
            .FirstOrDefault(c => string.Equals(c.EditorId, text, StringComparison.OrdinalIgnoreCase))?.FormKey;
    }
}
