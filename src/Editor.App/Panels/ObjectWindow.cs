using System.Numerics;
using Hexa.NET.ImGui;
using StbEditor.App.Ui;
using StbEditor.Data.Catalog;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// The CK Object Window: record types by category on the left, a virtualised record table on the
/// right. The filter matches EditorID, name or FormID across one type or across everything.
/// </summary>
internal sealed class ObjectWindow : Panel
{
    private const string AllTypes = "*";

    private string _type = "STAT";
    private string _filter = string.Empty;
    private bool _focusSearch;
    private bool _onlyOverrides;
    private List<CatalogRecord> _rows = [];
    private bool _dirty = true;
    private int _catalogVersion = -1;
    private int _sortColumn;
    private bool _sortAscending = true;
    private int _scrolledToSelection = -1;
    private Dictionary<string, List<RecordTypeInfo>> _categories = new();
    private bool _tiles = true;

    public override string Name => "Object Window";

    public void SetFilter(string text)
    {
        _filter = text;
        _type = AllTypes;
        _dirty = true;
    }

    /// <summary>Shows one record type (e.g. "STAT"), keeping the filter.</summary>
    public void SetType(string signature)
    {
        _type = signature;
        _dirty = true;
    }

    public void FocusSearch()
    {
        _focusSearch = true;
        ImGui.SetWindowFocus(Name);
    }

