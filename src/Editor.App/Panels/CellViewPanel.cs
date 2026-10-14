using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data.Catalog;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// The CK Cell View: pick a worldspace (or Interiors), then a cell; on the right the objects of the
/// cell loaded in the Render Window (click: select and frame it).
/// </summary>
internal sealed class CellViewPanel : Panel
{
    private readonly record struct WorldItem(FormKey? Key, string Label, int Count);

    private List<WorldItem> _worlds = [];
    private int _worldIndex;
    private string _filter = string.Empty;
    private List<CatalogRecord> _rows = [];
    private bool _dirty = true;
    private int _draftCount = -1;
    private List<WorldItem> _baseWorlds = [];

    public override string Name => "Cell View";

    public override void OnSessionChanged(EditorApp app)
    {
        var catalog = app.Session!.Catalog;
        var cells = catalog.BySignature.GetValueOrDefault("CELL") ?? [];

        var interiorCount = cells.Count(c => c.Winner.Cell?.IsInterior == true);
        var byWorld = cells
            .Where(c => c.Winner.Cell is { IsInterior: false, Worldspace: not null })
            .GroupBy(c => c.Winner.Cell!.Worldspace!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        _worlds = [new WorldItem(null, T("Interiors"), interiorCount)];
        foreach (var world in (catalog.BySignature.GetValueOrDefault("WRLD") ?? []).OrderBy(w => w.EditorId))
        {
            var count = byWorld.GetValueOrDefault(world.FormKey);
            _worlds.Add(new WorldItem(world.FormKey, world.EditorId ?? world.FormKey.ToString(), count));
        }

        // Tamriel first when present: it is where most work happens.
        var tamriel = _worlds.FindIndex(w => w.Label == "Tamriel");
        _worldIndex = tamriel >= 0 ? tamriel : 0;
        _baseWorlds = [.. _worlds];
        _draftCount = -1;
        _dirty = true;
        _byGrid = cells.Where(c => c.Winner.Cell is { IsInterior: false, IsPersistentCell: false, Worldspace: not null })
            .GroupBy(c => (c.Winner.Cell!.Worldspace!.Value, (int)c.Winner.Cell.GridX, (int)c.Winner.Cell.GridY))
            .ToDictionary(g => g.Key, g => g.First());
        _found = [];
        _foundFor = default;
    }

    /// <summary>Exterior cells by worldspace and grid (where the camera is).</summary>
    private Dictionary<(FormKey, int, int), CatalogRecord> _byGrid = [];
    private bool _follow = true;
    private bool _scrollToCamera;
    private (FormKey, int, int)? _lastCameraCell;
    private int _mode;

    /// <summary>Worldspaces created in this session join the list (marked new); their cells show above the rest.</summary>
    private void SyncDrafts(EditorApp app)
    {
        var drafts = app.Document.Drafts;
        if (drafts.Count == _draftCount) return;
        _draftCount = drafts.Count;

        var selected = _worlds.Count > 0 ? _worlds[_worldIndex].Key : null;
        _worlds = [.. _baseWorlds];
        foreach (var world in drafts.Values.OfType<WorldDraft>().OrderBy(w => w.EditorId))
            _worlds.Add(new WorldItem(world.Key, world.EditorId + "  (new)", app.Document.CellDrafts(world.Key).Count()));
        var index = _worlds.FindIndex(w => w.Key == selected);
        _worldIndex = index >= 0 ? index : 0;

        // A worldspace just created: show it.
        if (drafts.Values.OfType<WorldDraft>().LastOrDefault() is { } last && app.RenderWindow.Scene?.Worldspace == last.Key)
            _worldIndex = Math.Max(0, _worlds.FindIndex(w => w.Key == last.Key));
        _dirty = true;
    }

    protected override void DrawContent(EditorApp app)
    {
        if (!RequireSession(app)) return;
        SyncDrafts(app);

        // Cells on the left, the loaded cell's objects on the right (drag the border to resize).
        var avail = ImGui.GetContentRegionAvail();
        if (_split <= 0) _split = avail.X * 0.55f;
        if (ImGui.BeginChild("##cellList", new Vector2(_split, 0), ImGuiChildFlags.ResizeX))
            DrawCells(app);
        _split = ImGui.GetItemRectSize().X;
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("##cellObjects", Vector2.Zero))
            DrawObjects(app);
        ImGui.EndChild();
    }

