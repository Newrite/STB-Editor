using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data.Catalog;

namespace StbEditor.App.Panels;

/// <summary>The CK Cell View: pick a worldspace (or Interiors), then a cell.</summary>
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

        _worlds = [new WorldItem(null, "Interiors", interiorCount)];
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
    }

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
        var ws = app.Session!.Workspace;
        SyncDrafts(app);

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
        if (ImGui.InputTextWithHint("##cellfilter", "Filter cells", ref _filter, 256)) _dirty = true;

        if (_dirty) Rebuild(app);

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV
            | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Hideable;
        if (!ImGui.BeginTable("##cells", 5, flags)) return;

        var interiors = current.Key is null;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Editor ID", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Grid", interiors ? ImGuiTableColumnFlags.DefaultHide : ImGuiTableColumnFlags.None);
        ImGui.TableSetupColumn("Form ID");
        ImGui.TableSetupColumn("Last change", ImGuiTableColumnFlags.WidthStretch, 0.8f);
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
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("New in this session — saved with Ctrl+S. Double-click to load.");
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

        var clipper = new ImGuiListClipper();
        clipper.Begin(_rows.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var cell = _rows[i];
                var placement = cell.Winner.Cell!;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                ImGui.PushID(i);
                var label = placement.IsPersistentCell ? "<persistent>" : cell.EditorId ?? "(wilderness)";
                if (ImGui.Selectable(label, app.Selected == cell.FormKey, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
                {
                    app.Select(cell.FormKey);
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !placement.IsPersistentCell) app.RenderWindow.OpenCell(app, cell.FormKey);
                }
                if (ImGui.IsItemHovered() && !placement.IsPersistentCell) ImGui.SetTooltip("Double-click to load in the Render Window");
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
