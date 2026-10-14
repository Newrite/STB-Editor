using System.Diagnostics;
using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Data.Catalog;
using StbEditor.Core;
using StbEditor.Render;
using static StbEditor.App.Ui.Loc;

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
    public bool NeedsRedraw => _loading is not null || _havok is not null || _navGen is not null
        || (_renderer is { } r && (r.Meshes.Pending > 0 || r.Textures.Pending > 0))
        || ImGui.IsMouseDown(ImGuiMouseButton.Right) || ImGui.IsMouseDown(ImGuiMouseButton.Middle)
        || _drag != DragKind.None || _gizmoWasUsing
        || ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.Space) || _zoomLeft != 0
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

    /// <summary>The cell the view was opened on (interior, or the exterior centre cell).</summary>
    public FormKey? LoadedCell => _cell;

    /// <summary>Where the camera is (world units).</summary>
    public Vector3 CameraPosition => _camera.Position;

    /// <summary>Outdoors: the exterior grid cell the camera is over; null indoors or before a scene.</summary>
    public (FormKey World, int X, int Y)? CameraCell =>
        _scene is { IsInterior: false, Worldspace: { } world }
            ? (world, (int)MathF.Floor(_camera.Position.X / TerrainCell.CellSize), (int)MathF.Floor(_camera.Position.Y / TerrainCell.CellSize))
            : null;

    public bool IsSelected(SceneObject obj) => _selection.Contains(obj);

    public IReadOnlyCollection<SceneObject> SelectedObjects => _selection;

    /// <summary>Selects just these objects (autotests).</summary>
    public void SelectForTest(params SceneObject[] objects)
    {
        _selection.Clear();
        foreach (var o in objects) _selection.Add(o);
    }

    /// <summary>RMB flying speed (units per second); kept in the settings.</summary>
    public float FlySpeed
    {
        get => _camera.Speed;
        set => _camera.Speed = Math.Clamp(value, 50f, 20000f);
    }

    public void SetHighlightActors(EditorApp app, bool on)
    {
        app.Settings.HighlightActors = on;
        if (_renderer is not null) _renderer.Options.HighlightActors = on;
    }

    public MeshCache? Meshes => _renderer?.Meshes;
    public TextureCache? Textures => _renderer?.Textures;

    /// <summary>Files rewritten on disk (FaceGen export): their meshes and textures load again; returns how many were dropped.</summary>
    public int ReloadAssets(Func<string, bool> match)
    {
        var dropped = (_renderer?.Meshes.Forget(match) ?? 0) + (_renderer?.Textures.Forget(match) ?? 0);
        if (_scene is not null)
            foreach (var obj in _scene.Objects)
                if (obj.ModelPath is { } path && match(path)) { obj.Model = null; obj.ModelFailed = false; }
        return dropped;
    }

    public string? PendingCameraPose { get; set; }

    public void HideGroups(IEnumerable<string> groups, bool currentCellOnly = false)
    {
        if (_renderer is null) return;
        foreach (var group in groups) _renderer.Options.HiddenGroups.Add(group.Trim());
        if (currentCellOnly) _renderer.Options.CurrentCellOnly = true;
    }

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
                case "collision": o.ShowCollision = kv[1] != "0"; break;
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
        _renderer.Options.HighlightActors = app.Settings.HighlightActors;
        _camera.Speed = app.Settings.FlySpeed;
        if (app.Settings.LastView is { } last) ApplyView(app, last);
        _target ??= new ViewportTarget(device);
    }

    private SceneBuilder Builder(EditorSession session) => new(session.Workspace, session.Catalog) { Document = _document };

    /// <summary>Loads an interior, or the exterior grid around an exterior cell.</summary>
    public void OpenCell(EditorApp app, FormKey cellKey)
    {
        var session = app.Session;
        if (session is null) return;
        _pendingFocus = null;

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

    /// <summary>A reference to select and frame once its cell has loaded (Use Info → go to).</summary>
    private FormKey? _pendingFocus;

    /// <summary>
    /// Opens the cell of a placed reference and selects it, with the camera on it (Use Info's
    /// "go to"). <paramref name="cell"/> is the cell it sits in (a worldspace's persistent cell for
    /// persistent exterior references, whose grid comes from the reference's own position).
    /// </summary>
    public void GoToReference(EditorApp app, FormKey reference, FormKey cell)
    {
        if (app.Session is not { } session) return;
        if (_scene?.Objects.Any(o => o.Reference == reference) == true && _loading is null)
        {
            FocusReference(app, reference);
            return;
        }

        var lookup = new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog);
        var placement = session.Catalog.Get(cell)?.Winner.Cell;
        Vector3? position = null;
        foreach (var version in Enumerable.Reverse(placement is null ? [] : lookup.CellVersions(cell)))
        {
            var found = version.Persistent.Concat(version.Temporary).FirstOrDefault(r => r.FormKey == reference);
            var p = found switch
            {
                Mutagen.Bethesda.Skyrim.IPlacedObjectGetter o => o.Placement?.Position,
                Mutagen.Bethesda.Skyrim.IPlacedNpcGetter n => n.Placement?.Position,
                Mutagen.Bethesda.Skyrim.IAPlacedTrapGetter t => t.Placement?.Position,
                _ => null,
            };
            if (p is { } pos) { position = new Vector3(pos.X, pos.Y, pos.Z); break; }
        }

        var before = _loading;
        if (placement is { IsInterior: true }) OpenCell(app, cell);
        else if (placement?.Worldspace is { } world)
        {
            var (gx, gy) = position is { } at
                ? ((int)MathF.Floor(at.X / TerrainCell.CellSize), (int)MathF.Floor(at.Y / TerrainCell.CellSize))
                : (placement.GridX, placement.GridY);
            OpenExterior(app, world, gx, gy);
        }
        else
        {
            Log.Warn($"{session.Workspace.FormIdText(reference)}: its cell {session.Workspace.FormIdText(cell)} cannot be opened");
        }
        // Set after opening: opening a cell forgets an earlier go-to that has not landed yet.
        if (_loading is not null && !ReferenceEquals(_loading, before)) _pendingFocus = reference;
    }

    private void FocusReference(EditorApp app, FormKey reference)
    {
        if (_scene?.Objects.FirstOrDefault(o => o.Reference == reference) is not { } obj)
        {
            Log.Warn($"{app.Session?.Workspace.FormIdText(reference)}: not in the loaded cells (disabled or deleted?)");
            return;
        }
        _selection.Clear();
        _selection.Add(obj);
        app.Select(reference);
        _lastSyncedSelection = app.SelectionVersion;
        FrameSelection();
    }

    /// <summary>Loads the exterior grid of a worldspace around a cell position (also new, empty worldspaces).</summary>
    public void OpenExterior(EditorApp app, FormKey world, int x, int y)
    {
        if (app.Session is not { } session) return;
        _pendingFocus = null;
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
        _loadingTitle = TF($"cells around {cx}, {cy}");
        _streaming = true;
        _loading = Task.Run(() => builder.BuildExterior(world, cx, cy, radius));
    }

    private bool _streaming;
    private Vector2 _viewCenter;

    /// <summary>Where the 3D view was drawn last frame (screen pixels): pasting under the cursor.</summary>
    private Vector2 _viewOrigin, _viewSize;
    private double _lastTrim;

    /// <summary>
    /// Every couple of seconds: meshes and textures not drawn for 20 s go once the caches pass their
    /// share of the video memory the OS gives us (on a 6 GB card ~1.3 GB textures, ~0.5 GB meshes).
    /// Without this an exterior flight kept every cell it passed — on 16 GB machines Windows started
    /// paging and the whole PC froze.
    /// </summary>
    private void TrimCaches(EditorApp app)
    {
        var now = _clock.Elapsed.TotalSeconds;
        if (now - _lastTrim < 2 || _renderer is null) return;
        _lastTrim = now;

        var (_, budget) = app.Graphics.VideoMemory;
        if (budget == 0) budget = 2048L << 20;
        // --video-budget <MB>: pretend the card has this much (checks eviction on a big card).
        var cli = Environment.GetCommandLineArgs();
        var forced = Array.IndexOf(cli, "--video-budget");
        if (forced >= 0 && forced + 1 < cli.Length && long.TryParse(cli[forced + 1], out var mb)) budget = (ulong)mb << 20;
        var textureBudget = Math.Clamp((long)(budget * 0.25), 128L << 20, 2048L << 20);
        var meshBudget = Math.Clamp((long)(budget * 0.10), 64L << 20, 1024L << 20);
        var textures = _renderer.Textures.Trim(textureBudget, 20_000);
        var meshes = _renderer.Meshes.Trim(meshBudget, 20_000);
        if (textures + meshes > 0)
            Log.Info($"cache trim: {meshes} meshes, {textures} textures released; now {_renderer.Meshes.Bytes >> 20} MB meshes, {_renderer.Textures.Bytes >> 20} MB textures");
    }

    private int _zoomTest;
    private Vector3 _zoomStart;
    private int _zoomFrames;

    /// <summary>--autotest-zoom: one wheel notch over the view; logs how far the camera went and over how many frames.</summary>
    public bool ZoomTestStep()
    {
        if (!IsSettled) return false;
        switch (_zoomTest)
        {
            case 0:
                StbEditor.App.Ui.ImGuiLayer.TestMouse = (_viewCenter, false);
                _zoomTest = 1;
                return false;
            case 1:
                _zoomStart = _camera.Position;
                StbEditor.App.Ui.ImGuiLayer.TestWheel = 1;
                _zoomTest = 2;
                _zoomFrames = 0;
                return false;
            case 2:
                _zoomFrames++;
                if ((_zoomLeft != 0 || _zoomFrames < 4) && _zoomFrames < 600) return false;
                StbEditor.App.Ui.ImGuiLayer.TestMouse = null;
                Log.Info($"autotest-zoom: one notch moved the camera {Vector3.Distance(_zoomStart, _camera.Position):0.0} units over {_zoomFrames} frame(s)");
                return true;
        }
        return false;
    }

    /// <summary>--autotest-fly: once everything around has streamed in, move the camera one cell east. False while still loading.</summary>
    public bool FlyStepForTest()
    {
        if (!IsSettled || _scene is { IsInterior: true }) return false;
        _camera.Position += new Vector3(TerrainCell.CellSize, 0, 0);
        return true;
    }
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
        PollDoor(app);
        if (_renderer is not null) _renderer.Options.CurrentCell = CentreCell(app);
        if (_terrainDirty && _loading is null)
        {
            _terrainDirty = false;
            RefreshTerrain(app);
            RefreshNavmeshes(app);
            SyncReferenceBases(app);
            // An edited interior lighting shows at once.
            if (_scene is { IsInterior: true } scene && _cell is { } cellKey && app.Session is { } lit) scene.Lighting = Builder(lit).InteriorLightingOf(cellKey);
        }
        StreamExterior(app);
        TrimCaches(app);
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
        _viewCenter = origin + size / 2;
        _viewOrigin = origin;
        _viewSize = size;
        // ImGuizmo only starts a drag when no ImGui item is hovered (this frame or the last), and the
        // viewport button covers the whole view: while the mouse is on a gizmo handle leave it out.
        var gizmoHot = _gizmoOver || _gizmoWasUsing;
        if (gizmoHot)
            ImGui.Dummy(size);
        else
            ImGui.InvisibleButton("##viewport", size,
                ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        // Over a handle the right and middle buttons and the wheel still belong to the view (ImGuizmo uses the left one;
        // left presses wait for it through gizmoBusy in HandleInput).
        var hovered = !_gizmoWasUsing && ImGui.IsItemHovered();
        var active = !gizmoHot && ImGui.IsItemActive();

        FinishHavokSim();
        FinishNavmeshGeneration(app);
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
                // Checks of the 1 key: --fade-picked presses it once (see-through), twice with "2" (hidden).
                var fade = Array.IndexOf(Environment.GetCommandLineArgs(), "--fade-picked");
                if (fade >= 0)
                {
                    var times = fade + 1 < Environment.GetCommandLineArgs().Length && Environment.GetCommandLineArgs()[fade + 1] == "2" ? 2 : 1;
                    for (var t = 0; t < times; t++) HideSelection();
                    Log.Info($"fade-picked x{times}: faded {_renderer.Options.FadedByHand.Count}, hidden {_renderer.Options.HiddenByHand.Count}");
                }
            }
            PendingPick = null;
        }

        BrushOverlay();
        RegionOverlay(app);
        CollisionOverlay();
        ApplySky(app);
        var background = _scene?.IsInterior == true ? new Vector4(0.05f, 0.05f, 0.06f, 1) : _skyColour ?? new Vector4(0.50f, 0.60f, 0.72f, 1);
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
        DrawDoorQuestion(app);
        DrawOverlay(drawList, origin, size);
        AcceptDrop(app, origin, size);
    }

    private (Scene Scene, FormKey? Cell)? _centre;

    /// <summary>Outdoors: the cell in the middle of the loaded grid (it moves with the camera).</summary>
    private FormKey? CentreCell(EditorApp app)
    {
        if (_scene is not { IsInterior: false, Worldspace: { } world } scene || app.Session is not { } session) return null;
        if (_centre is { } c && ReferenceEquals(c.Scene, scene)) return c.Cell;
        var cell = Builder(session).CellAt(world, scene.CenterCell.X, scene.CenterCell.Y);
        _centre = (scene, cell);
        return cell;
    }

    /// <summary>CK's Show/Hide: kinds of objects, and outdoors the centre cell only.</summary>
    private void DrawShowHide(RenderOptions options)
    {
        ImGui.SameLine();
        var hidden = options.HiddenGroups.Count + (options.CurrentCellOnly ? 1 : 0) + options.HiddenByHand.Count + options.FadedByHand.Count;
        if (hidden > 0) ImGui.PushStyleColor(ImGuiCol.Button, Theme.ButtonActive);
        if (ImGui.Button((hidden > 0 ? TF($"Show ({hidden} off)") : T("Show")) + "###showhide")) ImGui.OpenPopup("##showhidepopup");
        if (hidden > 0) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Kinds of objects shown and picked (CK: Show/Hide)"));
        if (!ImGui.BeginPopup("##showhidepopup")) return;
        foreach (var group in RenderOptions.Groups)
        {
            var on = !options.HiddenGroups.Contains(group);
            if (ImGui.Checkbox(T(group), ref on))
            {
                if (on) options.HiddenGroups.Remove(group);
                else options.HiddenGroups.Add(group);
            }
        }
        ImGui.Separator();
        var only = options.CurrentCellOnly;
        if (ImGui.Checkbox(T("Current cell only"), ref only)) options.CurrentCellOnly = only;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Outdoors: only the objects of the cell in the middle of the loaded grid"));
        ImGui.Separator();
        ImGui.BeginDisabled(_selection.Count == 0);
        if (ImGui.Selectable(TF($"See-through, then hide the selection ({_selection.Count})") + "  1")) HideSelection();
        ImGui.EndDisabled();
        ImGui.BeginDisabled(options.HiddenByHand.Count + options.FadedByHand.Count == 0);
        if (ImGui.Selectable(TF($"Show hidden objects ({options.HiddenByHand.Count + options.FadedByHand.Count})") + "  Alt+1")) ShowHidden();
        ImGui.EndDisabled();
        if (ImGui.SmallButton(T("Show all")))
        {
            options.HiddenGroups.Clear();
            options.CurrentCellOnly = false;
            options.HiddenByHand.Clear();
            options.FadedByHand.Clear();
        }
        ImGui.EndPopup();
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
            Log.Info($"cell {_scene.Title}: {_scene.Objects.Count} objects, {_scene.Terrain.Count} terrain cells, {_scene.Water.Count} water planes, {Log.Memory()}");

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
            _pendingFocus = null;
        }

        if (_loading.IsCompletedSuccessfully && _pendingRestore is { } restore)
        {
            _camera.Position = restore.Position;
            _camera.Yaw = restore.Yaw;
            _camera.Pitch = restore.Pitch;
            foreach (var o in _scene!.Objects.Where(o => restore.Selected.Contains(o.Reference))) _selection.Add(o);
            _pendingRestore = null;
        }

        if (_loading.IsCompletedSuccessfully && _pendingFocus is { } focus)
        {
            FocusReference(app, focus);
            _pendingFocus = null;
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

        DrawPresets(app);
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7);
        var shading = (int)options.Shading;
        if (ImGui.Combo("##shading", ref shading, T("Lit\0Unlit\0Normals\0Untextured\0"))) options.Shading = (ShadingMode)shading;
        ImGui.SameLine();

        Toggle(T("Wire"), () => options.Wireframe, v => options.Wireframe = v, T("Wireframe (CK: W)"));
        Toggle(T("Markers"), () => options.ShowMarkers, v => options.ShowMarkers = v, T("Editor markers, model-less lights, actors (CK: M)"));
        Toggle(T("Disabled"), () => options.ShowInitiallyDisabled, v => options.ShowInitiallyDisabled = v, T("Initially disabled references (tinted purple)"));
        DrawShowHide(options);
        Toggle(T("Water"), () => options.ShowWater, v => options.ShowWater = v, null);
        Toggle(T("Land"), () => options.ShowTerrain, v => options.ShowTerrain = v, null);
        Toggle(T("Grass"), () => options.ShowGrass, v => options.ShowGrass = v, T("Grass on the land: the grass types of its landscape textures, placed like the game does (step iMinGrassSize, distance fGrassStartFadeDistance from the INI)"));
        Toggle(T("Fog"), () => options.Fog, v => options.Fog = v, null);
        Toggle(T("Collision"), () => options.ShowCollision, v => options.ShowCollision = v, T("Havok collision of the objects near the camera (CK: F4)"));
        DrawSky(app);
        Toggle(T("Borders"), () => options.CellBorders, v => options.CellBorders = v, T("Cell borders (CK: B)"));
        Toggle(T("Navmesh"), () => options.ShowNavmesh, v => options.ShowNavmesh = v, T("Show navmeshes (N edits them)"));
        Toggle("FX", () => options.ShowParticles, v => options.ShowParticles = v, T("Particles: fire, smoke, dust, sparks"));
        Toggle(T("Anim"), () => options.Animate, v => options.Animate = v, T("Play flames, scrolling water and particles (off: frozen frame, idle CPU)"));
        Toggle("NPC", () => options.HighlightActors, v => options.HighlightActors = app.Settings.HighlightActors = v, T("Tint NPCs and creatures so they are easy to spot in dark places"));

        if (_world is not null)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5);
            if (ImGui.SliderInt("##radius", ref _gridRadius, 0, 5, "grid ±%d"))
            {
                // Applied on the next load; reload now for convenience.
                if (_cell is { } cell) OpenCell(app, cell);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Cells loaded around the centre cell"));
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6.5f);
        var brightness = options.Brightness;
        if (ImGui.SliderFloat("##bright", ref brightness, 0.3f, 4f, "light %.1f")) options.Brightness = brightness;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Ambient and directional light multiplier"));

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
        if (ImGui.Button($"{label}###show{label}")) set(!value);
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

        if (_loading is not null) lines.Add(TF($"Loading {_loadingTitle}…"));
        else if (_scene is null) lines.Add(T("Double-click a cell in Cell View to load it"));
        else
        {
            lines.Add($"{_scene.Title}");
            lines.Add(TF($"{stats.Visible:N0}/{stats.Objects:N0} objects · {stats.DrawCalls:N0} draws · {stats.Triangles / 1000:N0}k tris · {stats.Lights} lights · {_fps:0} fps"));
            if (stats.Grass > 0) lines.Add(TF($"{stats.Grass:N0} grass tufts"));
            var pendingMeshes = _renderer.Meshes.Pending;
            var pendingTextures = _renderer.Textures.Pending;
            if (pendingMeshes + pendingTextures > 0) lines.Add(TF($"streaming: {pendingMeshes} meshes, {pendingTextures} textures"));
            var p = _camera.Position;
            lines.Add(TF($"camera {p.X:0} {p.Y:0} {p.Z:0}") + (_scene.IsInterior ? "" : TF($"  cell {(int)MathF.Floor(p.X / 4096)}, {(int)MathF.Floor(p.Y / 4096)}")));
        }

        if (_havok is not null) lines.Add(T("Havok sim…"));
        if (_navGen is not null) lines.Add(TF($"Generating navmesh… {NavmeshGenerationTime.TotalSeconds:0} s"));
        if (_selection.Count == 1)
        {
            var o = _selection.First();
            lines.Add(TF($"selected: {o.BaseEditorId ?? "?"} [{o.BaseSignature}] {o.ModelPath ?? ""}"));
        }
        else if (_selection.Count > 1)
        {
            lines.Add(TF($"selected: {_selection.Count} objects"));
        }

        if (_document is { PendingCount: > 0 } doc)
            lines.Add(TF($"{doc.PendingCount} unsaved change(s) — Ctrl+S saves into the active file"));

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