    private float _split;
    private string _objectFilter = string.Empty;
    private List<StbEditor.Render.SceneObject> _objects = [];
    private (StbEditor.Render.Scene? Scene, FormKey? Cell, string Filter, int Count, int Version) _objectsKey;

    /// <summary>
    /// References of the loaded cell: an interior's all, for an exterior those standing in the
    /// chosen cell (the one selected on the left if it is loaded, else the centre cell).
    /// </summary>
    private void DrawObjects(EditorApp app)
    {
        var window = app.RenderWindow;
        var scene = window.Scene;
        var catalog = app.Session!.Catalog;
        if (scene is null)
        {
            ImGui.TextColored(Theme.TextDim, T("Double-click a cell to load it; its objects show here."));
            return;
        }

        var cellKey = window.LoadedCell;
        if (!scene.IsInterior && app.Selected is { } picked && catalog.Get(picked)?.Winner.Cell is { IsInterior: false, IsPersistentCell: false } p
            && p.Worldspace == scene.Worldspace && Math.Abs(p.GridX - scene.CenterCell.X) <= 5 && Math.Abs(p.GridY - scene.CenterCell.Y) <= 5)
            cellKey = picked;
        var cellRecord = cellKey is { } ck ? catalog.Get(ck) : null;
        var grid = cellRecord?.Winner.Cell is { IsInterior: false } g ? (g.GridX, g.GridY) : scene.CenterCell;

        var key = (scene, cellKey, _objectFilter, scene.Objects.Count, app.Document.History.Version);
        if (key != _objectsKey)
        {
            _objectsKey = key;
            var filter = _objectFilter.Trim();
            _objects = scene.Objects
                .Where(o => !o.IsDeleted && o.CollectionPart <= 0)
                .Where(o => scene.IsInterior || ((int)MathF.Floor(o.Position.X / StbEditor.Render.TerrainCell.CellSize) == grid.Item1
                                                 && (int)MathF.Floor(o.Position.Y / StbEditor.Render.TerrainCell.CellSize) == grid.Item2))
                .Where(o => filter.Length == 0 || (o.BaseEditorId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                            || o.BaseSignature.Contains(filter, StringComparison.OrdinalIgnoreCase)
                            || app.Session.Workspace.FormIdText(o.Reference).Contains(filter, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(o => o.Reference)
                .OrderBy(o => o.BaseEditorId ?? "~", StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var title = scene.IsInterior ? cellRecord?.EditorId ?? scene.Title : $"{cellRecord?.EditorId ?? "(wilderness)"} [{grid.Item1}, {grid.Item2}]";
        ImGui.TextColored(Theme.TextDim, TF($"{title}: {_objects.Count:N0} objects"));
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##objectfilter", T("Filter objects (EditorID, type, FormID)"), ref _objectFilter, 256);

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV
            | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Hideable;
        if (!ImGui.BeginTable("##objects", 4, flags)) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("Base"), ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn(T("Type"));
        ImGui.TableSetupColumn(T("Ref ID"));
        ImGui.TableSetupColumn(T("Plugin"), ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableHeadersRow();

        var ws = app.Session.Workspace;
        var clipper = new ImGuiListClipper();
        clipper.Begin(_objects.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var obj = _objects[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.PushID(i);
                var label = obj.BaseEditorId ?? ws.FormIdText(obj.Base);
                if (obj.IsModified) label += "  *";
                if (ImGui.Selectable(label, window.IsSelected(obj), ImGuiSelectableFlags.SpanAllColumns)) app.Select(obj.Reference);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(obj.ModelPath ?? T("no model"));
                ImGui.PopID();
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, obj.BaseSignature);
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, ws.FormIdText(obj.Reference));
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, obj.SourcePlugin ?? "new");
            }
        }
        clipper.End();
        ImGui.EndTable();
    }

    private void DrawCells(EditorApp app)
    {
        var ws = app.Session!.Workspace;

        // Where the camera is: the exterior cell under it, highlighted in the list (and followed, if asked).
        var camera = app.RenderWindow.CameraCell;
        CatalogRecord? cameraCell = null;
        if (camera is { } c)
        {
            cameraCell = _byGrid.GetValueOrDefault((c.World, c.X, c.Y));
            var label = cameraCell?.EditorId ?? T("(wilderness)");
            ImGui.TextColored(Theme.Accent, TF($"Camera: {label} [{c.X}, {c.Y}]"));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("The exterior cell the camera is over now (the Render Window loads the cells around it)"));
            ImGui.SameLine();
            if (ImGui.SmallButton(T("Show in the list"))) ShowCameraCell(c.World);
            ImGui.SameLine();
            ImGui.Checkbox(T("Follow") + "##followcamera", ref _follow);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Scroll the list to the camera's cell whenever it changes"));
            if (_follow && _lastCameraCell != (c.World, c.X, c.Y) && _worlds.Count > 0 && _worlds[_worldIndex].Key == c.World) _scrollToCamera = true;
            _lastCameraCell = (c.World, c.X, c.Y);
        }
        else if (app.RenderWindow.Scene is { IsInterior: true } && app.RenderWindow.LoadedCell is { } inside)
        {
            ImGui.TextColored(Theme.Accent, TF($"Loaded: {app.Session.Catalog.Get(inside)?.EditorId ?? ws.FormIdText(inside)}"));
        }
        if (ImGui.RadioButton(T("Cells"), _mode == 0)) _mode = 0;
        ImGui.SameLine();
        if (ImGui.RadioButton(T("Find placed objects"), _mode == 1)) _mode = 1;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Every placed copy of a base object in the chosen worldspace (or in the interiors), whatever cell it is in"));

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.45f);
        var current = _worlds.Count > 0 ? _worlds[_worldIndex] : default;
        if (ImGui.BeginCombo("##world", $"{current.Label} ({current.Count:N0})", ImGuiComboFlags.HeightLarge))
        {
            for (var i = 0; i < _worlds.Count; i++)
            {
                if (ImGui.Selectable($"{_worlds[i].Label}  ({_worlds[i].Count:N0})##{i}", i == _worldIndex))
                {
                    _worldIndex = i;
                    _dirty = true;
                }
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        if (_mode == 1)
        {
            ImGui.InputTextWithHint("##findplaced", T("Base EditorID, name or FormID"), ref _find, 256);
            DrawFound(app, current.Key);
            return;
        }
        if (ImGui.InputTextWithHint("##cellfilter", T("Filter cells"), ref _filter, 256)) _dirty = true;

        if (_dirty) Rebuild(app);

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV
            | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Hideable;
        if (!ImGui.BeginTable("##cells", 5, flags)) return;

        var interiors = current.Key is null;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("Editor ID"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn(T("Name"), ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(T("Grid"), interiors ? ImGuiTableColumnFlags.DefaultHide : ImGuiTableColumnFlags.None);
        ImGui.TableSetupColumn(T("Form ID"));
        ImGui.TableSetupColumn(T("Last change"), ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableHeadersRow();

        // Cells created in this session (not saved yet).
        foreach (var draft in app.Document.CellDrafts(current.Key).OrderBy(d => d.EditorId))
        {
            if (_filter.Length > 0 && !draft.EditorId.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.PushID(draft.Key.GetHashCode());
            if (ImGui.Selectable(draft.EditorId, app.Selected == draft.Key, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
            {
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) app.RenderWindow.OpenCell(app, draft.Key);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("New in this session — saved with Ctrl+S. Double-click to load."));
            ImGui.PopID();
            ImGui.TableNextColumn();
            ImGui.Text(draft.Cell.Name?.String ?? string.Empty);
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.TextDim, draft.Cell.Grid is { } g ? $"{g.Point.X}, {g.Point.Y}" : "—");
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.TextDim, ws.FormIdText(draft.Key));
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.Warning, "new");
        }

        if (_scrollToCamera && cameraCell is not null)
        {
            _scrollToCamera = false;
            var at = _rows.IndexOf(cameraCell);
            var row = ImGui.GetTextLineHeight() + ImGui.GetStyle().CellPadding.Y * 2;
            if (at >= 0) ImGui.SetScrollY(Math.Max(0, at * row - ImGui.GetWindowHeight() / 2));
        }
        var clipper = new ImGuiListClipper();
        clipper.Begin(_rows.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var cell = _rows[i];
                var placement = cell.Winner.Cell!;
                ImGui.TableNextRow();
                if (ReferenceEquals(cell, cameraCell)) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(Theme.Alpha(Theme.Accent, 0.28f)));
                ImGui.TableNextColumn();

                ImGui.PushID(i);
                var label = placement.IsPersistentCell ? "<persistent>" : cell.EditorId ?? T("(wilderness)");
                if (ImGui.Selectable(label, app.Selected == cell.FormKey, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
                {
                    app.Select(cell.FormKey);
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !placement.IsPersistentCell) app.RenderWindow.OpenCell(app, cell.FormKey);
                }
                if (ImGui.IsItemHovered() && !placement.IsPersistentCell) ImGui.SetTooltip(T("Double-click to load in the Render Window; right-click: properties"));
                if (!placement.IsPersistentCell && ImGui.BeginPopupContextItem("##cellmenu"))
                {
                    if (ImGui.MenuItem(T("Cell properties..."))) app.PlaceProperties.Open(cell.FormKey);
                    if (!placement.IsInterior && placement.Worldspace is { } world && ImGui.MenuItem(T("Worldspace properties..."))) app.PlaceProperties.Open(world);
                    if (ImGui.MenuItem(T("Load in the Render Window"))) app.RenderWindow.OpenCell(app, cell.FormKey);
                    ImGui.EndPopup();
                }
                ImGui.PopID();

                ImGui.TableNextColumn();
                ImGui.Text(cell.Name ?? string.Empty);
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, placement.IsPersistentCell ? "—" : $"{placement.GridX}, {placement.GridY}");
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, ws.FormIdText(cell.FormKey));
                ImGui.TableNextColumn();
                ImGui.TextColored(cell.IsOverridden ? Theme.Override : Theme.TextDim, cell.WinningPlugin.Name);
            }
        }
        clipper.End();
        ImGui.EndTable();
    }

    /// <summary>Switches the list to the camera's worldspace, clears the filter and scrolls to its cell.</summary>
    private void ShowCameraCell(FormKey world)
    {
        var index = _worlds.FindIndex(w => w.Key == world);
        if (index >= 0 && index != _worldIndex) { _worldIndex = index; _dirty = true; }
        if (_filter.Length > 0) { _filter = string.Empty; _dirty = true; }
        _mode = 0;
        _scrollToCamera = true;
    }

    private string _find = string.Empty;
    private readonly record struct Placed(FormKey Reference, string BaseLabel, string Signature, FormKey Cell, string CellLabel);
    private List<Placed> _found = [];
    private (string, FormKey?) _foundFor;
    private int _foundBases;
    private const int MaxBases = 300;
    private const int MaxPlaced = 5000;

    /// <summary>
    /// «Find placed objects»: base records whose EditorID, name or FormID matches, and every reference to them standing in the
    /// chosen worldspace (its grid cells and its persistent cell) or, for «Interiors», in interior cells — from the Use Info index.
    /// Double-click goes there (loads the cell and selects the reference).
    /// </summary>
    private void DrawFound(EditorApp app, FormKey? world)
    {
        var session = app.Session!;
        var text = _find.Trim();
        if (text.Length < 2) { ImGui.TextColored(Theme.TextDim, T("Type at least two letters of the base object's EditorID or name (or its FormID).")); return; }
        var uses = session.Uses;
        if (!uses.IsCompleted) { ImGui.TextColored(Theme.Warning, T("building the Use Info index…")); return; }
        if (!uses.IsCompletedSuccessfully) { ImGui.TextColored(Theme.Error, uses.Exception?.GetBaseException().Message ?? "?"); return; }
        if (_foundFor != (text, world)) Search(session, uses.Result, text, world);
        DrawFoundTable(app, session);
    }

    private void Search(StbEditor.App.Services.EditorSession session, StbEditor.Data.UseIndex uses, string text, FormKey? world)
    {
        {
            _foundFor = (text, world);
            var catalog = session.Catalog;
            var bases = catalog.ByKey.Values.Where(r => RecordTypes.IsPlaceable(r.Signature)
                    && ((r.EditorId?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) || (r.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                        || session.Workspace.FormIdText(r.FormKey).Equals(text, StringComparison.OrdinalIgnoreCase)))
                .Take(MaxBases + 1).ToList();
            _foundBases = bases.Count;
            _found = [];
            foreach (var b in bases.Take(MaxBases))
            {
                foreach (var use in uses.UsesOf(b.FormKey))
                {
                    if (!use.IsPlaced || use.Parent is not { } cell || catalog.Get(cell) is not { Winner.Cell: { } placement } cellRecord) continue;
                    if (world is null ? !placement.IsInterior : placement.IsInterior || placement.Worldspace != world) continue;
                    var cellLabel = placement.IsInterior ? cellRecord.EditorId ?? session.Workspace.FormIdText(cell)
                        : placement.IsPersistentCell ? "<persistent>" : $"{cellRecord.EditorId ?? T("(wilderness)")} [{placement.GridX}, {placement.GridY}]";
                    _found.Add(new Placed(use.Source, b.EditorId ?? session.Workspace.FormIdText(b.FormKey), b.Signature, cell, cellLabel));
                }
                if (_found.Count >= MaxPlaced) break;
            }
            _found = _found.DistinctBy(f => f.Reference).OrderBy(f => f.BaseLabel, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.CellLabel).ToList();
        }
    }

    private void DrawFoundTable(EditorApp app, StbEditor.App.Services.EditorSession session)
    {
        ImGui.TextColored(Theme.TextDim, TF($"{_found.Count:N0} placed, of {Math.Min(_foundBases, MaxBases)} matching base object(s)")
            + (_foundBases > MaxBases ? "  " + TF($"(only the first {MaxBases} bases: type more)") : "") + "   " + T("Double-click: go there"));
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable("##found", 4, flags)) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("Base"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn(T("Type"));
        ImGui.TableSetupColumn(T("Ref ID"));
        ImGui.TableSetupColumn(T("Cell"), ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableHeadersRow();
        var clipper = new ImGuiListClipper();
        clipper.Begin(_found.Count);
        while (clipper.Step())
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var f = _found[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.PushID(i);
                if (ImGui.Selectable(f.BaseLabel, false, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick)
                    && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    app.RenderWindow.GoToReference(app, f.Reference, f.Cell);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Double-click: go there"));
                ImGui.PopID();
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, f.Signature);
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, session.Workspace.FormIdText(f.Reference));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(f.CellLabel);
            }
        clipper.End();
        ImGui.EndTable();
    }

    /// <summary>Autotests: the «Find placed objects» rows for a text in a worldspace (null: interiors), once the Use Info index is built.</summary>
    public string FindForTest(EditorApp app, string text, FormKey? world)
    {
        _find = text;
        _mode = 1;
        var index = _worlds.FindIndex(w => w.Key == world);
        if (index >= 0) _worldIndex = index;
        Search(app.Session!, app.Session!.Uses.GetAwaiter().GetResult(), text, world);
        return $"{_found.Count} placed of {_foundBases} base(s): " + string.Join(", ", _found.Take(5).Select(f => $"{f.BaseLabel} {app.Session.Workspace.FormIdText(f.Reference)} in {f.CellLabel}"));
    }

    private void Rebuild(EditorApp app)
    {
        _dirty = false;
        var catalog = app.Session!.Catalog;
        var world = _worlds.Count > 0 ? _worlds[_worldIndex].Key : null;
        var filter = _filter.Trim();

        _rows = (catalog.BySignature.GetValueOrDefault("CELL") ?? [])
            .Where(c => c.Winner.Cell is { } p && (world is null ? p.IsInterior : !p.IsInterior && p.Worldspace == world))
            .Where(c => filter.Length == 0
                || (c.EditorId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                || (c.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                || $"{c.Winner.Cell!.GridX}, {c.Winner.Cell.GridY}".Contains(filter))
            .OrderBy(c => c.Winner.Cell!.IsPersistentCell ? 0 : 1)
            .ThenBy(c => c.EditorId is null ? 1 : 0)
            .ThenBy(c => c.EditorId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Winner.Cell!.GridX)
            .ThenBy(c => c.Winner.Cell!.GridY)
            .ToList();
    }
}
