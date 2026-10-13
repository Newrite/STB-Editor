using System.Numerics;
using Hexa.NET.ImGui;
using StbEditor.App.Ui;
using StbEditor.Core;
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
    private enum Show { All, Overridden, Conflicts }

    private Show _show;

    /// <summary>Only records this plugin defines or overrides (null = any).</summary>
    private string? _plugin;
    private string _pluginFilter = string.Empty;
    private readonly ConflictScan _scan = new();

    /// <summary>ModGroups changed: conflicts are found again.</summary>
    public void ResetConflicts() => _scan.Reset();
    private int _scanVersion = -1;

    /// <summary>The conflict filter is still checking records (keeps frames coming).</summary>
    public bool IsScanning => _show == Show.Conflicts && _scan.IsRunning;

    /// <summary>Filters as the toolbar sets them (autotests).</summary>
    public void SetFiltersForTest(string? plugin, bool conflicts)
    {
        _plugin = plugin;
        _show = conflicts ? Show.Conflicts : Show.All;
        _dirty = true;
    }

    public int RowCount => _rows.Count;
    public IEnumerable<Mutagen.Bethesda.Plugins.FormKey> VisibleKeys => _rows.Select(r => r.FormKey);
    public int ConflictsChecked => _scan.Checked;
    private List<CatalogRecord> _rows = [];
    private bool _dirty = true;
    private int _catalogVersion = -1;
    private int _sortColumn;
    private bool _sortAscending = true;
    private int _scrolledToSelection = -1;
    private Dictionary<string, List<RecordTypeInfo>> _categories = new();
    private bool _tiles = true;

    /// <summary>Records picked with Ctrl/Shift+click (for Compare); the anchor is where a Shift range starts.</summary>
    private readonly HashSet<Mutagen.Bethesda.Plugins.FormKey> _picked = [];
    private int _anchor = -1;

    /// <summary>The picked records in list order (autotests use it as well).</summary>
    public List<Mutagen.Bethesda.Plugins.FormKey> Picked => _rows.Where(r => _picked.Contains(r.FormKey)).Select(r => r.FormKey).ToList();

    /// <summary>Picks records as Ctrl+click would (autotests).</summary>
    public void Pick(IEnumerable<Mutagen.Bethesda.Plugins.FormKey> keys)
    {
        _picked.Clear();
        _picked.UnionWith(keys);
    }

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
        _scan.Reset();
        if (_plugin is not null && !app.Session.Workspace.Active.Any(p => p.Name.Equals(_plugin, StringComparison.OrdinalIgnoreCase))) _plugin = null;
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

        var em = ImGui.GetFontSize();
        ImGui.SetNextItemWidth(-em * 4.5f);
        if (ImGui.InputTextWithHint("##filter", T("Filter: EditorID, name or FormID   (Ctrl+F)"), ref _filter, 256)) _dirty = true;
        ImGui.SameLine();
        if (ImGui.SmallButton(_tiles ? T("List") : T("Tiles"))) _tiles ^= true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Thumbnails of placeable objects, or the record table"));

        // Second row: which records (all / overridden / conflicts), from which plugin, how many, New.
        ImGui.SetNextItemWidth(em * 6.5f);
        string[] shows = [T("All records"), T("Overridden"), T("Conflicts")];
        var show = (int)_show;
        if (ImGui.Combo("##show", ref show, shows, shows.Length)) { _show = (Show)show; _dirty = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(T("Overridden: more than one plugin has the record. Conflicts: some plugin's change is lost to the winner (as xEdit's conflict filter; checked field by field)"));
        ImGui.SameLine();
        DrawPluginFilter(app, em);

        if (app.Session!.Catalog.Version != _catalogVersion)
        {
            _catalogVersion = app.Session.Catalog.Version;
            _dirty = true;
        }
        if (_dirty) Rebuild(app);

        if (_show == Show.Conflicts)
        {
            _scan.Step(app);
            if (_scan.Version != _scanVersion)
            {
                _scanVersion = _scan.Version;
                Rebuild(app);
            }
        }

        ImGui.SameLine();
        ImGui.TextColored(Theme.TextDim, $"{_rows.Count:N0}");
        if (_show == Show.Conflicts && _scan.IsRunning)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.Warning, TF($"checking: {_scan.Checked:N0} done, {_scan.Pending:N0} left"));
        }
        if (SpreadsheetWindow.Has(_type))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(T("Table"))) app.Spreadsheets.Show(_type);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("All records of this type as a sortable table, edited in place"));
        }
        if (_type != AllTypes && NewFormDialog.CanCreate(_type))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(T("+ New"))) app.NewForm.Show(_type);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(TF($"A new {T(RecordTypes.Get(_type).Name)} in the active file (as the CK's New)"));
        }

        // Ctrl+F4 (as in the CK): FaceGen for the selected NPCs.
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows) && !ImGui.GetIO().WantTextInput && ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.F4, false))
        {
            var npcs = (_picked.Count > 1 ? Picked : app.Selected is { } one ? [one] : [])
                .Where(k => app.Session!.Catalog.Get(k)?.Signature == "NPC_").ToList();
            if (npcs.Count > 0) ExportFaceGen(app, npcs);
        }

        // Delete in the record list: take the selected records out of the active file (Ctrl+Z brings them back).
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows) && !ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Delete, false))
        {
            foreach (var key in _picked.Count > 1 ? Picked : app.Selected is { } one ? [one] : [])
                RecordActions.Remove(app, key);
        }

        var tileable = _type != AllTypes && RecordTypes.IsPlaceable(_type);
        if (_tiles && tileable && app.Thumbnails is not null) DrawTiles(app);
        else DrawTable(app);
        ImGui.EndChild();
    }

    /// <summary>Plugin combo: any, the active file, or one plugin of the load order (with a filter box).</summary>
    private void DrawPluginFilter(EditorApp app, float em)
    {
        // Fills the row, leaving room for the count and «+ New».
        var reserve = em * ((_type != AllTypes && NewFormDialog.CanCreate(_type) ? 8.5f : 4f) + (SpreadsheetWindow.Has(_type) ? 3.5f : 0f));
        ImGui.SetNextItemWidth(Math.Max(em * 6, ImGui.GetContentRegionAvail().X - reserve));
        if (!ImGui.BeginCombo("##plugin", _plugin ?? T("Any plugin"), ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Only records that this plugin adds or overrides (xEdit: the plugin's branch)"));
            return;
        }
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##pf", T("Filter plugins"), ref _pluginFilter, 128);
        if (ImGui.Selectable(T("Any plugin"), _plugin is null)) { _plugin = null; _dirty = true; }
        if (app.ActivePlugin is { } active && ImGui.Selectable(TF($"{active.Name}  (active file)"), _plugin == active.Name)) { _plugin = active.Name; _dirty = true; }
        ImGui.Separator();
        foreach (var plugin in app.Session!.Workspace.Active)
        {
            if (plugin.Mod is null) continue;
            if (_pluginFilter.Length > 0 && !plugin.Name.Contains(_pluginFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (ImGui.Selectable($"{plugin.IndexLabel,-6} {plugin.Name}", string.Equals(_plugin, plugin.Name, StringComparison.OrdinalIgnoreCase)))
            {
                _plugin = plugin.Name;
                _dirty = true;
            }
        }
        ImGui.EndCombo();
    }

    /// <summary>FaceGen for these NPCs; the outcome goes to the log and the status line.</summary>
    public static void ExportFaceGen(EditorApp app, IReadOnlyCollection<Mutagen.Bethesda.Plugins.FormKey> npcs)
    {
        var outcome = Services.FaceGenExports.Export(app, npcs);
        foreach (var problem in outcome.Problems) Services.Log.Warn("FaceGen: " + problem);
        if (outcome.Exported > 0) StatusBar.Notice(TF($"FaceGen exported for {outcome.Exported} NPC(s)") + (outcome.Problems.Count > 0 ? TF($", {outcome.Problems.Count} note(s) in the log") : ""));
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

        if (_plugin is { } plugin) source = source.Where(r => r.Plugins.Any(p => p.Name.Equals(plugin, StringComparison.OrdinalIgnoreCase)));
        if (_show == Show.Overridden) source = source.Where(r => r.IsOverridden);
        if (_show == Show.Conflicts)
        {
            var candidates = source.Where(r => r.Plugins.Count >= 3).ToList();
            _scan.Request(candidates);
            source = candidates.Where(r => _scan.IsConflict(r.FormKey) == true);
        }

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
                        ImGui.GetWindowDrawList().AddRect(start, start + new Vector2(tile, tile), ImGui.GetColorU32(Theme.Accent), 0, 0, 2f);

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

    /// <summary>Click: just this record. Ctrl+click: add or drop it. Shift+click: the range from the last click.</summary>
    /// <returns>False when Ctrl+click dropped the record from the picked set.</returns>
    private bool Pick(EditorApp app, int index)
    {
        var io = ImGui.GetIO();
        var key = _rows[index].FormKey;
        if (io.KeyCtrl)
        {
            if (_picked.Count == 0 && app.Selected is { } current) _picked.Add(current);
            _anchor = index;
            if (_picked.Add(key)) return true;
            _picked.Remove(key);
            return _picked.Count == 0;
        }
        else if (io.KeyShift && _anchor >= 0 && _anchor < _rows.Count)
        {
            _picked.Clear();
            var (from, to) = (Math.Min(_anchor, index), Math.Max(_anchor, index));
            for (var j = from; j <= to && _picked.Count < 500; j++) _picked.Add(_rows[j].FormKey);
        }
        else
        {
            _picked.Clear();
            _picked.Add(key);
            _anchor = index;
        }
        return true;
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

        // A selection made elsewhere (render window, links) replaces the picked set.
        if (app.Selected is { } current && !_picked.Contains(current)) _picked.Clear();

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

                var selected = app.Selected == record.FormKey || _picked.Contains(record.FormKey);
                var removed = app.Document.IsRemoved(record.FormKey);
                ImGui.PushID(i);
                if (removed) ImGui.PushStyleColor(ImGuiCol.Text, Theme.Error);
                var clickedRow = ImGui.Selectable(removed ? $"{record.EditorId}  ({T("removed on save")})" : record.EditorId ?? T("<no editor id>"), selected,
                    ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick);
                if (removed) ImGui.PopStyleColor();
                if (removed && ImGui.IsItemHovered()) ImGui.SetTooltip(T("Taken out of the active file when you save; Ctrl+Z brings it back"));
                if (clickedRow)
                {
                    if (Pick(app, i)) app.Select(record.FormKey);
                    else if (_picked.Count > 0) app.Select(_picked.First());
                    _scrolledToSelection = app.SelectionVersion;
                    // As in the Creation Kit: double-click opens the record's form window.
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) app.FormWindows.Open(record.FormKey);
                }

                if (i == scrollTo) ImGui.SetScrollHereY(0.4f);

                if (ImGui.BeginPopupContextItem("##ctx"))
                {
                    if (NewFormDialog.CanCreate(record.Signature) && ImGui.MenuItem(TF($"New {T(RecordTypes.Get(record.Signature).Name)}...")))
                        app.NewForm.Show(record.Signature);
                    var removal = RecordActions.RemovalOf(app, record.FormKey);
                    if (ImGui.MenuItem(RecordActions.Label(app, removal), "Del", false, removal != RecordActions.Removal.None))
                        RecordActions.Remove(app, record.FormKey);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(RecordActions.Tooltip(app, removal, record.FormKey));
                    ImGui.Separator();
                    if (_picked.Count > 1 && _picked.Contains(record.FormKey))
                    {
                        if (ImGui.MenuItem(TF($"Compare selected ({_picked.Count})"))) app.Compare.OpenRecords(Picked);
                        if (_picked.Count > CompareWindows.MaxColumns && ImGui.IsItemHovered())
                            ImGui.SetTooltip(TF($"The first {CompareWindows.MaxColumns} are compared"));
                    }
                    else if (app.Selected is { } other && other != record.FormKey && ImGui.MenuItem(TF($"Compare with {app.Session!.Catalog.Get(other)?.EditorId ?? ws.FormIdText(other)}")))
                    {
                        app.Compare.OpenRecords([other, record.FormKey]);
                    }
                    if (ImGui.MenuItem(T("Use Info"))) app.UseInfo.Open(record.FormKey);
                    if (_picked.Count > 1 && _picked.Contains(record.FormKey))
                    {
                        var together = Picked;
                        var sameType = together.All(k => app.Session!.Catalog.Get(k)?.Signature == record.Signature);
                        if (ImGui.MenuItem(TF($"Edit together ({together.Count})"), (string?)null, false, sameType)) app.FormWindows.OpenBatch(together);
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                            ImGui.SetTooltip(sameType ? T("One form: a field you change is set on every selected record (xEdit: Copy to selected records)") : T("Only records of one type can be edited together"));
                    }
                    var copyKeys = _picked.Count > 1 && _picked.Contains(record.FormKey) ? Picked : [record.FormKey];
                    var copyable = RecordActions.CopyableAsOverride(app, copyKeys).Count;
                    if (ImGui.MenuItem(app.ActivePlugin is { } activeFile ? TF($"Copy as override into {activeFile.Name} ({copyable})") : T("Copy as override into the active file"), (string?)null, false, copyable > 0))
                        RecordActions.CopyAsOverride(app, copyKeys);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(T("The winning version goes into the active file unchanged (xEdit: Copy as override). Already there, cells, quests and dialogue are skipped."));
                    if (ImGui.MenuItem(TF($"Compare versions ({record.Plugins.Count})"), (string?)null, false, record.Plugins.Count > 1))
                        app.Compare.OpenVersions(record.FormKey);
                    if (record.Signature == "NPC_")
                    {
                        var npcs = copyKeys.Where(k => app.Session!.Catalog.Get(k)?.Signature == "NPC_").ToList();
                        if (ImGui.MenuItem(TF($"Export FaceGen ({npcs.Count})"), "Ctrl+F4")) ExportFaceGen(app, npcs);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(T("The NPC's head (mesh with its face morphs, hair, eyes…) and tint texture, written next to the active file (CK: Ctrl+F4). Without it the face is dark in game."));
                    }
                    ImGui.Separator();
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
