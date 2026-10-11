using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Strings;
using Noggog;
using StbEditor.Core;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Ui;

/// <summary>How a row of a comparison reads, with xEdit's colours.</summary>
internal enum CompareState
{
    /// <summary>Every column holds the same value.</summary>
    Same,

    /// <summary>Versions: the winner carries every change made to the master (green).</summary>
    Override,

    /// <summary>Versions: some plugin's change is lost to the winner (red).</summary>
    Conflict,

    /// <summary>Different records: the values differ (yellow).</summary>
    Differ,
}

/// <summary>One column: a plugin's version of a record, or one of several records.</summary>
internal sealed record CompareColumn(string Title, string Tooltip, IMajorRecordGetter? Record);

/// <summary>A row: a field with its value in every column; structures and lists have children.</summary>
internal sealed class CompareNode
{
    public required string Label { get; init; }
    public required string[] Text { get; init; }

    /// <summary>Comparable text per column ("" = empty or absent).</summary>
    public required string[] Keys { get; init; }

    public FormKey?[]? Links { get; set; }
    public List<CompareNode> Children { get; } = [];
    public bool Group { get; set; }

    /// <summary>A list: its row shows the size even when open (a structure's inline summary only when closed).</summary>
    public bool IsList { get; set; }
    public CompareState State { get; set; }
    public int Count { get; set; }
}

/// <summary>
/// Builds the side-by-side field tree of several records (xEdit's view): members are matched by
/// name, lists by their value or by the record they point at (an inventory row by its item, a
/// keyword by itself), so an added or removed row lines up instead of shifting everything below.
/// </summary>
internal static class RecordCompare
{
    private const int MaxDepth = 12;
    private const int MaxItems = 500;

    public static CompareNode Build(EditorApp app, IReadOnlyList<object?> roots, bool versions, bool showHeader)
    {
        var n = roots.Count;
        var root = new CompareNode { Label = "", Text = new string[n], Keys = new string[n], Group = true };
        Members(app, root, roots, 0, showHeader);
        Classify(root, versions);
        return root;
    }

    private static void Members(EditorApp app, CompareNode parent, IReadOnlyList<object?> owners, int depth, bool showHeader)
    {
        var n = owners.Count;
        var names = new List<string>();
        var byName = new Dictionary<string, PropertyInfo?[]>();
        for (var col = 0; col < n; col++)
        {
            if (owners[col] is not { } owner) continue;
            var header = depth == 0 && owner is IMajorRecordGetter && !showHeader;
            foreach (var property in RecordFields.PropertiesOf(owner))
            {
                if (header && RecordFields.IsRecordHeader(property.Name)) continue;
                // Padding the game never reads would flag false conflicts (xEdit ignores it too).
                if (!app.Settings.ShowRecordHeader && property.Name.StartsWith("Unused", StringComparison.Ordinal)) continue;
                if (!byName.TryGetValue(property.Name, out var slots))
                {
                    byName[property.Name] = slots = new PropertyInfo?[n];
                    names.Add(property.Name);
                }
                slots[col] = property;
            }
        }

        foreach (var name in names)
        {
            var slots = byName[name];
            var values = new object?[n];
            for (var col = 0; col < n; col++)
            {
                if (slots[col] is not { } property || owners[col] is not { } owner) continue;
                try { values[col] = property.GetValue(owner); }
                catch (TargetInvocationException) { values[col] = null; }
            }
            Add(app, parent, FieldEditors.Label(name), values, depth + 1);
        }
    }

    private static void Add(EditorApp app, CompareNode parent, string label, object?[] values, int depth)
    {
        var n = values.Length;
        var node = new CompareNode { Label = label, Text = new string[n], Keys = new string[n] };
        parent.Children.Add(node);

        if (depth < MaxDepth && values.Any(RecordFields.IsComposite))
        {
            node.Group = true;
            for (var col = 0; col < n; col++)
            {
                // Lists show their size, small structures their fields inline; others nothing (as xEdit).
                node.Text[col] = values[col] switch
                {
                    null => "",
                    IEnumerable and not string when !RecordFields.IsGendered(values[col]) => PropertyTree.Summary(app, values[col]!),
                    { } v => PropertyTree.InlineSummary(v) ?? "",
                };
                node.Keys[col] = node.Text[col] == "[0]" ? "" : node.Text[col]; // an empty list and no list read alike
            }

            if (values.Any(v => v is not null && RecordFields.IsDictionary(v))) Map(app, node, values, depth);
            else if (values.Any(v => v is IEnumerable and not string && !RecordFields.IsGendered(v)))
            {
                node.IsList = true;
                List(app, node, values, depth);
            }
            else Members(app, node, values, depth, true);
            return;
        }

        for (var col = 0; col < n; col++)
        {
            var value = values[col];
            node.Keys[col] = Key(value);
            node.Text[col] = Display(app, value, label);
            if (value is IFormLinkGetter { FormKeyNullable: { IsNull: false } key })
            {
                node.Links ??= new FormKey?[n];
                node.Links[col] = key;
            }
        }
    }

