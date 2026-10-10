using System.Numerics;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;
using Mutagen.Bethesda.Plugins;
using StbEditor.App.Services;
using StbEditor.Core;
using StbEditor.Render;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

internal enum EditTool { Select, Move, Rotate, Scale, Land, Navmesh }

/// <summary>
/// Editing in the render window.
///
/// Creation Kit style: LMB drag on a selected object moves it on the ground plane (hold Z for
/// height, X / Y to lock an axis), RMB drag on it rotates (Z axis; X / Y keys for the others),
/// S + LMB drag scales. Modern style: W / E / R gizmo for move / rotate / scale, Q plain select.
///
/// Camera: Shift + mouse orbits the selection (looks around without one), Space + mouse pans,
/// wheel zooms, MMB pans, RMB on empty space + WASD flies, Alt + LMB orbits.
/// </summary>
internal sealed partial class RenderWindow
{
    private enum DragKind { None, Move, Rotate, Scale }

    private EditTool _tool = EditTool.Move;
    private bool _localSpace;
    private bool _gridSnap;
    private float _gridStep = 16f;
    private bool _angleSnap = true;
    private float _angleStep = 15f;

    private DragKind _drag;
    private readonly Dictionary<SceneObject, RefState> _dragBefore = new();
    private Vector3 _dragAnchor;
    private Vector2 _dragMouseStart;
    private SceneObject? _pressObject;
    private bool _pressWasSelected;
    private bool _pressed;
    private bool _looking;
    private bool _gizmoWasUsing;
    private Vector2 _shiftLastMouse;

    private EditDocument? _document;

    // ---- pick mode: a form field waits for a click on a reference.
    private string? _pickRequest;
    private (string Id, SceneObject Object)? _pickResult;

    public string? PickRequestId => _pickRequest;

    public void RequestPick(string id)
    {
        _pickRequest = _pickRequest == id ? null : id;
        _pickResult = null;
    }

    public SceneObject? TakePick(string id)
    {
        if (_pickResult is not { } r || r.Id != id) return null;
        _pickResult = null;
        return r.Object;
    }

    /// <summary>Where a scene object lives, for edits made outside the render window (Inspector).</summary>
    public RefContext RefContextOf(SceneObject obj) => ContextOf(obj);

    /// <summary>Hooks the session's edit document: undo/redo and other panels change objects here.</summary>
    public void AttachDocument(EditDocument document)
    {
        if (_document is not null)
        {
            _document.Changed -= OnDocumentChanged;
            _document.DraftsChanged -= OnDraftsChanged;
        }
        _document = document;
        _document.Changed += OnDocumentChanged;
        _document.DraftsChanged += OnDraftsChanged;
    }

    private bool _terrainDirty;

    private void OnDraftsChanged() => _terrainDirty = true;

    private void OnDocumentChanged(FormKey key, RefState state)
    {
        if (_scene is null) return;
        foreach (var obj in _scene.Objects)
        {
            if (obj.Reference != key) continue;
            obj.Apply(state);
            obj.IsModified = _document?.IsChanged(key) == true;
            if (state.Deleted) _selection.Remove(obj);
        }
    }

    public EditTool Tool => _tool;

