using System.Numerics;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Services;
using StbEditor.Core;
using StbEditor.Render;

namespace StbEditor.App.Panels;

/// <summary>
/// Going through doors: double-click a teleport door (or "Go to linked door" in the Inspector) opens
/// the cell of the door it leads to and puts the camera on its teleport marker, door selected.
/// </summary>
internal sealed partial class RenderWindow
{
    private (FormKey Door, Vector3 Marker, float Heading)? _pendingDoor;

    /// <summary>"Finding door…" while the door index is built (first use only).</summary>
    public bool IsFindingDoor => _pendingDoor is not null;

    /// <summary>The teleport of a reference as it is now (with unsaved edits), if it has one.</summary>
    public static ITeleportDestinationGetter? TeleportOf(EditorApp app, SceneObject obj)
    {
        var record = app.Document.Draft(obj.Reference) is OverrideDraft { Record: IPlacedObjectGetter edited } ? edited : obj.Record as IPlacedObjectGetter;
        return record?.TeleportDestination is { Door.IsNull: false } teleport ? teleport : null;
    }

    public void GoToLinkedDoor(EditorApp app, SceneObject door)
    {
        if (TeleportOf(app, door) is not { } teleport)
        {
            Log.Warn($"{door.BaseEditorId}: not a teleport door");
            return;
        }

        // Its teleport marker is where the player comes out of the other door.
        var marker = new Vector3(teleport.Position.X, teleport.Position.Y, teleport.Position.Z);
        _pendingDoor = (teleport.Door.FormKey, marker, teleport.Rotation.Z);
    }

    private FormKey? _arrivingAt;

    /// <summary>Called every frame: once the door index is ready, opens the destination.</summary>
    private void PollDoor(EditorApp app)
    {
        // Arrived: the Inspector shows the door we came out of (without re-framing the camera).
        if (_arrivingAt is { } arrived && _loading is null && _pendingRestore is null)
        {
            _arrivingAt = null;
            if (_scene?.Objects.Any(o => o.Reference == arrived) == true)
            {
                app.Select(arrived);
                _lastSyncedSelection = app.SelectionVersion;
            }
        }

        if (_pendingDoor is not { } pending || app.Session is not { } session) return;

        // Already loaded (a door within this interior, or the exterior around it).
        var here = _scene?.Objects.FirstOrDefault(o => o.Reference == pending.Door);
        if (here is not null && _loading is null)
        {
            _pendingDoor = null;
            _selection.Clear();
            _selection.Add(here);
            PlaceCameraAtMarker(pending.Marker, pending.Heading);
            app.Select(here.Reference);
            _lastSyncedSelection = app.SelectionVersion;
            return;
        }

        var doors = session.Doors;
        if (!doors.IsCompleted) return;
        _pendingDoor = null;
        if (!doors.IsCompletedSuccessfully)
        {
            Log.Error($"door index failed: {doors.Exception?.GetBaseException().Message}");
            return;
        }

        if (doors.Result.CellOf(pending.Door) is not { } cellKey || session.Catalog.Get(cellKey)?.Winner.Cell is not { } placement)
        {
            Log.Warn($"door {session.Workspace.FormIdText(pending.Door)}: its cell was not found");
            return;
        }

        var eye = pending.Marker + new Vector3(0, 0, 110);
        if (placement.IsInterior)
        {
            _pendingRestore = new ViewState(cellKey, null, 0, 0, eye, pending.Heading, -0.15f, _gridRadius, [pending.Door]);
            OpenCell(app, cellKey);
        }
        else if (placement.Worldspace is { } world)
        {
            // Exterior doors are persistent (they sit in the worldspace's persistent cell): load the grid around the marker.
            var gx = (int)MathF.Floor(pending.Marker.X / TerrainCell.CellSize);
            var gy = (int)MathF.Floor(pending.Marker.Y / TerrainCell.CellSize);
            _pendingRestore = new ViewState(null, world, gx, gy, eye, pending.Heading, -0.15f, _gridRadius, [pending.Door]);
            OpenExterior(app, world, gx, gy);
        }
        _arrivingAt = pending.Door;
        Log.Info($"through the door to {session.Catalog.Get(cellKey)?.EditorId ?? session.Workspace.FormIdText(cellKey)}");
    }

    private int _doorTest;
    private string? _doorTestFrom;

    /// <summary>
    /// --autotest-door (with --cell, an interior): go out through a teleport door, then back in
    /// through the door we arrived at. Logs each scene and the selected door. True when done.
    /// </summary>
    public bool DoorTestStep(EditorApp app)
    {
        if (!IsSettled || _pendingDoor is not null || _arrivingAt is not null) return false;
        var selected = _selection.FirstOrDefault();
        switch (_doorTest)
        {
            case 0:
                var door = _scene!.Objects.FirstOrDefault(o => TeleportOf(app, o) is not null);
                if (door is null) { Log.Error("autotest-door: no teleport door here"); return true; }
                _doorTestFrom = _scene.Title;
                Log.Info($"autotest-door: in {_scene.Title}, through {door.BaseEditorId} {app.Session!.Workspace.FormIdText(door.Reference)}");
                _selection.Clear();
                _selection.Add(door);
                GoToLinkedDoor(app, door);
                _doorTest = 1;
                return false;
            case 1:
                Log.Info($"autotest-door: arrived in {_scene!.Title}, selected {selected?.BaseEditorId} {(selected is null ? "" : app.Session!.Workspace.FormIdText(selected.Reference))}, camera {_camera.Position}, distance to door {(selected is null ? -1 : Vector3.Distance(selected.Position, _camera.Position)):0}");
                if (selected is null || TeleportOf(app, selected) is null) { Log.Error("autotest-door: arrived door has no teleport"); return true; }
                GoToLinkedDoor(app, selected);
                _doorTest = 2;
                return false;
            default:
                Log.Info($"autotest-door: back in {_scene!.Title} (started in {_doorTestFrom}), selected {selected?.BaseEditorId}");
                return true;
        }
    }

    private void PlaceCameraAtMarker(Vector3 marker, float heading)
    {
        _camera.Position = marker + new Vector3(0, 0, 110);
        _camera.Yaw = heading;
        _camera.Pitch = -0.15f;
    }
}