    /// <summary>Lists, aligned by their values (or the record each row points at); by position otherwise.</summary>
    private static void List(EditorApp app, CompareNode node, object?[] values, int depth)
    {
        var n = values.Length;
        var columns = new List<(string Key, object? Item)>[n];
        var keyed = true;
        for (var col = 0; col < n; col++)
        {
            columns[col] = [];
            if (values[col] is not IEnumerable list || list is string) continue;
            foreach (var item in list)
            {
                if (columns[col].Count >= MaxItems) break;
                var key = RecordFields.IsComposite(item) ? LinkKey(item!) : Key(item);
                if (key is null) keyed = false;
                columns[col].Add((key ?? "", item));
            }
        }

        // Same key twice in a list (two rows of the same item): the second is "key#2".
        for (var col = 0; col < n; col++)
        {
            var seen = new Dictionary<string, int>();
            for (var i = 0; i < columns[col].Count; i++)
            {
                var key = keyed ? columns[col][i].Key : i.ToString(CultureInfo.InvariantCulture);
                var count = seen[key] = seen.GetValueOrDefault(key) + 1;
                columns[col][i] = (count > 1 ? $"{key}#{count}" : key, columns[col][i].Item);
            }
        }

        // Ordered union: each column's new keys go right after the key it had before them.
        var merged = new List<string>();
        var known = new HashSet<string>();
        foreach (var column in columns)
        {
            var at = 0;
            foreach (var (key, _) in column)
            {
                if (known.Contains(key)) { at = merged.IndexOf(key) + 1; continue; }
                merged.Insert(at++, key);
                known.Add(key);
            }
        }

        var lookups = columns.Select(c => c.ToDictionary(e => e.Key, e => e.Item)).ToArray();
        for (var i = 0; i < merged.Count; i++)
        {
            var items = new object?[n];
            for (var col = 0; col < n; col++) items[col] = lookups[col].GetValueOrDefault(merged[i]);
            Add(app, node, $"[{i}]", items, depth + 1);
        }
        node.Count = merged.Count;
    }

    private static void Map(EditorApp app, CompareNode node, object?[] values, int depth)
    {
        var n = values.Length;
        var keys = new List<object>();
        var maps = new Dictionary<object, object?>[n];
        for (var col = 0; col < n; col++)
        {
            maps[col] = [];
            if (values[col] is not { } map || !RecordFields.IsDictionary(map)) continue;
            foreach (var (key, value) in RecordFields.Entries(map))
            {
                if (!maps.Take(col).Any(m => m.ContainsKey(key)) && !maps[col].ContainsKey(key)) keys.Add(key);
                maps[col][key] = value;
            }
        }
        foreach (var key in keys)
            Add(app, node, FieldEditors.Label(key.ToString() ?? "?"), maps.Select(m => m.GetValueOrDefault(key)).ToArray(), depth + 1);
    }

    /// <summary>The first record a structure points at (an inventory row's item, an effect's base effect), two levels deep.</summary>
    private static string? LinkKey(object item)
    {
        var nested = new List<object>();
        foreach (var property in RecordFields.PropertiesOf(item))
        {
            object? value;
            try { value = property.GetValue(item); }
            catch (TargetInvocationException) { continue; }
            if (value is IFormLinkGetter link) return Key(link);
            if (value is not null && RecordFields.IsComposite(value) && value is not IEnumerable) nested.Add(value);
        }
        foreach (var inner in nested)
        {
            foreach (var property in RecordFields.PropertiesOf(inner))
            {
                object? value;
                try { value = property.GetValue(inner); }
                catch (TargetInvocationException) { continue; }
                if (value is IFormLinkGetter link) return Key(link);
            }
        }
        return null;
    }

    /// <summary>Exact comparable text: floats to the last digit, links by FormKey, empty and absent alike.</summary>
    private static string Key(object? value)
    {
        if (value is null) return "";
        if (value is IFormLinkGetter link) return link.FormKeyNullable is { IsNull: false } key ? key.ToString() : "";
        if (RecordFields.IsBytes(value)) return Convert.ToHexString(Bytes(value));
        var text = RecordFields.Repr(value);
        return text == "null" ? "" : text;
    }