    private void DrawEditToolbar()
    {
        ToolButton(T("Select"), EditTool.Select, T("Q — select only"));
        ToolButton(T("Move"), EditTool.Move, T("W — move gizmo (LMB drag on the object works with any tool)"));
        ToolButton(T("Rotate"), EditTool.Rotate, T("E — rotate gizmo"));
        ToolButton(T("Scale"), EditTool.Scale, T("R — uniform scale gizmo"));
        ToolButton(T("Navmesh"), EditTool.Navmesh, T("N — navmesh editing: click vertex/triangle (Ctrl adds), drag moves (Z height), Shift+click adds a vertex (with 2 selected: a triangle), Del deletes, F drops to ground; more in the Navmesh panel"));
        ToolButton(T("Land"), EditTool.Land, T("H — landscape editing: brush settings and textures in the Landscape panel; LMB drag sculpts/paints, Shift inverts raise/lower"));

        Toggle(_localSpace ? T("Local") : T("World"), () => _localSpace, v => _localSpace = v, T("L — gizmo axes: world or the object's own"));
        Toggle(T("Grid"), () => _gridSnap, v => _gridSnap = v, T("G — snap positions to the grid step"));
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 3.2f);
        if (ImGui.BeginCombo("##gridstep", $"{_gridStep:0}", ImGuiComboFlags.NoArrowButton))
        {
            foreach (var step in new[] { 1f, 4f, 8f, 16f, 32f, 64f, 128f, 256f, 512f })
                if (ImGui.Selectable($"{step:0}", Math.Abs(step - _gridStep) < 0.01f)) _gridStep = step;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        Toggle(T("Angle"), () => _angleSnap, v => _angleSnap = v, T("Snap rotation to the angle step"));
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 3.2f);
        if (ImGui.BeginCombo("##anglestep", $"{_angleStep:0}°", ImGuiComboFlags.NoArrowButton))
        {
            foreach (var step in new[] { 1f, 5f, 10f, 15f, 22.5f, 45f, 90f })
                if (ImGui.Selectable($"{step:0.#}°", Math.Abs(step - _angleStep) < 0.01f)) _angleStep = step;
            ImGui.EndCombo();
        }
    }

    private void ToolButton(string label, EditTool tool, string tooltip)
    {
        var active = _tool == tool;
        if (active) ImGui.PushStyleColor(ImGuiCol.Button, Ui.Theme.ButtonActive);
        if (ImGui.Button(label)) _tool = tool;
        if (active) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        ImGui.SameLine();
    }

    // ------------------------------------------------------------------ input

    private void HandleInput(EditorApp app, Vector2 origin, Vector2 size, bool hovered, bool active, float delta)
    {
        var io = ImGui.GetIO();
        var mouse = io.MousePos - origin;
        var focused = ImGui.IsWindowFocused();
        var gizmoBusy = ImGuizmo.IsUsing() || (ImGuizmo.IsOver() && _selection.Count > 0 && _tool is not (EditTool.Select or EditTool.Land or EditTool.Navmesh));

        if (hovered && (ImGui.IsMouseClicked(ImGuiMouseButton.Right) || ImGui.IsMouseClicked(ImGuiMouseButton.Middle) || ImGui.IsMouseClicked(ImGuiMouseButton.Left)))
            ImGui.SetWindowFocus();

        // ---- right button: rotate the selection when pressed on it, otherwise fly/look.
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && _drag == DragKind.None)
        {
            var hit = PickAt(mouse, size);
            if (hit is { } h && _selection.Contains(h.Object)) BeginDrag(DragKind.Rotate, h.Point, io.MousePos);
            else _looking = true;
        }

        if (!ImGui.IsMouseDown(ImGuiMouseButton.Right))
        {
            _looking = false;
            if (_drag == DragKind.Rotate) EndDrag("Rotate");
        }

        if (_looking) Fly(io, delta);
        else if (hovered && io.MouseWheel != 0 && _drag == DragKind.None)
        {
            var step = Math.Max(64f, _camera.Speed * 0.35f) * (io.KeyShift ? 4f : io.KeyCtrl ? 0.25f : 1f);
            _camera.Position += _camera.Forward * io.MouseWheel * step;
        }

        // ---- camera without buttons: Shift orbits/looks, Space pans (Creation Kit).
        var noButtons = !ImGui.IsMouseDown(ImGuiMouseButton.Left) && !ImGui.IsMouseDown(ImGuiMouseButton.Right) && !ImGui.IsMouseDown(ImGuiMouseButton.Middle);
        if (hovered && noButtons && !io.WantTextInput)
        {
            var d = io.MousePos - _shiftLastMouse;
            if (ImGui.IsKeyDown(ImGuiKey.LeftShift) || ImGui.IsKeyDown(ImGuiKey.RightShift))
            {
                if (_selection.Count > 0) _camera.Orbit(SelectionCenter(), d.X * 0.005f, -d.Y * 0.005f);
                else _camera.Rotate(d.X * 0.004f, -d.Y * 0.004f);
            }
            else if (ImGui.IsKeyDown(ImGuiKey.Space))
            {
                Pan(d);
            }
        }
        _shiftLastMouse = io.MousePos;

        if (active && ImGui.IsMouseDown(ImGuiMouseButton.Middle)) Pan(io.MouseDelta);

        if (active && io.KeyAlt && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var pivot = _selection.Count > 0 ? SelectionCenter() : _camera.Position + _camera.Forward * 1000f;
            _camera.Orbit(pivot, io.MouseDelta.X * 0.005f, -io.MouseDelta.Y * 0.005f);
        }

        // ---- landscape and navmesh tools: the left button belongs to them.
        if (_tool == EditTool.Land) HandleLandscape(app, mouse, size, hovered, delta);
        if (_tool == EditTool.Navmesh) HandleNavmesh(app, mouse, size, hovered);
        else if (_renderer is not null) _renderer.NavSelection = null;

        // ---- pick mode: the click goes to the waiting form field.
        if (_pickRequest is { } pickId && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !io.KeyAlt)
        {
            if (PickAt(mouse, size) is { } picked)
            {
                _pickResult = (pickId, picked.Object);
                _pickRequest = null;
            }
            return;
        }
        if (_pickRequest is not null && ImGui.IsKeyPressed(ImGuiKey.Escape, false)) _pickRequest = null;

        // ---- left button: select, and drag the selection.
        if (_tool is not (EditTool.Land or EditTool.Navmesh) && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !io.KeyAlt && !gizmoBusy && _scene is not null)
        {
            _pressed = true;
            _dragMouseStart = io.MousePos;
            var hit = PickAt(mouse, size);
            _pressObject = hit?.Object;
            _pressWasSelected = hit is { } h0 && _selection.Contains(h0.Object);
            _dragAnchor = hit?.Point ?? Vector3.Zero;

            // Pressing an unselected object selects it right away so it can be dragged at once.
            if (hit is { } h && !_pressWasSelected)
            {
                if (!io.KeyCtrl) _selection.Clear();
                _selection.Add(h.Object);
                app.Select(h.Object.Reference);
                _lastSyncedSelection = app.SelectionVersion;
            }
        }

        if (_pressed && ImGui.IsMouseDown(ImGuiMouseButton.Left) && _drag == DragKind.None && _pressObject is not null
            && _selection.Contains(_pressObject) && Vector2.Distance(io.MousePos, _dragMouseStart) > 3)
        {
            BeginDrag(ImGui.IsKeyDown(ImGuiKey.S) ? DragKind.Scale : DragKind.Move, _dragAnchor, _dragMouseStart);
        }

        if (_pressed && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (_drag is DragKind.Move or DragKind.Scale)
            {
                EndDrag(_drag == DragKind.Move ? "Move" : "Scale");
            }
            else if (Vector2.Distance(io.MousePos, _dragMouseStart) < 4)
            {
                // A plain click: on empty space clears, Ctrl+click on a selected object removes it.
                if (_pressObject is null && !io.KeyCtrl) _selection.Clear();
                else if (_pressObject is not null && _pressWasSelected && io.KeyCtrl) _selection.Remove(_pressObject);
                else if (_pressObject is not null && _pressWasSelected && !io.KeyCtrl)
                {
                    _selection.Clear();
                    _selection.Add(_pressObject);
                    app.Select(_pressObject.Reference);
                    _lastSyncedSelection = app.SelectionVersion;
                }
            }

            _pressed = false;
            _pressObject = null;
        }

        if (_drag != DragKind.None) UpdateDrag(io, mouse, size);

        if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && _selection.Count > 0)
        {
            // A teleport door: go through it (to the cell of the door it leads to). Anything else: frame it.
            if (_selection.Count == 1 && _tool is EditTool.Select or EditTool.Move or EditTool.Rotate or EditTool.Scale
                && TeleportOf(app, _selection.First()) is not null)
                GoToLinkedDoor(app, _selection.First());
            else
                FrameSelection();
        }

        // ---- keys
        if ((focused || hovered) && !io.WantTextInput && !_looking && _drag == DragKind.None)
        {
            if (!io.KeyCtrl && !io.KeyShift)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.Q, false)) _tool = EditTool.Select;
                if (ImGui.IsKeyPressed(ImGuiKey.W, false)) _tool = EditTool.Move;
                if (ImGui.IsKeyPressed(ImGuiKey.E, false)) _tool = EditTool.Rotate;
                if (ImGui.IsKeyPressed(ImGuiKey.R, false)) _tool = EditTool.Scale;
                if (ImGui.IsKeyPressed(ImGuiKey.H, false)) _tool = _tool == EditTool.Land ? EditTool.Select : EditTool.Land;
                if (ImGui.IsKeyPressed(ImGuiKey.N, false)) SetNavmeshMode(_tool != EditTool.Navmesh);
                if (ImGui.IsKeyPressed(ImGuiKey.L, false)) _localSpace ^= true;
                if (ImGui.IsKeyPressed(ImGuiKey.G, false)) _gridSnap ^= true;
                if (ImGui.IsKeyPressed(ImGuiKey.F, false) && _tool != EditTool.Navmesh) DropToGround();
                if (ImGui.IsKeyPressed(ImGuiKey.C, false)) FrameSelection();
                if (ImGui.IsKeyPressed(ImGuiKey.T, false)) TopView();
                if (ImGui.IsKeyPressed(ImGuiKey.M, false)) _renderer!.Options.ShowMarkers ^= true;
                if (ImGui.IsKeyPressed(ImGuiKey.B, false)) _renderer!.Options.CellBorders ^= true;
            }
            if (ImGui.IsKeyPressed(ImGuiKey.W, false) && io.KeyShift) _renderer!.Options.Wireframe ^= true;
            if (ImGui.IsKeyPressed(ImGuiKey.Delete, false) && _tool != EditTool.Navmesh) DeleteSelection();
            if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D, false)) DuplicateSelection(app);
            if (ImGui.IsKeyPressed(ImGuiKey.Escape, false)) _selection.Clear();
        }
    }

    private void Fly(ImGuiIOPtr io, float delta)
    {
        _camera.Rotate(io.MouseDelta.X * 0.0035f, -io.MouseDelta.Y * 0.0035f);

        var move = Vector3.Zero;
        if (ImGui.IsKeyDown(ImGuiKey.W)) move += _camera.Forward;
        if (ImGui.IsKeyDown(ImGuiKey.S)) move -= _camera.Forward;
        if (ImGui.IsKeyDown(ImGuiKey.D)) move += _camera.Right;
        if (ImGui.IsKeyDown(ImGuiKey.A)) move -= _camera.Right;
        if (ImGui.IsKeyDown(ImGuiKey.E)) move += Vector3.UnitZ;
        if (ImGui.IsKeyDown(ImGuiKey.Q)) move -= Vector3.UnitZ;

        var speed = _camera.Speed * (io.KeyShift ? 4f : io.KeyCtrl ? 0.25f : 1f);
        if (move != Vector3.Zero) _camera.Position += Vector3.Normalize(move) * speed * delta;

        if (io.MouseWheel != 0) _camera.Speed = Math.Clamp(_camera.Speed * (io.MouseWheel > 0 ? 1.25f : 0.8f), 50f, 20000f);
    }

    private void Pan(Vector2 pixels)
    {
        // Scale with distance to what is in front, so panning feels the same near and far.
        var distance = _selection.Count > 0 ? Vector3.Distance(_camera.Position, SelectionCenter()) : 800f;
        var scale = Math.Max(0.5f, distance / 600f);
        _camera.Position += (-_camera.Right * pixels.X + _camera.Up * pixels.Y) * scale;
    }

    private void TopView()
    {
        var center = _selection.Count > 0 ? SelectionCenter() : _camera.Position + _camera.Forward * 1000f;
        var height = Math.Max(1500f, Vector3.Distance(_camera.Position, center));
        _camera.Pitch = -1.5533f;
        _camera.Position = center + new Vector3(0, -height * 0.0175f, height);
    }

    private PickHit? PickAt(Vector2 mouse, Vector2 size)
    {
        if (_scene is null) return null;
        var (origin, direction) = _camera.Ray(mouse, size);
        return Picking.Pick(_scene, origin, direction, _renderer!.Options);
    }

    // ------------------------------------------------------------------ CK-style drags

    private void BeginDrag(DragKind kind, Vector3 anchor, Vector2 mouseStart)
    {
        _drag = kind;
        _dragAnchor = anchor;
        _dragMouseStart = mouseStart;
        _dragBefore.Clear();
        foreach (var obj in _selection) _dragBefore[obj] = obj.State;
    }

    private void UpdateDrag(ImGuiIOPtr io, Vector2 mouse, Vector2 size)
    {
        var total = io.MousePos - _dragMouseStart;

        switch (_drag)
        {
            case DragKind.Move:
            {
                Vector3 offset;
                if (ImGui.IsKeyDown(ImGuiKey.Z))
                {
                    // Height: pixels → world units at the anchor's distance.
                    var distance = Vector3.Distance(_camera.Position, _dragAnchor);
                    var unitsPerPixel = 2 * distance * MathF.Tan(_camera.FieldOfView / 2) / size.Y;
                    offset = new Vector3(0, 0, -total.Y * unitsPerPixel);
                }
                else
                {
                    var (origin, direction) = _camera.Ray(mouse, size);
                    if (MathF.Abs(direction.Z) < 1e-4f) return;
                    var t = (_dragAnchor.Z - origin.Z) / direction.Z;
                    if (t < 0) return;
                    var point = origin + direction * t;
                    offset = point - _dragAnchor;
                    offset.Z = 0;
                    if (ImGui.IsKeyDown(ImGuiKey.X)) offset.Y = 0;
                    if (ImGui.IsKeyDown(ImGuiKey.Y)) offset.X = 0;
                }

                foreach (var (obj, before) in _dragBefore)
                {
                    var position = before.Position + offset;
                    if (_gridSnap) position = Snap(position, _gridStep, snapZ: ImGui.IsKeyDown(ImGuiKey.Z));
                    obj.Apply(before with { Position = position });
                }
                break;
            }

            case DragKind.Rotate:
            {
                var degrees = total.X * 0.5f;
                if (_angleSnap) degrees = MathF.Round(degrees / _angleStep) * _angleStep;
                var axis = ImGui.IsKeyDown(ImGuiKey.X) ? Vector3.UnitX : ImGui.IsKeyDown(ImGuiKey.Y) ? Vector3.UnitY : Vector3.UnitZ;
                RotateSelection(axis, TransformMath.Radians(degrees));
                break;
            }

            case DragKind.Scale:
            {
                var factor = MathF.Exp(-total.Y * 0.005f + total.X * 0.005f);
                foreach (var (obj, before) in _dragBefore)
                    obj.Apply(before with { Scale = Math.Clamp(before.Scale * factor, 0.01f, 10f) });
                break;
            }
        }
    }

    /// <summary>Rotates the selection (from its drag-start state) about a world axis through its centre.</summary>
    private void RotateSelection(Vector3 axis, float radians)
    {
        var pivot = _dragBefore.Count == 1 ? _dragBefore.Keys.First().Position : Center(_dragBefore.Values.Select(s => s.Position));
        // Clockwise seen from the positive axis, like a heading turning to the right.
        var rotation = Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateFromAxisAngle(axis, -radians) * Matrix4x4.CreateTranslation(pivot);

        foreach (var (obj, before) in _dragBefore)
        {
            var world = SceneBuilder.ReferenceTransform(before.Position, before.Rotation, before.Scale).ToMatrix() * rotation;
            var (position, euler, _) = TransformMath.Decompose(world);
            obj.Apply(before with { Position = position, Rotation = euler });
        }
    }

    private void EndDrag(string name)
    {
        _drag = DragKind.None;
        Commit(name, _dragBefore);
        _dragBefore.Clear();
    }

    /// <summary>Records the change from <paramref name="before"/> to the objects' current state as one undo step.</summary>
    private void Commit(string name, Dictionary<SceneObject, RefState> before)
    {
        if (_document is null || before.Count == 0) return;

        foreach (var obj in before.Keys) _document.RegisterContext(obj.Reference, ContextOf(obj));

        var changes = before
            .Select(kv => (kv.Key.Reference, kv.Key.LoadedState, kv.Value, kv.Key.State))
            .ToList();

        var count = changes.Count;
        _document.Execute(count == 1 ? $"{name} {before.Keys.First().BaseEditorId}" : $"{name} {count} objects", changes);
    }

    private static Vector3 Snap(Vector3 p, float step, bool snapZ) => new(
        MathF.Round(p.X / step) * step,
        MathF.Round(p.Y / step) * step,
        snapZ ? MathF.Round(p.Z / step) * step : p.Z);

    private static Vector3 Center(IEnumerable<Vector3> points)
    {
        var sum = Vector3.Zero;
        var n = 0;
        foreach (var p in points) { sum += p; n++; }
        return n == 0 ? Vector3.Zero : sum / n;
    }

    // ------------------------------------------------------------------ gizmo

    private unsafe void DrawGizmo(Vector2 origin, Vector2 size)
    {
        _gizmoOver = false;
        if (_selection.Count == 0 || _tool is EditTool.Select or EditTool.Land or EditTool.Navmesh || _drag != DragKind.None || _scene is null) return;

        ImGuizmo.SetOrthographic(false);
        ImGuizmo.SetDrawlist(ImGui.GetWindowDrawList());
        ImGuizmo.SetRect(origin.X, origin.Y, size.X, size.Y);

        var view = _camera.View;
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(_camera.FieldOfView, size.X / size.Y, 10f, 1_000_000f);

        // Pivot: the object itself (local space, single selection) or the selection centre.
        var single = _selection.Count == 1 ? _selection.First() : null;
        Matrix4x4 pivot;
        if (_localSpace && single is not null)
        {
            var rotationOnly = SceneBuilder.ReferenceTransform(single.Position, single.Rotation, 1f).ToMatrix();
            pivot = rotationOnly;
        }
        else
        {
            pivot = Matrix4x4.CreateTranslation(single?.Position ?? Center(_selection.Select(o => o.Position)));
        }

        // ImGuizmo applies each frame's rotation on top of the matrix it is given. Rebuilding the
        // pivot from the objects loses that in world mode (the pivot has no rotation), so the
        // objects only ever got the last frame's few degrees: during a drag feed back its own output.
        if (_gizmoWasUsing && !Environment.GetCommandLineArgs().Contains("--gizmo-old")) pivot = _gizmoPivot;

        var operation = _tool switch
        {
            EditTool.Rotate => ImGuizmoOperation.Rotate,
            EditTool.Scale => ImGuizmoOperation.Scaleu,
            _ => ImGuizmoOperation.Translate,
        };
        var mode = _localSpace ? ImGuizmoMode.Local : ImGuizmoMode.World;

        var snap = _tool switch
        {
            EditTool.Move when _gridSnap => new Vector3(_gridStep),
            EditTool.Rotate when _angleSnap => new Vector3(_angleStep),
            _ => Vector3.Zero,
        };

        var before = pivot;
        var changed = ImGuizmo.Manipulate(&view.M11, &projection.M11, operation, mode, &pivot.M11, null, snap == Vector3.Zero ? null : &snap.X);
        var usingNow = ImGuizmo.IsUsing();
        _gizmoPivot = pivot;
        _gizmoOver = ImGuizmo.IsOver();
        var clip = Vector4.Transform(new Vector4(pivot.Translation, 1f), view * projection);
        if (clip.W > 0) _gizmoScreen = origin + new Vector2((clip.X / clip.W * 0.5f + 0.5f) * size.X, (0.5f - clip.Y / clip.W * 0.5f) * size.Y);

        if (usingNow && !_gizmoWasUsing)
        {
            _dragBefore.Clear();
            foreach (var obj in _selection) _dragBefore[obj] = obj.State;
            _gizmoStart = before;
        }

        if (usingNow && changed)
        {
            // Apply the pivot's total change since the drag started to each object's start state.
            Matrix4x4.Invert(_gizmoStart, out var inverseStart);
            var deltaMatrix = inverseStart * pivot;

            foreach (var (obj, start) in _dragBefore)
            {
                if (_tool == EditTool.Scale)
                {
                    var factor = new Vector3(pivot.M11, pivot.M12, pivot.M13).Length() / Math.Max(1e-6f, new Vector3(_gizmoStart.M11, _gizmoStart.M12, _gizmoStart.M13).Length());
                    var center = new Vector3(_gizmoStart.M41, _gizmoStart.M42, _gizmoStart.M43);
                    obj.Apply(start with
                    {
                        Scale = Math.Clamp(start.Scale * factor, 0.01f, 10f),
                        Position = center + (start.Position - center) * factor,
                    });
                    continue;
                }

                var world = SceneBuilder.ReferenceTransform(start.Position, start.Rotation, start.Scale).ToMatrix() * deltaMatrix;
                var (position, euler, _) = TransformMath.Decompose(world);
                obj.Apply(start with { Position = position, Rotation = _tool == EditTool.Move ? start.Rotation : euler });
            }
        }

        if (!usingNow && _gizmoWasUsing)
        {
            Commit(_tool switch { EditTool.Rotate => T("Rotate"), EditTool.Scale => T("Scale"), _ => T("Move") }, _dragBefore);
            _dragBefore.Clear();
        }

        _gizmoWasUsing = usingNow;
    }

    private Matrix4x4 _gizmoStart;
    private Matrix4x4 _gizmoPivot;
    private bool _gizmoOver;
    private Vector2 _gizmoScreen;
    private int _gizmoTest;
    private int _gizmoTestFrame;
    private float _gizmoTestRadius;
    private SceneObject? _gizmoTestObject;
    private Vector3 _gizmoTestBefore;
    private Vector3 _gizmoTestBeforePosition;
    private bool _gizmoTestMove;

    /// <summary>
    /// --autotest-gizmo (with --cell): select an object, rotate tool in world space, find a ring of the
    /// gizmo by moving a fake mouse out from the centre, drag it, check the object turned. True when done.
    /// </summary>
    public bool GizmoTestStep()
    {
        _gizmoTestFrame++;
        switch (_gizmoTest)
        {
            case 0 when IsSettled:
                _gizmoTestObject = _scene!.Objects
                    .Where(o => o.Model is { IsEmpty: false } && !o.IsDeleted && o.BoundsRadius is > 40 and < 400)
                    .OrderBy(o => Vector3.Distance(o.Position, _scene.Focus)).FirstOrDefault();
                if (_gizmoTestObject is null) { Log.Error("autotest-gizmo: no object"); return true; }
                _selection.Clear();
                _selection.Add(_gizmoTestObject);
                _gizmoTestMove = Environment.GetCommandLineArgs().Contains("move");
                _tool = _gizmoTestMove ? EditTool.Move : EditTool.Rotate;
                _localSpace = false;
                _gridSnap = false;
                _angleSnap = false;
                // From above, so the Z ring is a circle and a diagonal from the centre meets only it.
                _camera.Position = _gizmoTestObject.Position + new Vector3(0, -120, 700);
                _camera.LookAt(_gizmoTestObject.Position);
                _gizmoTestBefore = _gizmoTestObject.Rotation;
                _gizmoTestBeforePosition = _gizmoTestObject.Position;
                _gizmoTest = 1;
                _gizmoTestFrame = 0;
                _gizmoTestRadius = 30;
                break;
            case 1 when _gizmoTestFrame > 10:
                _gizmoTest = 2;
                break;
            case 2:
                if (_gizmoOver && _gizmoTestFrame > 2)
                {
                    Log.Info($"autotest-gizmo: ring at {_gizmoTestRadius:0} px from the centre");
                    StbEditor.App.Ui.ImGuiLayer.TestMouse = (ImGuiLayerPosition(), true);
                    _gizmoTest = 3;
                    _gizmoTestFrame = 0;
                    break;
                }
                if (_gizmoTestFrame > 2)
                {
                    _gizmoTestRadius += 3;
                    _gizmoTestFrame = 0;
                    if (_gizmoTestRadius > 500) { Log.Error("autotest-gizmo: no ring found"); StbEditor.App.Ui.ImGuiLayer.TestMouse = null; return true; }
                }
                StbEditor.App.Ui.ImGuiLayer.TestMouse = (ImGuiLayerPosition(), false);
                break;
            case 3 when _gizmoTestFrame <= 40:
                // Along the tangent of the ring (found down-right of the centre).
                StbEditor.App.Ui.ImGuiLayer.TestMouse = (ImGuiLayerPosition() + Tangent() * (_gizmoTestFrame * 3f), true);
                break;
            case 3:
                StbEditor.App.Ui.ImGuiLayer.TestMouse = (ImGuiLayerPosition() + Tangent() * 120f, false);
                _gizmoTest = 4;
                _gizmoTestFrame = 0;
                break;
            case 4 when _gizmoTestFrame > 5:
                StbEditor.App.Ui.ImGuiLayer.TestMouse = null;
                var after = _gizmoTestObject!.Rotation;
                Log.Info($"autotest-gizmo: {_gizmoTestObject.BaseEditorId} rotation {Deg(_gizmoTestBefore)} -> {Deg(after)}, position {_gizmoTestBeforePosition} -> {_gizmoTestObject.Position}, undo: {_document?.History.UndoName}");
                return true;
        }
        return false;

        // Rotate: a diagonal meets only the Z ring (seen from above), then along its tangent.
        // Move: the X arrow points right on screen; drag along it.
        Vector2 ImGuiLayerPosition() => _gizmoScreen + (_gizmoTestMove ? new Vector2(1, 0) : new Vector2(0.7071f)) * _gizmoTestRadius;
        Vector2 Tangent() => _gizmoTestMove ? new Vector2(1, 0) : new Vector2(-1, 1);
        static string Deg(Vector3 r) => $"({TransformMath.Degrees(r.X):0.#}, {TransformMath.Degrees(r.Y):0.#}, {TransformMath.Degrees(r.Z):0.#})";
    }

    // ------------------------------------------------------------------ commands

    /// <summary>F (as in the Creation Kit): drops each selected object onto what is below it.</summary>
    private void DropToGround()
    {
        if (_scene is null || _selection.Count == 0) return;

        var before = _selection.ToDictionary(o => o, o => o.State);
        foreach (var obj in _selection)
        {
            var (bottom, top) = VerticalExtent(obj);
            var start = new Vector3(obj.Position.X, obj.Position.Y, top + 32f);
            if (Picking.CastDown(_scene, start, _selection, _renderer!.Options) is not { } groundZ) continue;
            obj.Apply(obj.State with { Position = obj.Position + new Vector3(0, 0, groundZ - bottom) });
        }

        Commit(T("Drop to ground"), before);
    }

    /// <summary>Lowest and highest world Z of an object's model.</summary>
    private static (float Bottom, float Top) VerticalExtent(SceneObject obj)
    {
        if (obj.Model is not { Shapes.Count: > 0 } model) return (obj.Position.Z, obj.Position.Z + 32);

        float bottom = float.MaxValue, top = float.MinValue;
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? model.GeometryMin.X : model.GeometryMax.X,
                (i & 2) == 0 ? model.GeometryMin.Y : model.GeometryMax.Y,
                (i & 4) == 0 ? model.GeometryMin.Z : model.GeometryMax.Z);
            var z = obj.Transform.Apply(corner).Z;
            bottom = Math.Min(bottom, z);
            top = Math.Max(top, z);
        }
        return (bottom, top);
    }

    /// <summary>Delete marks references deleted; saving decides how (own refs removed, master refs disabled safely).</summary>
    private void DeleteSelection()
    {
        if (_selection.Count == 0) return;
        var before = _selection.ToDictionary(o => o, o => o.State);
        foreach (var obj in _selection) obj.Apply(obj.State with { Deleted = true });
        Commit(T("Delete"), before);
        _selection.Clear();
    }

    private RefContext ContextOf(SceneObject obj) => new(
        obj.Cell,
        _scene?.Worldspace,
        obj.IsPersistent,
        _scene?.IsInterior ?? true,
        obj.Record,
        obj.Base,
        obj.BaseSignature);

    /// <summary>Places a new reference of <paramref name="baseKey"/> under the cursor.</summary>
    private void PlaceAt(EditorApp app, FormKey baseKey, Vector2 mouse, Vector2 size)
    {
        if (_scene is null || app.Session is not { } session || _document is null) return;

        var signature = session.Catalog.Get(baseKey)?.Signature;
        if (signature is null || !StbEditor.Data.Catalog.RecordTypes.IsPlaceable(signature))
        {
            Log.Warn($"{session.Workspace.FormIdText(baseKey)} [{signature}] cannot be placed in the world");
            return;
        }

        if (app.ActivePlugin is null)
        {
            app.ShowActiveFileDialog(T("New objects need an active file for their FormIDs. Choose one, then drop again."));
            return;
        }

        var (origin, direction) = _camera.Ray(mouse, size);
        var point = Picking.Pick(_scene, origin, direction, _renderer!.Options)?.Point
            ?? Picking.PickTerrain(_scene, origin, direction)
            ?? origin + direction * 400f;

        var created = CreateNew(app, baseKey, signature, point, Vector3.Zero, 1f);
        if (created is null) return;

        _selection.Clear();
        _selection.Add(created);
        app.Select(created.Reference);
        _lastSyncedSelection = app.SelectionVersion;
    }

    /// <summary>Ctrl+D: copies of the selection in place (as in the Creation Kit), selected for moving.</summary>
    private void DuplicateSelection(EditorApp app)
    {
        if (_selection.Count == 0) return;
        if (app.ActivePlugin is null)
        {
            app.ShowActiveFileDialog(T("Duplicates need an active file for their FormIDs."));
            return;
        }

        var copies = new List<SceneObject>();
        foreach (var source in _selection.ToList())
        {
            var copy = CreateNew(app, source.Base, source.BaseSignature, source.Position, source.Rotation, source.Scale, record: false);
            if (copy is not null) copies.Add(copy);
        }

        if (copies.Count == 0) return;
        CommitCreation(copies.Count == 1 ? $"Duplicate {copies[0].BaseEditorId}" : $"Duplicate {copies.Count} objects", copies);
        _selection.Clear();
        foreach (var copy in copies) _selection.Add(copy);
    }

    /// <summary>Places a reference programmatically (autotests); same path as a drop from the Object Window.</summary>
    public void PlaceForTest(EditorApp app, FormKey baseKey, Vector3 position)
    {
        var signature = app.Session!.Catalog.Get(baseKey)!.Signature;
        CreateNew(app, baseKey, signature, position, Vector3.Zero, 1f);
    }

    /// <summary>Creates the reference in the scene and the document (one undo step unless batched).</summary>
    private SceneObject? CreateNew(EditorApp app, FormKey baseKey, string signature, Vector3 position, Vector3 rotation, float scale, bool record = true)
    {
        var session = app.Session!;
        var builder = Builder(session);
        var actor = signature is "NPC_" or "LVLN";

        // Owning cell: the interior, or the exterior cell under the point (actors: the world's persistent cell).
        FormKey cell;
        var persistent = actor;
        if (_scene!.IsInterior)
        {
            cell = _cell ?? FormKey.Null;
        }
        else
        {
            var world = _scene.Worldspace!.Value;
            var gx = (int)MathF.Floor(position.X / TerrainCell.CellSize);
            var gy = (int)MathF.Floor(position.Y / TerrainCell.CellSize);
            if (actor && builder.PersistentCellOf(world) is { } top) cell = top;
            else if ((builder.CellAt(world, gx, gy) ?? _pendingCells.FirstOrDefault(d => d.Cell.Grid!.Point.X == gx && d.Cell.Grid.Point.Y == gy && d.World == world)?.Key) is { } c)
            {
                cell = c;
                persistent = false;
            }
            else
            {
                // No cell there yet (new worldspace, edge of the map): make one, saved with the reference.
                try
                {
                    var draft = NewRecords.ExteriorCell(app.ActivePlugin!.Allocate(), session.Game.Release, world, gx, gy);
                    _pendingCells.Add(draft);
                    cell = draft.Key;
                    persistent = false;
                    Log.Info($"new exterior cell {gx}, {gy} ({draft.Key}) for the placed object");
                }
                catch (Exception ex)
                {
                    Log.Error(ex.Message);
                    return null;
                }
            }
        }

        FormKey key;
        try
        {
            key = app.ActivePlugin!.Allocate();
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            return null;
        }

        var obj = builder.CreatePlaced(key, baseKey, position, rotation, scale, cell, persistent);
        obj.LoadedState = EditDocument.Nonexistent;
        _scene.Objects.Add(obj);
        _document!.RegisterContext(key, new RefContext(cell, _scene.Worldspace, persistent, _scene.IsInterior, null, baseKey, signature));

        if (record) CommitCreation(TF($"Place {obj.BaseEditorId}"), [obj]);
        return obj;
    }

    /// <summary>Cells made on the fly for references being placed; committed with them.</summary>
    private readonly List<CellDraft> _pendingCells = [];

    private void CommitCreation(string name, List<SceneObject> created)
    {
        var changes = created.Select(o => (o.Reference, EditDocument.Nonexistent, EditDocument.Nonexistent, o.State)).ToList();
        _document!.Execute(name, changes, [.. _pendingCells]);
        _pendingCells.Clear();
        foreach (var o in created) o.IsModified = true;
    }

    /// <summary>
    /// After a cell (re)load: references created in this session that fall into the loaded area
    /// get their scene objects back.
    /// </summary>
    private void RestoreNewReferences(EditorApp app)
    {
        if (_document is null || _scene is null || app.Session is not { } session) return;

        var builder = Builder(session);
        var present = _scene.Objects.Select(o => o.Reference).ToHashSet();

        foreach (var (key, context, state) in _document.NewReferences)
        {
            if (present.Contains(key)) continue;
            var inside = _scene.IsInterior
                ? context.Interior && context.Cell == _cell
                : !context.Interior && context.Worldspace == _scene.Worldspace
                  && Math.Abs(MathF.Floor(state.Position.X / TerrainCell.CellSize) - _scene.CenterCell.X) <= _gridRadius
                  && Math.Abs(MathF.Floor(state.Position.Y / TerrainCell.CellSize) - _scene.CenterCell.Y) <= _gridRadius;
            if (!inside) continue;

            var obj = builder.CreatePlaced(key, context.Base, state.Position, state.Rotation, state.Scale, context.Cell, context.Persistent);
            obj.LoadedState = EditDocument.Nonexistent;
            obj.IsModified = true;
            _scene.Objects.Add(obj);
        }
    }

    /// <summary>What to come back to after a save reloads everything.</summary>
    public sealed record ViewState(FormKey? Cell, FormKey? World, int GridX, int GridY, Vector3 Position, float Yaw, float Pitch, int GridRadius, FormKey[] Selected);

    public ViewState? CaptureView()
    {
        if (_scene is null || (_cell is null && _world is null)) return null;
        var grid = _scene.IsInterior ? (0, 0) : _scene.CenterCell;
        return new ViewState(_cell, _world, grid.Item1, grid.Item2, _camera.Position, _camera.Yaw, _camera.Pitch, _gridRadius,
            _selection.Select(o => o.Reference).ToArray());
    }

    public void RestoreView(EditorApp app, ViewState view)
    {
        _gridRadius = view.GridRadius;
        _pendingRestore = view;
        if (view.World is { } world) OpenExterior(app, world, view.GridX, view.GridY);
        else if (view.Cell is { } cell) OpenCell(app, cell);
    }

    private ViewState? _pendingRestore;

    /// <summary>Forgets the scene (its records belong to a workspace that is being closed).</summary>
    public void DropScene()
    {
        _scene = null;
        _selection.Clear();
        _loading = null;
    }

    /// <summary>Transform fields of the selected reference, for the Inspector.</summary>
    public SceneObject? SingleSelection => _selection.Count == 1 ? _selection.First() : null;

    /// <summary>Applies a field edit from the Inspector live; <see cref="CommitFieldEdit"/> records it.</summary>
    public void PreviewFieldEdit(SceneObject obj, RefState state)
    {
        if (!_fieldEditBefore.ContainsKey(obj)) _fieldEditBefore[obj] = obj.State;
        obj.Apply(state);
    }

    public void CommitFieldEdit(string name)
    {
        Commit(name, _fieldEditBefore);
        _fieldEditBefore.Clear();
    }

    private readonly Dictionary<SceneObject, RefState> _fieldEditBefore = new();

    private static string HelpLine =>
        T("LMB select / drag move (Z height, X/Y lock) · RMB drag on selection rotate · S+drag scale · ") +
        T("Shift+mouse orbit · Space+mouse pan · wheel zoom · RMB+WASD fly · W/E/R gizmo · F drop · C focus · T top · Del delete · Ctrl+Z undo · F1 help");
}
