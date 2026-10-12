using System.Globalization;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data;
using StbEditor.Data.Catalog;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// xEdit's Weapon / Armor / Ammunition spreadsheets (and a few more item types): every record of
/// a type as a sortable table of its main numbers, edited in place — each cell is the same field
/// editor the forms use, one undo step per change.
/// </summary>
internal sealed class SpreadsheetWindow
{
    private sealed record Column(string Header, object[] Path, float Width = 1f);

    private sealed record Sheet(string Signature, string Title, Column[] Columns);

    private static readonly Sheet[] Sheets =
    [
        new("WEAP", "Weapons",
        [
            new("Value", ["BasicStats", "Value"], 0.6f), new("Weight", ["BasicStats", "Weight"], 0.6f), new("Damage", ["BasicStats", "Damage"], 0.6f),
            new("Speed", ["Data", "Speed"], 0.6f), new("Reach", ["Data", "Reach"], 0.6f), new("Animation Type", ["Data", "AnimationType"], 1f),
            new("Critical Damage", ["Critical", "Damage"], 0.6f), new("Critical Mult", ["Critical", "PercentMult"], 0.6f),
            new("Enchantment", ["ObjectEffect"], 1.6f), new("Enchantment Amount", ["EnchantmentAmount"], 0.6f),
        ]),
        new("ARMO", "Armor",
        [
            new("Armor Rating", ["ArmorRating"], 0.6f), new("Value", ["Value"], 0.6f), new("Weight", ["Weight"], 0.6f),
            new("Armor Type", ["BodyTemplate", "ArmorType"], 0.9f), new("Slots", ["BodyTemplate", "FirstPersonFlags"], 1.6f),
            new("Enchantment", ["ObjectEffect"], 1.6f),
        ]),
        new("AMMO", "Ammunition",
        [
            new("Damage", ["Damage"], 0.6f), new("Value", ["Value"], 0.6f), new("Weight", ["Weight"], 0.6f),
            new("Projectile", ["Projectile"], 1.6f), new("Flags", ["Flags"], 1.2f),
        ]),
        new("ALCH", "Potions and food", [new("Value", ["Value"], 0.6f), new("Weight", ["Weight"], 0.6f), new("Flags", ["Flags"], 1.4f)]),
        new("INGR", "Ingredients", [new("Value", ["Value"], 0.6f), new("Weight", ["Weight"], 0.6f), new("Ingredient Value", ["IngredientValue"], 0.6f)]),
        new("BOOK", "Books", [new("Value", ["Value"], 0.6f), new("Weight", ["Weight"], 0.6f), new("Type", ["Type"], 0.8f), new("Teaches", ["Teaches"], 1.6f)]),
        new("MISC", "Misc items", [new("Value", ["Value"], 0.6f), new("Weight", ["Weight"], 0.6f)]),
    ];

    private sealed class Row
    {
        public required CatalogRecord Catalog { get; init; }
        public required IMajorRecordGetter Current { get; set; }
        public object?[] Values { get; set; } = [];
    }

    private bool _open;
    private int _sheet;
    private string _filter = string.Empty;
    private string? _plugin;
    private List<Row> _rows = [];
    private (int Sheet, int Version, int Catalog, string Filter, string? Plugin)? _builtFor;
    private int _sortColumn;
    private bool _sortAscending = true;

    public void Show(string? signature = null)
    {
        _open = true;
        if (signature is not null && Array.FindIndex(Sheets, s => s.Signature == signature) is var i and >= 0) _sheet = _forceSheet = i;
    }

    public static bool Has(string signature) => Sheets.Any(s => s.Signature == signature);

    /// <summary>Rows of the shown sheet (autotests).</summary>
    public int RowCount => _rows.Count;

    /// <summary>Per column of the shown sheet: how many rows have no value (a wrong path shows as all empty).</summary>
    public string EmptyCounts => string.Join(", ", Sheets[_sheet].Columns.Select((c, i) => $"{c.Header} {_rows.Count(r => r.Values[i] is null)}"));

    public IEnumerable<string> FirstRows(int n) => _rows.Take(n).Select(r => $"{r.Catalog.EditorId}: {string.Join(" | ", r.Values.Select(v => v is null ? "—" : RecordFields.Repr(v)))}");