    private static ReadOnlySpan<byte> Bytes(object value) => value switch
    {
        byte[] a => a,
        ReadOnlyMemorySlice<byte> s => s.Span,
        MemorySlice<byte> m => m.Span,
        _ => [],
    };

    private static string Display(EditorApp app, object? value, string label)
    {
        switch (value)
        {
            case null:
                return "";
            case IFormLinkGetter link:
                if (link.FormKeyNullable is not { IsNull: false } key) return "NULL";
                var session = app.Session!;
                var record = session.Catalog.Get(key);
                return record is null
                    ? session.Workspace.FormIdText(key)
                    : $"{record.EditorId ?? record.Name ?? "?"} [{record.Signature}:{session.Workspace.FormIdText(key)}]";
            case ITranslatedStringGetter translated:
                return translated.String ?? "";
            case float f:
                return RecordFields.FloatText(f);
            case double d:
                return RecordFields.DoubleText(d);
            case bool b:
                return b ? "true" : "false";
            case Enum e:
                return e.ToString();
            case int or uint when label.Contains("Flags", StringComparison.OrdinalIgnoreCase):
                return $"0x{Convert.ToUInt32(value is int i ? unchecked((uint)i) : value, CultureInfo.InvariantCulture):X8}";
            case System.Drawing.Color c:
                return $"{c.R}, {c.G}, {c.B}, {c.A}";
            case string s:
                return s.ReplaceLineEndings(" ⏎ ");
            default:
                if (RecordFields.IsBytes(value))
                {
                    var bytes = Bytes(value);
                    var hex = Convert.ToHexString(bytes.Length > 32 ? bytes[..32] : bytes);
                    return bytes.Length > 32 ? $"{hex}… ({bytes.Length})" : hex;
                }
                return RecordFields.Repr(value);
        }
    }

    private static void Classify(CompareNode node, bool versions)
    {
        if (!node.Group || node.Children.Count == 0)
        {
            node.State = State(node.Keys, versions);
            return;
        }
        var state = CompareState.Same;
        foreach (var child in node.Children)
        {
            Classify(child, versions);
            if (Rank(child.State) > Rank(state)) state = child.State;
        }
        node.State = state;
    }

    private static int Rank(CompareState state) => state switch
    {
        CompareState.Conflict => 3,
        CompareState.Override or CompareState.Differ => 2,
        _ => 0,
    };

    private static CompareState State(string[] keys, bool versions)
    {
        if (keys.All(k => k == keys[0])) return CompareState.Same;
        if (!versions) return CompareState.Differ;
        string master = keys[0], winner = keys[^1];
        for (var i = 1; i < keys.Length - 1; i++)
        {
            if (keys[i] != master && keys[i] != winner) return CompareState.Conflict;
        }
        return CompareState.Override;
    }

    /// <summary>Text colour of a cell: the winning value green, a lost change red, a replaced master value dim.</summary>
    public static Vector4 CellColor(CompareNode node, int col)
    {
        var keys = node.Keys;
        return node.State switch
        {
            CompareState.Differ => col == 0 || keys[col] == keys[0] ? Theme.Text : Theme.Warning,
            CompareState.Override => keys[col] == keys[0] ? Theme.TextDim : Theme.Good,
            CompareState.Conflict => keys[col] == keys[^1] ? Theme.Good : keys[col] == keys[0] ? Theme.TextDim : Theme.Error,
            _ => node.Group ? Theme.TextDim : Theme.Text,
        };
    }

    public static Vector4 StateColor(CompareState state) => state switch
    {
        CompareState.Override => Theme.Good,
        CompareState.Conflict => Theme.Error,
        CompareState.Differ => Theme.Warning,
        _ => Theme.Text,
    };
}

/// <summary>
/// The side-by-side table of a comparison (record versions in the Inspector and compare windows,
/// several records from the Object Window); rebuilt when the compared records change.
/// </summary>
internal sealed class CompareView
{
    private CompareNode? _root;
    private IMajorRecordGetter?[] _builtFor = [];
    private (bool Header, bool Versions) _builtWith;

    /// <summary>Rows of the last drawn table (autotests).</summary>
    public CompareNode? Root => _root;