    public override void OnSessionChanged(EditorApp app)
    {
        var catalog = app.Session!.Catalog;
        _categories = catalog.BySignature.Keys
            .Select(RecordTypes.Get)
            .Where(t => t.Signature != "CELL")
            .GroupBy(t => t.Category)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Name).ToList());
        _dirty = true;
    }

    protected override void DrawContent(EditorApp app)
    {
        if (!RequireSession(app)) return;
        var session = app.Session!;

        var leftWidth = 170 * ImGui.GetFontSize() / 16f;
        ImGui.BeginChild("##types", new Vector2(leftWidth, 0), ImGuiChildFlags.ResizeX | ImGuiChildFlags.Borders);
        DrawTypeTree(session.Catalog);
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginChild("##records", Vector2.Zero);

        if (_focusSearch)
        {
            ImGui.SetKeyboardFocusHere();
            _focusSearch = false;
        }

        ImGui.SetNextItemWidth(-ImGui.GetFontSize() * 9);
        if (ImGui.InputTextWithHint("##filter", T("Filter: EditorID, name or FormID   (Ctrl+F)"), ref _filter, 256)) _dirty = true;
        ImGui.SameLine();
        if (ImGui.Checkbox(T("Overrides"), ref _onlyOverrides)) _dirty = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Only records that more than one plugin defines"));

        if (app.Session!.Catalog.Version != _catalogVersion)
        {
            _catalogVersion = app.Session.Catalog.Version;
            _dirty = true;
        }
        if (_dirty) Rebuild(app);

        ImGui.SameLine();
        ImGui.TextColored(Theme.TextDim, $"{_rows.Count:N0}");
        ImGui.SameLine();
        if (ImGui.SmallButton(_tiles ? T("List") : T("Tiles"))) _tiles ^= true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Thumbnails of placeable objects, or the record table"));

        var tileable = _type != AllTypes && RecordTypes.IsPlaceable(_type);
        if (_tiles && tileable && app.Thumbnails is not null) DrawTiles(app);
        else DrawTable(app);
        ImGui.EndChild();
    }

    private void DrawTypeTree(RecordCatalog catalog)
    {
        if (ImGui.Selectable(TF($"All types ({catalog.ByKey.Count:N0})"), _type == AllTypes))
        {
            _type = AllTypes;
            _dirty = true;
        }

        foreach (var category in RecordTypes.CategoryOrder)
        {
            if (!_categories.TryGetValue(category, out var types)) continue;

            var defaultOpen = category is "World Objects" ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
            if (!ImGui.TreeNodeEx(T(category) + "##" + category, defaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth)) continue;

            foreach (var type in types)
            {
                var count = catalog.BySignature.TryGetValue(type.Signature, out var list) ? list.Count : 0;
                if (ImGui.Selectable($"{T(type.Name)}  ##{type.Signature}", _type == type.Signature))
                {
                    _type = type.Signature;
                    _dirty = true;
                }
                ImGui.SameLine();
                ImGui.TextColored(Theme.TextDim, count.ToString("N0"));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(type.Signature);
            }

            ImGui.TreePop();
        }
    }

    private void Rebuild(EditorApp app)
    {
        _dirty = false;
        var session = app.Session!;
        var catalog = session.Catalog;

        IEnumerable<CatalogRecord> source = _type == AllTypes
            ? catalog.ByKey.Values.Where(r => r.Signature != "CELL")
            : catalog.BySignature.GetValueOrDefault(_type) ?? [];

        if (_onlyOverrides) source = source.Where(r => r.IsOverridden);

        var filter = _filter.Trim();
        if (filter.Length > 0)
        {
            var formKey = session.Workspace.ParseFormReference(filter);
            source = source.Where(r =>
                (formKey is { } k && r.FormKey == k)
                || (r.EditorId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        _rows = source.ToList();
        Sort(app);
    }

    private void Sort(EditorApp app)
    {
        var ws = app.Session!.Workspace;
        Comparison<CatalogRecord> compare = _sortColumn switch
        {
            1 => (a, b) => (ws.ToFormId(a.FormKey) ?? 0).CompareTo(ws.ToFormId(b.FormKey) ?? 0),
            2 => (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
            3 => (a, b) => string.CompareOrdinal(a.Signature, b.Signature),
            4 => (a, b) => a.Plugins.Count.CompareTo(b.Plugins.Count),
            5 => (a, b) => a.WinningPlugin.LoadIndex.CompareTo(b.WinningPlugin.LoadIndex),
            _ => (a, b) => string.Compare(a.EditorId, b.EditorId, StringComparison.OrdinalIgnoreCase),
        };

        _rows.Sort(_sortAscending ? compare : (a, b) => compare(b, a));
    }

    /// <summary>Grid of rendered thumbnails; click selects, drag places into the Render Window.</summary>
    private unsafe void DrawTiles(EditorApp app)
    {
        var thumbs = app.Thumbnails!;
        var scale = ImGui.GetFontSize() / 16f;
        var tile = MathF.Round(118 * scale);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var labelHeight = ImGui.GetTextLineHeight();

        if (!ImGui.BeginChild("##tiles", Vector2.Zero)) { ImGui.EndChild(); return; }

        var avail = ImGui.GetContentRegionAvail().X;
        var columns = Math.Max(1, (int)((avail + spacing) / (tile + spacing)));
        var rows = (_rows.Count + columns - 1) / columns;

        var clipper = new ImGuiListClipper();
        clipper.Begin(rows, tile + labelHeight + ImGui.GetStyle().ItemSpacing.Y * 2);
        while (clipper.Step())
        {
            for (var row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
            {
                for (var c = 0; c < columns; c++)
                {
                    var i = row * columns + c;
                    if (i >= _rows.Count) break;
                    var record = _rows[i];
                    if (c > 0) ImGui.SameLine();

                    ImGui.BeginGroup();
                    ImGui.PushID(i);
                    var start = ImGui.GetCursorScreenPos();
                    var texture = thumbs.Get(record.FormKey);
                    bool clicked;
                    if (texture != 0)
                        clicked = ImGui.ImageButton("##t", new ImTextureRef(null, new ImTextureID((ulong)texture)), new Vector2(tile - 4, tile - 4));
                    else
                        clicked = ImGui.Button("…##t", new Vector2(tile, tile));

                    if (clicked) app.Select(record.FormKey);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{record.EditorId}\n{record.Name}\n{app.Session!.Workspace.FormIdText(record.FormKey)} · {record.WinningPlugin.Name}");
                    if (ImGui.BeginDragDropSource())
                    {
                        FormDrag.Current = record.FormKey;
                        ImGui.SetDragDropPayload(FormDrag.PayloadType, null, 0);
                        ImGui.Text(TF($"Place {record.EditorId}"));
                        ImGui.EndDragDropSource();
                    }

                    if (app.Selected == record.FormKey)
                        ImGui.GetWindowDrawList().AddRect(start, start + new Vector2(tile, tile), 0xE0FFFFFF, 0, 0, 2f);

                    var label = record.EditorId ?? "?";
                    while (label.Length > 3 && ImGui.CalcTextSize(label).X > tile) label = label[..^2];
                    if (label != record.EditorId) label += "…";
                    ImGui.TextColored(app.Selected == record.FormKey ? Theme.Accent : Theme.TextDim, label);
                    ImGui.PopID();
                    ImGui.EndGroup();
                }
            }
        }
        clipper.End();
        ImGui.EndChild();
    }

    private unsafe void DrawTable(EditorApp app)
    {
        var ws = app.Session!.Workspace;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable
            | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Hideable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.SizingFixedFit;

        if (!ImGui.BeginTable("##recordTable", 6, flags)) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        var em = ImGui.GetFontSize();
        ImGui.TableSetupColumn(T("Editor ID"), ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort, 2.0f);
        ImGui.TableSetupColumn(T("Form ID"), ImGuiTableColumnFlags.WidthFixed, em * 4.6f);
        ImGui.TableSetupColumn(T("Name"), ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed | (_type == AllTypes ? ImGuiTableColumnFlags.None : ImGuiTableColumnFlags.DefaultHide), em * 2.8f);
        ImGui.TableSetupColumn(T("Count"), ImGuiTableColumnFlags.WidthFixed, em * 2.4f);
        ImGui.TableSetupColumn(T("Last change"), ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultHide, 1.0f);
        ImGui.TableHeadersRow();

        var specs = ImGui.TableGetSortSpecs();
        if (specs.Handle != null && specs.SpecsDirty)
        {
            if (specs.SpecsCount > 0)
            {
                var spec = specs.Specs[0];
                _sortColumn = spec.ColumnIndex;
                _sortAscending = spec.SortDirection == ImGuiSortDirection.Ascending;
                Sort(app);
            }
            specs.SpecsDirty = false;
        }

        // Scroll the selection into view once when it changes from elsewhere (search, links).
        var scrollTo = -1;
        if (_scrolledToSelection != app.SelectionVersion && app.Selected is { } selectedKey)
        {
            _scrolledToSelection = app.SelectionVersion;
            scrollTo = _rows.FindIndex(r => r.FormKey == selectedKey);
        }

        var clipper = new ImGuiListClipper();
        clipper.Begin(_rows.Count);
        if (scrollTo >= 0) clipper.IncludeItemByIndex(scrollTo);

        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var record = _rows[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                var selected = app.Selected == record.FormKey;
                ImGui.PushID(i);
                if (ImGui.Selectable(record.EditorId ?? T("<no editor id>"), selected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
                {
                    app.Select(record.FormKey);
                    _scrolledToSelection = app.SelectionVersion;
                }

                if (i == scrollTo) ImGui.SetScrollHereY(0.4f);

                if (ImGui.BeginPopupContextItem("##ctx"))
                {
                    if (ImGui.MenuItem(T("Copy Form ID"))) ImGui.SetClipboardText(ws.FormIdText(record.FormKey));
                    if (ImGui.MenuItem(T("Copy Editor ID"), (string?)null, false, record.EditorId is not null)) ImGui.SetClipboardText(record.EditorId!);
                    if (ImGui.MenuItem(T("Copy FormKey"))) ImGui.SetClipboardText(record.FormKey.ToString());
                    ImGui.EndPopup();
                }

                if (ImGui.BeginDragDropSource())
                {
                    // The render window reads the form from FormDrag; the payload only marks the drag.
                    FormDrag.Current = record.FormKey;
                    ImGui.SetDragDropPayload(FormDrag.PayloadType, null, 0);
                    ImGui.Text(record.EditorId ?? ws.FormIdText(record.FormKey));
                    ImGui.EndDragDropSource();
                }
                ImGui.PopID();

                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, ws.FormIdText(record.FormKey));

                ImGui.TableNextColumn();
                ImGui.Text(record.Name ?? string.Empty);

                ImGui.TableNextColumn();
                ImGui.Text(record.Signature);

                ImGui.TableNextColumn();
                if (record.IsOverridden) ImGui.TextColored(Theme.Override, record.Plugins.Count.ToString());
                else ImGui.TextColored(Theme.TextDim, "1");

                ImGui.TableNextColumn();
                ImGui.TextColored(record.IsOverridden ? Theme.Override : Theme.TextDim, record.WinningPlugin.Name);
            }
        }

        clipper.End();
        ImGui.EndTable();
    }
}
