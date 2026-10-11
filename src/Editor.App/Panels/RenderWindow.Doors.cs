using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Services;
using StbEditor.App.Ui;
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

    private SceneObject? _doorQuestion;
    private bool _doorQuestionOpen;

    /// <summary>Double-click on a door: ask first (as the Creation Kit), unless turned off in Settings.</summary>
    public void AskGoThroughDoor(EditorApp app, SceneObject door)
    {
        if (!app.Settings.ConfirmDoorTravel)
        {
            GoToLinkedDoor(app, door);
            return;
        }
        _doorQuestion = door;
        _doorQuestionOpen = true;
    }

    private string DoorQuestionTitle => Loc.T("Go through the door") + "###doorquestion";

    private void DrawDoorQuestion(EditorApp app)
    {
        if (_doorQuestion is not { } door || app.Session is not { } session) return;
        if (_doorQuestionOpen)
        {
            ImGui.OpenPopup(DoorQuestionTitle);
            _doorQuestionOpen = false;
        }

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f));
        if (!ImGui.BeginPopupModal(DoorQuestionTitle, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            if (!ImGui.IsPopupOpen(DoorQuestionTitle)) _doorQuestion = null;
            return;
        }

        // Where it leads, when the door index already knows.
        var destination = TeleportOf(app, door)?.Door.FormKey;
        string? place = null;
        if (destination is { } target && session.Doors.IsCompletedSuccessfully && session.Doors.Result.CellOf(target) is { } cellKey
            && session.Catalog.Get(cellKey) is { } cell)
            place = cell.Winner.Cell is { IsPersistentCell: true, Worldspace: { } w } && TeleportOf(app, door) is { } teleport
                ? ExteriorPlace(session, w, teleport.Position.X, teleport.Position.Y)
                : cell.Name is { Length: > 0 } n ? $"{n} ({cell.EditorId})" : cell.EditorId;

        DoorQuestionText = place is null ? Loc.T("Go to the cell this door leads to?") : Loc.TF($"Go to {place}?");
        ImGui.Text(DoorQuestionText);
        ImGui.TextColored(Theme.TextDim, Loc.TF($"{door.BaseEditorId} {session.Workspace.FormIdText(door.Reference)}"));
        ImGui.Spacing();
        var ask = app.Settings.ConfirmDoorTravel;
        if (ImGui.Checkbox(Loc.T("Ask every time"), ref ask)) app.Settings.ConfirmDoorTravel = ask;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Off: a double-click goes straight through (Settings can turn it back on)"));
        ImGui.Spacing();
        var width = ImGui.GetFontSize() * 7;
        if (ImGui.Button(Loc.T("Yes"), new Vector2(width, 0)) || ImGui.IsKeyPressed(ImGuiKey.Enter, false))
        {
            GoToLinkedDoor(app, door);
            _doorQuestion = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button(Loc.T("No"), new Vector2(width, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            _doorQuestion = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    /// <summary>"Riverwood (Tamriel 5, -11)": the named exterior cell at a point, else the worldspace and grid.</summary>
    private static string ExteriorPlace(EditorSession session, FormKey world, float x, float y)
    {
        var gx = (int)MathF.Floor(x / TerrainCell.CellSize);
        var gy = (int)MathF.Floor(y / TerrainCell.CellSize);
        var worldName = session.Catalog.Get(world)?.EditorId ?? world.ToString();
        var cell = (session.Catalog.BySignature.GetValueOrDefault("CELL") ?? [])
            .FirstOrDefault(c => c.Winner.Cell is { IsInterior: false, IsPersistentCell: false } p && p.Worldspace == world && p.GridX == gx && p.GridY == gy);
        var name = cell?.Name is { Length: > 0 } n ? n : cell?.EditorId;
        return name is null ? $"{worldName} {gx}, {gy}" : $"{name} ({worldName} {gx}, {gy})";
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

    /// <summary>Called every frame: once the door index is ready, opens the destination.</summary>
    private void PollDoor(EditorApp app)
    {
        if (_pendingDoor is not { } pending || app.Session is not { } session) return;

        // Already loaded (a door within this interior, or the exterior around it).
        var here = _scene?.Objects.FirstOrDefault(o => o.Reference == pending.Door);
        if (here is not null && _loading is null)
        {
            _pendingDoor = null;
            // The camera stands at the marker; nothing gets selected (a selection would make Shift orbit around the door).
            _selection.Clear();
            PlaceCameraAtMarker(pending.Marker, pending.Heading);
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
            _pendingRestore = new ViewState(cellKey, null, 0, 0, eye, pending.Heading, -0.15f, _gridRadius, []);
            OpenCell(app, cellKey);
        }
        else if (placement.Worldspace is { } world)
        {
            // Exterior doors are persistent (they sit in the worldspace's persistent cell): load the grid around the marker.
            var gx = (int)MathF.Floor(pending.Marker.X / TerrainCell.CellSize);
            var gy = (int)MathF.Floor(pending.Marker.Y / TerrainCell.CellSize);
            _pendingRestore = new ViewState(null, world, gx, gy, eye, pending.Heading, -0.15f, _gridRadius, []);
            OpenExterior(app, world, gx, gy);
        }
        Log.Info($"through the door to {session.Catalog.Get(cellKey)?.EditorId ?? session.Workspace.FormIdText(cellKey)}");
    }

    private int _doorTest;
    private int _doorTestFrames;
    private string? DoorQuestionText;
    private string? _doorTestFrom;

    /// <summary>
    /// --autotest-door (with --cell, an interior): go out through a teleport door, then back in
    /// through the door we arrived at. Logs each scene and the selected door. True when done.
    /// </summary>
    public bool DoorTestStep(EditorApp app)
    {
        if (!IsSettled || _pendingDoor is not null) return false;
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
                if (Environment.GetCommandLineArgs().Contains("ask"))
                {
                    // As a double-click does: the question first, answered with Enter.
                    app.Settings.ConfirmDoorTravel = true;
                    AskGoThroughDoor(app, door);
                    _doorTest = 5;
                    return false;
                }
                GoToLinkedDoor(app, door);
                _doorTest = 1;
                return false;
            case 5 when ++_doorTestFrames > 5:
                Log.Info($"autotest-door: question open: {ImGui.IsPopupOpen(DoorQuestionTitle)}, asks: {DoorQuestionText}");
                StbEditor.App.Ui.ImGuiLayer.TestKeys.Enqueue(ImGuiKey.Enter);
                _doorTest = 6;
                return false;
            case 5:
                return false;
            case 6 when _scene!.Title != _doorTestFrom:
                _doorTest = 1;
                return false;
            case 6:
                return false;
            case 1:
                // Nothing is selected on arrival: the door we came out of is the teleport door next to the camera.
                selected = _scene!.Objects.Where(o => TeleportOf(app, o) is not null).MinBy(o => Vector3.Distance(o.Position, _camera.Position));
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