    public void Draw(EditorApp app, IReadOnlyList<CompareColumn> columns, bool versions)
    {
        if (columns.Count == 0)
        {
            ImGui.TextColored(Theme.TextDim, T("Nothing to compare."));
            return;
        }

        var settings = app.Settings;
        var records = columns.Select(c => c.Record).ToArray();
        if (_root is null || !records.SequenceEqual(_builtFor, ReferenceEqualityComparer.Instance) || _builtWith != (settings.ShowRecordHeader, versions))
        {
            _builtFor = records;
            _builtWith = (settings.ShowRecordHeader, versions);
            _root = RecordCompare.Build(app, records, versions && columns.Count > 1, settings.ShowRecordHeader);
        }

        var onlyDiff = settings.CompareOnlyDifferences;
        if (ImGui.Checkbox(T("Only differences"), ref onlyDiff)) settings.CompareOnlyDifferences = onlyDiff;
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextDim, "·");
        if (versions && columns.Count > 1)
        {
            Legend(Theme.Good, T("the winner's change"));
            Legend(Theme.Error, T("a change the winner loses (conflict)"));
            Legend(Theme.TextDim, T("replaced value"));
        }
        else if (columns.Count > 1)
        {
            Legend(Theme.Warning, T("differs from the first column"));
        }

        var em = ImGui.GetFontSize();
        var flags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersOuterH | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollX
            | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Reorderable;
        if (!ImGui.BeginTable("##compare", columns.Count + 1, flags)) return;

        var fieldWidth = em * 13;
        var valueWidth = Math.Max(em * 13, (ImGui.GetContentRegionAvail().X - fieldWidth) / columns.Count - ImGui.GetStyle().CellPadding.X * 2 - 2);
        ImGui.TableSetupScrollFreeze(1, 1);
        ImGui.TableSetupColumn(T("Field"), ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoReorder, fieldWidth);
        for (var i = 0; i < columns.Count; i++)
            ImGui.TableSetupColumn($"{columns[i].Title}##c{i}", ImGuiTableColumnFlags.WidthFixed, valueWidth);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        for (var i = 0; i <= columns.Count; i++)
        {
            ImGui.TableSetColumnIndex(i);
            ImGui.TableHeader(i == 0 ? T("Field") : $"{columns[i - 1].Title}##c{i - 1}");
            if (i > 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(columns[i - 1].Tooltip);
        }

        foreach (var child in _root.Children) DrawNode(app, child, onlyDiff);
        ImGui.EndTable();
    }

    private static void Legend(Vector4 color, string text)
    {
        ImGui.SameLine();
        var h = ImGui.GetTextLineHeight();
        var at = ImGui.GetCursorScreenPos() + new Vector2(0, (ImGui.GetFrameHeight() - h) * 0.5f + h * 0.2f);
        ImGui.GetWindowDrawList().AddRectFilled(at, at + new Vector2(h * 0.6f, h * 0.6f), ImGui.GetColorU32(color));
        ImGui.Dummy(new Vector2(h * 0.6f, h));
        ImGui.SameLine(0, h * 0.3f);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, text);
    }

    private static void DrawNode(EditorApp app, CompareNode node, bool onlyDiff)
    {
        if (onlyDiff && node.State == CompareState.Same) return;

        ImGui.TableNextRow();
        if (node.State != CompareState.Same)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(Theme.RowTint(RecordCompare.StateColor(node.State))));

        ImGui.TableNextColumn();
        ImGui.PushStyleColor(ImGuiCol.Text, node.State == CompareState.Same ? Theme.Text : RecordCompare.StateColor(node.State));
        bool open;
        if (node.Group && node.Children.Count > 0)
        {
            var flags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick;
            if (node.Count <= 24) flags |= ImGuiTreeNodeFlags.DefaultOpen;
            open = ImGui.TreeNodeEx(node.Label, flags);
        }
        else
        {
            ImGui.TreeNodeEx(node.Label, ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.Bullet);
            open = false;
        }
        ImGui.PopStyleColor();

        var summary = !open || node.IsList;
        for (var col = 0; col < node.Text.Length; col++)
        {
            ImGui.TableNextColumn();
            var text = node.Group && !summary ? "" : node.Text[col];
            if (text.Length == 0) continue;
            ImGui.TextColored(RecordCompare.CellColor(node, col), text);
            if (!ImGui.IsItemHovered()) continue;
            var link = node.Links?[col];
            ImGui.SetTooltip(link is not null ? text + "\n" + T("Double-click to open") : text);
            if (link is { } key && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) app.Select(key);
        }

        if (!open) return;
        foreach (var child in node.Children) DrawNode(app, child, onlyDiff);
        ImGui.TreePop();
    }
}
