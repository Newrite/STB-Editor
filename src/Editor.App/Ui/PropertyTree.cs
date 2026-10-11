using System.Collections;
using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using Hexa.NET.ImGui;
using Loqui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Strings;
using StbEditor.Core;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Ui;

/// <summary>
/// Every field of a record as a tree (the «All fields» tab and plugin versions); editable with an
/// <see cref="EditTarget"/>. The form tabs (<c>FormView</c>) show the same fields laid out like
/// the Creation Kit; this tree is the complete, uncurated view.
/// </summary>
internal static class PropertyTree
{
    private const int MaxDepth = 16;
    private const int ListPageSize = 500;

    [ThreadStatic] private static EditTarget? _target;
    private static readonly List<object> Path = [];

    public static void Draw(EditorApp app, object root, EditTarget? target = null)
    {
        _target = target;
        Path.Clear();
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("##alltree", 2, flags)) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("Field"), ImGuiTableColumnFlags.WidthStretch, 0.42f);
        ImGui.TableSetupColumn(T("Value"), ImGuiTableColumnFlags.WidthStretch, 0.58f);
        ImGui.TableHeadersRow();

        DrawMembers(app, root, 0);
        ImGui.EndTable();
    }

    private static void DrawMembers(EditorApp app, object obj, int depth)
    {
        var hideHeader = depth == 0 && obj is IMajorRecordGetter && !app.Settings.ShowRecordHeader;
        foreach (var property in RecordFields.PropertiesOf(obj))
        {
            if (hideHeader && RecordFields.IsRecordHeader(property.Name)) continue;
            object? value;
            string? error = null;
            try
            {
                value = property.GetValue(obj);
            }
            catch (TargetInvocationException ex)
            {
                value = null;
                error = ex.InnerException?.Message ?? ex.Message;
            }

            ImGui.PushID(property.Name);
            Path.Add(property.Name);
            DrawValue(app, property.Name, value, error, depth, obj, property.PropertyType);
            Path.RemoveAt(Path.Count - 1);
            ImGui.PopID();
        }
    }

    private static void DrawValue(EditorApp app, string label, object? value, string? error, int depth, object owner, Type? declared)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        if (error is not null)
        {
            Leaf(label);
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.Error, T("read error: ") + error);
            return;
        }

        if (RecordFields.IsComposite(value) && depth < MaxDepth)
        {
            var open = ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick);
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.TextDim, Summary(app, value!));

            if (!open) return;

            if (value is IEnumerable list and not string && !RecordFields.IsGendered(value)) DrawList(app, list, depth + 1);
            else DrawMembers(app, value!, depth + 1);
            if (_target is not null && value is IEnumerable and not string && !RecordFields.IsGendered(value)) DrawListAdd();

            ImGui.TreePop();
            return;
        }

        Leaf(label);
        ImGui.TableNextColumn();
        if (owner is IEnumerable)
        {
            var element = RecordFields.ElementType(owner);
            FieldEditors.Draw(app, _target, label, Path, value, element, RecordFields.KindOf(value, element) is not (FieldKind.None or FieldKind.Bytes));
        }
        else
        {
            var (editable, type) = RecordFields.Editability(owner, label, value);
            FieldEditors.Draw(app, _target, label, Path, value, type ?? declared, editable);
        }
    }

    private static void DrawList(EditorApp app, IEnumerable list, int depth)
    {
        var index = 0;
        foreach (var item in list)
        {
            if (index >= ListPageSize)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, TF($"… more than {ListPageSize} items, use the cell or record views"));
                return;
            }

            ImGui.PushID(index);
            Path.Add(index);
            DrawValue(app, $"[{index}]", item, null, depth, list, null);
            Path.RemoveAt(Path.Count - 1);
            ImGui.PopID();
            index++;
        }

        if (index == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.TextDim, T("(empty)"));
        }
    }

    /// <summary>"+" and "−" under an editable list: append a new item / drop the last one.</summary>
    private static void DrawListAdd()
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        var at = Path.ToArray();
        ImGui.PushID("add" + FieldEditors.PathKey(at));
        if (ImGui.SmallButton(T("+ add"))) _target!.Apply(T("Add item"), copy => RecordFields.AddItem(copy, at));
        ImGui.SameLine();
        if (ImGui.SmallButton(T("− last")))
            _target!.Apply(T("Remove item"), copy =>
            {
                if (RecordFields.Get(copy, at) is IList { Count: > 0 } items) items.RemoveAt(items.Count - 1);
            });
        ImGui.PopID();
    }

    private static void Leaf(string label) =>
        ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.Bullet);

    internal static string Summary(EditorApp app, object value)
    {
        switch (value)
        {
            case IMajorRecordGetter record:
                return $"{record.EditorID ?? ""} [{app.Session!.Workspace.FormIdText(record.FormKey)}]";

            case IEnumerable list when !RecordFields.IsGendered(value):
                var count = list switch
                {
                    ICollection c => c.Count,
                    _ => CountOf(value),
                };
                return count >= 0 ? $"[{count}]" : "[…]";

            default:
                return InlineSummary(value) ?? value.GetType().Name.Replace("BinaryOverlay", "");
        }
    }

    /// <summary>
    /// Small structures (bounds, coordinates) read better inline: "First = (-20, -20, 0), Second = (20, 20, 40)".
    /// </summary>
    internal static string? InlineSummary(object value)
    {
        var properties = RecordFields.PropertiesOf(value);
        if (properties.Length is 0 or > 4) return null;

        var parts = new List<string>(properties.Length);
        foreach (var p in properties)
        {
            object? v;
            try { v = p.GetValue(value); }
            catch { return null; }

            if (RecordFields.IsComposite(v)) return null;
            parts.Add($"{p.Name} = {v switch { null => "—", float f => RecordFields.FloatText(f), ITranslatedStringGetter t => t.String, _ => v.ToString() }}");
        }

        var text = string.Join(", ", parts);
        return text.Length > 160 ? text[..160] + "…" : text;
    }

    /// <summary>Count via a generic IReadOnlyCollection&lt;T&gt; without enumerating lazy overlays.</summary>
    private static int CountOf(object value)
    {
        foreach (var iface in value.GetType().GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>))
                return (int)iface.GetProperty("Count")!.GetValue(value)!;
        }
        return -1;
    }
}