    /// <summary>Applies a change to one row through the same edit target the cells use (autotests).</summary>
    public bool EditForTest(EditorApp app, FormKey key, object[] path, object? value)
    {
        if (_rows.FirstOrDefault(r => r.Catalog.FormKey == key) is not { } row) return false;
        Target(app, row).Apply(TF($"Set {string.Join(".", path)}"), copy => RecordFields.Set(copy, path, value));
        return true;
    }

    public void Draw(EditorApp app)
    {
        if (!_open || app.Session is not { } session) return;
        ImGui.SetNextWindowSize(app.Scaled(1200, 700), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(Title("Spreadsheets"), ref _open, ImGuiWindowFlags.NoDocking))
        {
            if (ImGui.BeginTabBar("##sheets"))
            {
                for (var i = 0; i < Sheets.Length; i++)
                {
                    var flags = ImGuiTabItemFlags.None;
                    if (_forceSheet == i) { flags = ImGuiTabItemFlags.SetSelected; _forceSheet = -1; }
                    unsafe
                    {
                        if (!ImGui.BeginTabItem($"{T(Sheets[i].Title)}###{Sheets[i].Signature}", (bool*)null, flags)) continue;
                    }
                    _sheet = i;
                    DrawSheet(app, session, Sheets[i]);
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }
        ImGui.End();
    }

    private int _forceSheet = -1;

    private void DrawSheet(EditorApp app, Services.EditorSession session, Sheet sheet)
    {
        var em = ImGui.GetFontSize();
        ImGui.SetNextItemWidth(em * 18);
        ImGui.InputTextWithHint("##filter", T("Filter: EditorID or name"), ref _filter, 128);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(em * 14);
        if (ImGui.BeginCombo("##plugin", _plugin ?? T("Any plugin"), ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.Selectable(T("Any plugin"), _plugin is null)) _plugin = null;
            foreach (var plugin in session.Workspace.Active.Where(p => p.IsLoaded))
                if (ImGui.Selectable(plugin.Name, _plugin == plugin.Name)) _plugin = plugin.Name;
            ImGui.EndCombo();
        }

        var key = (Array.IndexOf(Sheets, sheet), app.Document.History.Version, session.Catalog.Version, _filter.Trim(), _plugin);
        if (_builtFor != key) Build(app, session, sheet, key);
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextDim, TF($"{_rows.Count:N0} record(s) · double-click a name to open its form"));

        var columns = sheet.Columns.Length + 3;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX
            | ImGuiTableFlags.Sortable | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Hideable | ImGuiTableFlags.Reorderable;
        if (!ImGui.BeginTable("##sheet", columns, flags)) return;
        ImGui.TableSetupScrollFreeze(2, 1);
        ImGui.TableSetupColumn(T("Editor ID"), ImGuiTableColumnFlags.DefaultSort, 1.6f);
        ImGui.TableSetupColumn(T("Name"), ImGuiTableColumnFlags.None, 1.4f);
        foreach (var column in sheet.Columns) ImGui.TableSetupColumn(T(column.Header), ImGuiTableColumnFlags.None, column.Width);
        ImGui.TableSetupColumn(T("Plugin"), ImGuiTableColumnFlags.None, 1f);
        ImGui.TableHeadersRow();

        unsafe
        {
            var specs = ImGui.TableGetSortSpecs();
            if (specs.Handle != null && specs.SpecsDirty)
            {
                if (specs.SpecsCount > 0)
                {
                    _sortColumn = specs.Specs[0].ColumnIndex;
                    _sortAscending = specs.Specs[0].SortDirection == ImGuiSortDirection.Ascending;
                    Sort(sheet);
                }
                specs.SpecsDirty = false;
            }
        }

        var clipper = new ImGuiListClipper();
        clipper.Begin(_rows.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var row = _rows[i];
                ImGui.TableNextRow();
                ImGui.PushID(i);
                ImGui.TableNextColumn();
                if (ImGui.Selectable(row.Catalog.EditorId ?? session.Workspace.FormIdText(row.Catalog.FormKey), app.Selected == row.Catalog.FormKey, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    app.Select(row.Catalog.FormKey);
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) app.FormWindows.Open(row.Catalog.FormKey);
                }
                ImGui.TableNextColumn();
                ImGui.Text(row.Catalog.Name ?? "");
                var target = Target(app, row);
                for (var c = 0; c < sheet.Columns.Length; c++)
                {
                    ImGui.TableNextColumn();
                    DrawCell(app, target, row, sheet.Columns[c], c);
                }
                ImGui.TableNextColumn();
                ImGui.TextColored(app.Document.Override(row.Catalog.FormKey) is not null ? Theme.Warning : Theme.TextDim,
                    app.Document.Override(row.Catalog.FormKey) is not null ? T("changed") : row.Catalog.WinningPlugin.Name);
                ImGui.PopID();
            }
        }
        clipper.End();
        ImGui.EndTable();
    }

    private static void DrawCell(EditorApp app, EditTarget target, Row row, Column column, int index)
    {
        var value = row.Values[index];
        object? owner;
        try { owner = column.Path.Length == 1 ? row.Current : RecordFields.Get(row.Current, column.Path[..^1]); }
        catch { owner = null; }
        if (owner is null)
        {
            ImGui.TextColored(Theme.TextDim, "—");
            return;
        }
        var (editable, declared) = RecordFields.Editability(owner, (string)column.Path[^1], value);
        ImGui.PushID(index);
        FieldEditors.Draw(app, target, T(column.Header), column.Path, value, declared, editable);
        ImGui.PopID();
    }

    private static EditTarget Target(EditorApp app, Row row)
    {
        var key = row.Catalog.FormKey;
        return new EditTarget(
            () => app.Document.WorkingCopy(key, row.Current),
            (name, edited) => app.Document.CommitEdit(name, edited, RecordPlace.TopLevel));
    }

    private void Build(EditorApp app, Services.EditorSession session, Sheet sheet, (int, int, int, string, string?) key)
    {
        _builtFor = key;
        var lookup = new RecordLookup(session.Workspace, session.Catalog);
        var filter = _filter.Trim();
        var rows = new List<Row>();
        foreach (var record in session.Catalog.BySignature.GetValueOrDefault(sheet.Signature) ?? [])
        {
            if (filter.Length > 0 && !(record.EditorId?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true || record.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)) continue;
            if (_plugin is { } plugin && !record.Plugins.Any(p => p.Name == plugin)) continue;
            var current = (IMajorRecordGetter?)app.Document.Override(record.FormKey)?.Record ?? lookup.Resolve(record.FormKey);
            if (current is null) continue;
            var values = sheet.Columns.Select(c =>
            {
                try { return RecordFields.Get(current, c.Path); }
                catch { return null; }
            }).ToArray();
            rows.Add(new Row { Catalog = record, Current = current, Values = values });
        }
        _rows = rows;
        Sort(sheet);
    }

    private void Sort(Sheet sheet)
    {
        int Compare(Row a, Row b)
        {
            if (_sortColumn == 0) return string.Compare(a.Catalog.EditorId, b.Catalog.EditorId, StringComparison.OrdinalIgnoreCase);
            if (_sortColumn == 1) return string.Compare(a.Catalog.Name, b.Catalog.Name, StringComparison.CurrentCultureIgnoreCase);
            var c = _sortColumn - 2;
            if (c >= sheet.Columns.Length) return a.Catalog.WinningPlugin.LoadIndex.CompareTo(b.Catalog.WinningPlugin.LoadIndex);
            var (x, y) = (a.Values[c], b.Values[c]);
            if (Number(x) is { } nx && Number(y) is { } ny) return nx.CompareTo(ny);
            return string.Compare(RecordFields.Repr(x), RecordFields.Repr(y), StringComparison.OrdinalIgnoreCase);
        }
        _rows.Sort((a, b) => _sortAscending ? Compare(a, b) : Compare(b, a));
    }

    private static double? Number(object? value) => value switch
    {
        float f => f,
        double d => d,
        int or uint or short or ushort or byte or sbyte or long or ulong => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        _ => null,
    };
}
