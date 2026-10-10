using System.Collections;
using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using Hexa.NET.ImGui;
using Loqui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Strings;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Ui;

/// <summary>Makes a <see cref="PropertyTree"/> editable: edits go to a working copy committed as one undo step.</summary>
internal sealed class EditTarget(Func<IMajorRecord> copy, Action<string, IMajorRecord> commit)
{
    public IMajorRecord Copy() => copy();
    public void Commit(string name, IMajorRecord edited) => commit(name, edited);
}

/// <summary>
/// Generic field tree over Mutagen getter interfaces. Every Skyrim record type is shown without
/// per-type code; dedicated editors replace parts of it later.
/// </summary>
internal static class PropertyTree
{
    private const int MaxDepth = 16;
    private const int ListPageSize = 500;

    /// <summary>Members of the Loqui/Mutagen plumbing, not record data.</summary>
    private static readonly HashSet<string> Hidden =
    [
        "Registration", "StaticRegistration", "BinaryWriteTranslator", "CommonInstance",
        "CommonSetterInstance", "CommonSetterTranslationInstance", "EqualityContract",
    ];

    /// <summary>Record header fields go first, in this order.</summary>
    private static readonly string[] HeaderOrder =
        ["FormKey", "EditorID", "MajorRecordFlagsRaw", "IsDeleted", "IsCompressed", "FormVersion", "VersionControl", "Version2"];

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    [ThreadStatic] private static EditTarget? _target;
    private static readonly List<object> Path = [];
    private static readonly Dictionary<string, object> Pending = new();

    public static void Draw(EditorApp app, object root, EditTarget? target = null)
    {
        _target = target;
        Path.Clear();
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("##props", 2, flags)) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("Field"), ImGuiTableColumnFlags.WidthStretch, 0.42f);
        ImGui.TableSetupColumn(T("Value"), ImGuiTableColumnFlags.WidthStretch, 0.58f);
        ImGui.TableHeadersRow();

        DrawMembers(app, root, 0);
        ImGui.EndTable();
    }

    private static void DrawMembers(EditorApp app, object obj, int depth)
    {
        foreach (var property in PropertiesOf(obj))
        {
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
            DrawValue(app, property.Name, value, error, depth, obj);
            Path.RemoveAt(Path.Count - 1);
            ImGui.PopID();
        }
    }

    private static void DrawValue(EditorApp app, string label, object? value, string? error, int depth, object? owner = null)
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

        if (IsComposite(value) && depth < MaxDepth)
        {
            var open = ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick);
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.TextDim, Summary(app, value!));

            if (!open) return;

            if (value is IEnumerable list and not string) DrawList(app, list, depth + 1);
            else DrawMembers(app, value!, depth + 1);
            if (_target is not null && value is IEnumerable and not string) DrawListAdd(value!);

            ImGui.TreePop();
            return;
        }

        Leaf(label);
        ImGui.TableNextColumn();

        if (_target is not null && owner is not null && Editable(owner, label, value) && DrawEditor(app, label, value))
            return;

        // Raw flag words (MajorRecordFlagsRaw etc.) mean nothing in decimal.
        if (label.Contains("Flags") && value is int or uint)
        {
            var raw = Convert.ToUInt32(value is int i ? unchecked((uint)i) : value);
            ImGui.Text($"0x{raw:X8}");
            return;
        }

        DrawLeafValue(app, value);
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
            DrawValue(app, $"[{index}]", item, null, depth, list);
            if (_target is not null && IsComposite(item))
            {
                // Remove button on the row after the item's subtree.
            }
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

    // ------------------------------------------------------------------ editing

    /// <summary>The setter on the mutable record class for a member (getter interfaces have none).</summary>
    private static PropertyInfo? ClassProperty(object owner, string name)
    {
        var classType = owner is ILoquiObject loqui ? loqui.Registration.ClassType : owner.GetType();
        return classType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
    }

    private static bool Editable(object owner, string label, object? value)
    {
        if (owner is IEnumerable) return value is not null && !IsComposite(value);
        if (label is "FormKey" or "FormVersion" or "VersionControl" or "Version2" or "IsCompressed") return false;
        var property = ClassProperty(owner, label);
        if (property is null) return false;
        if (value is IFormLinkGetter) return true;
        return property.CanWrite && property.SetMethod?.IsPublic == true && value is not null && IsEditableType(value);
    }

    private static bool IsEditableType(object value) => value is string or bool or int or uint or short or ushort or byte or sbyte or long or float or double
        or Enum or Noggog.P3Float or Noggog.P2Float or System.Drawing.Color or ITranslatedStringGetter;

    private static string PathKey() => string.Join('/', Path);

    /// <summary>Widget for an editable leaf; returns true when it drew (the read-only view is skipped).</summary>
    private static bool DrawEditor(EditorApp app, string label, object? value)
    {
        var key = PathKey();
        ImGui.SetNextItemWidth(-1);
        switch (value)
        {
            case IFormLinkGetter link:
            {
                FormKey? current = link.FormKeyNullable;
                var signatures = SignaturesFor(link);
                if (FormField.Draw(app, key, ref current, signatures, references: signatures is null && IsReferenceLink(link)))
                    Apply(TF($"Set {label}"), key, target => SetLink(target, current));
                return true;
            }
            case bool b:
                if (ImGui.Checkbox("##v", ref b)) Apply(TF($"Set {label}"), key, Setter(b));
                return true;
            case string text:
            {
                var buffer = Pending.TryGetValue(key, out var p) ? (string)p : text;
                if (ImGui.InputText("##v", ref buffer, 1024)) Pending[key] = buffer;
                if (ImGui.IsItemDeactivatedAfterEdit() && Pending.Remove(key, out var done)) Apply(TF($"Set {label}"), key, Setter((string)done));
                return true;
            }
            case ITranslatedStringGetter translated:
            {
                var buffer = Pending.TryGetValue(key, out var p) ? (string)p : translated.String ?? string.Empty;
                if (ImGui.InputText("##v", ref buffer, 1024)) Pending[key] = buffer;
                if (ImGui.IsItemDeactivatedAfterEdit() && Pending.Remove(key, out var done))
                    Apply(TF($"Set {label}"), key, Setter(new TranslatedString(TranslatedString.DefaultLanguage, (string)done)));
                return true;
            }
            case float f:
            {
                var v = Pending.TryGetValue(key, out var p) ? (float)p : f;
                if (ImGui.DragFloat("##v", ref v, Math.Max(Math.Abs(f) * 0.01f, 0.01f), 0, 0, "%.4g")) Pending[key] = v;
                if (ImGui.IsItemDeactivatedAfterEdit() && Pending.Remove(key, out var done)) Apply(TF($"Set {label}"), key, Setter((float)done));
                return true;
            }
            case double d:
            {
                var v = (float)(Pending.TryGetValue(key, out var p) ? (double)p : d);
                if (ImGui.DragFloat("##v", ref v, 0.01f)) Pending[key] = (double)v;
                if (ImGui.IsItemDeactivatedAfterEdit() && Pending.Remove(key, out var done)) Apply(TF($"Set {label}"), key, Setter((double)done));
                return true;
            }
            case int or uint or short or ushort or byte or sbyte or long:
            {
                var v = Pending.TryGetValue(key, out var p) ? (long)p : Convert.ToInt64(value);
                var text = v.ToString();
                if (label.Contains("Flags")) return false; // raw flag words stay read-only hex
                if (ImGui.InputText("##v", ref text, 32, ImGuiInputTextFlags.CharsDecimal) && long.TryParse(text, out var parsed)) Pending[key] = parsed;
                if (ImGui.IsItemDeactivatedAfterEdit() && Pending.Remove(key, out var done))
                {
                    var typed = Convert.ChangeType((long)done, value!.GetType());
                    Apply(TF($"Set {label}"), key, Setter(typed));
                }
                return true;
            }
            case Enum e:
                DrawEnum(label, key, e);
                return true;
            case Noggog.P3Float p3:
            {
                var v = Pending.TryGetValue(key, out var p) ? (Vector3)p : new Vector3(p3.X, p3.Y, p3.Z);
                if (ImGui.DragFloat3("##v", ref v, 1f)) Pending[key] = v;
                if (ImGui.IsItemDeactivatedAfterEdit() && Pending.Remove(key, out var done))
                {
                    var r = (Vector3)done;
                    Apply(TF($"Set {label}"), key, Setter(new Noggog.P3Float(r.X, r.Y, r.Z)));
                }
                return true;
            }
            case Noggog.P2Float p2:
            {
                var v = Pending.TryGetValue(key, out var p) ? (Vector2)p : new Vector2(p2.X, p2.Y);
                if (ImGui.DragFloat2("##v", ref v, 1f)) Pending[key] = v;
                if (ImGui.IsItemDeactivatedAfterEdit() && Pending.Remove(key, out var done))
                {
                    var r = (Vector2)done;
                    Apply(TF($"Set {label}"), key, Setter(new Noggog.P2Float(r.X, r.Y)));
                }
                return true;
            }
            case System.Drawing.Color color:
            {
                var v = Pending.TryGetValue(key, out var p) ? (Vector4)p : new Vector4(color.R, color.G, color.B, color.A) / 255f;
                if (ImGui.ColorEdit4("##v", ref v, ImGuiColorEditFlags.Uint8)) Pending[key] = v;
                if (ImGui.IsItemDeactivatedAfterEdit() && Pending.Remove(key, out var done))
                {
                    var c = (Vector4)done * 255f;
                    Apply(TF($"Set {label}"), key, Setter(System.Drawing.Color.FromArgb((int)Math.Round(c.W), (int)Math.Round(c.X), (int)Math.Round(c.Y), (int)Math.Round(c.Z))));
                }
                return true;
            }
        }
        return false;
    }

    private static void DrawEnum(string label, string key, Enum e)
    {
        var type = e.GetType();
        var values = Enum.GetValues(type).Cast<Enum>().ToArray();
        if (type.IsDefined(typeof(FlagsAttribute), false))
        {
            var raw = Convert.ToUInt64(e);
            if (ImGui.BeginCombo("##v", raw == 0 ? "(none)" : e.ToString()))
            {
                foreach (var flag in values)
                {
                    var bit = Convert.ToUInt64(flag);
                    if (bit == 0) continue;
                    var on = (raw & bit) == bit;
                    if (ImGui.Checkbox(flag.ToString(), ref on))
                    {
                        var next = on ? raw | bit : raw & ~bit;
                        Apply(TF($"Set {label}"), key, Setter(Enum.ToObject(type, next)));
                    }
                }
                ImGui.EndCombo();
            }
            return;
        }

        if (!ImGui.BeginCombo("##v", e.ToString())) return;
        foreach (var option in values)
            if (ImGui.Selectable(option.ToString(), Equals(option, e)))
                Apply(TF($"Set {label}"), key, Setter(option));
        ImGui.EndCombo();
    }

    private static Action<object> Setter(object value) => target => ((SetTarget)target).Set(value);

    /// <summary>The member a path ends in, on the working copy.</summary>
    private sealed class SetTarget(object owner, object segment)
    {
        public object Owner { get; } = owner;
        public object Segment { get; } = segment;

        public object? Get() => Segment switch
        {
            int i when Owner is IList list => list[i],
            string name => ClassProperty(Owner, name)?.GetValue(Owner),
            _ => null,
        };

        public void Set(object? value)
        {
            switch (Segment)
            {
                case int i when Owner is IList list:
                    list[i] = value;
                    break;
                case string name:
                    ClassProperty(Owner, name)!.SetValue(Owner, value);
                    break;
            }
        }
    }

    private static void SetLink(object target, FormKey? value)
    {
        var t = (SetTarget)target;
        if (t.Get() is { } link)
        {
            var method = link.GetType().GetMethods().FirstOrDefault(m => m.Name == "SetTo" && m.GetParameters() is [{ ParameterType: var pt }] && (pt == typeof(FormKey?) || pt == typeof(FormKey)));
            if (method is not null && (value is not null || method.GetParameters()[0].ParameterType == typeof(FormKey?)))
            {
                method.Invoke(link, [value ?? (object?)null]);
                return;
            }
            if (value is null)
            {
                link.GetType().GetMethod(T("Clear"))?.Invoke(link, null);
                return;
            }
        }
        throw new InvalidOperationException("this link cannot be set");
    }

    /// <summary>Walks <paramref name="path"/> on a fresh working copy, applies, commits one undo step.</summary>
    private static void Apply(string name, string pathKey, Action<object> apply)
    {
        if (_target is not { } target) return;
        var segments = pathKey.Split('/').Select(x => int.TryParse(x, out var i) ? (object)i : x).ToList();
        try
        {
            var copy = target.Copy();
            object current = copy;
            for (var i = 0; i < segments.Count - 1; i++)
                current = new SetTarget(current, segments[i]).Get() ?? throw new InvalidOperationException($"{segments[i]} is empty");
            apply(new SetTarget(current, segments[^1]));
            target.Commit(name, copy);
        }
        catch (Exception ex)
        {
            Services.Log.Warn($"edit {pathKey}: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    /// <summary>"+" under an editable list: appends a new empty item (or a null link).</summary>
    private static void DrawListAdd(object list)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        var key = PathKey();
        ImGui.PushID("add" + key);
        if (ImGui.SmallButton(T("+ add")))
        {
            Apply(T("Add item"), key, target =>
            {
                var t = (SetTarget)target;
                if (t.Get() is not IList items) throw new InvalidOperationException("not a list");
                var element = items.GetType().GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>))?.GetGenericArguments()[0];
                if (element is null) throw new InvalidOperationException("unknown item type");
                var concrete = element.IsInterface ? ConcreteFor(element) : element;
                items.Add(Activator.CreateInstance(concrete ?? throw new InvalidOperationException($"cannot create {element.Name}")));
            });
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(T("− last")))
        {
            Apply(T("Remove item"), key, target =>
            {
                if (((SetTarget)target).Get() is IList { Count: > 0 } items) items.RemoveAt(items.Count - 1);
            });
        }
        ImGui.PopID();
    }

    private static Type? ConcreteFor(Type element)
    {
        // IFormLinkGetter<T> lists hold FormLink<T>.
        if (element.IsGenericType && element.GetGenericArguments() is [var t])
            return typeof(FormLink<>).MakeGenericType(t);
        return null;
    }

    private static readonly ConcurrentDictionary<Type, string[]?> LinkSignatures = new();

    /// <summary>Record signatures a link can point to (from its target getter type).</summary>
    private static string[]? SignaturesFor(IFormLinkGetter link)
    {
        var target = link.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IFormLinkGetter<>))?.GetGenericArguments()[0];
        if (target is null) return null;
        return LinkSignatures.GetOrAdd(target, static t =>
        {
            var list = StbEditor.Data.Catalog.RecordTypes.Signatures
                .Where(sig => StbEditor.Data.RecordLookup.GetterType(sig) is { } g && t.IsAssignableFrom(g)).ToArray();
            return list.Length == 0 || list.Length > 40 ? null : list;
        });
    }

    private static bool IsReferenceLink(IFormLinkGetter link) =>
        link.GetType().GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IFormLinkGetter<>)
            && i.GetGenericArguments()[0].Name is "IPlacedGetter" or "IPlacedObjectGetter" or "IPlacedNpcGetter" or "ILinkedReferenceGetter" or "IPlacedSimpleGetter");

    private static void Leaf(string label) =>
        ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.Bullet);

    private static void DrawLeafValue(EditorApp app, object? value)
    {
        switch (value)
        {
            case null:
                ImGui.TextColored(Theme.TextDim, "—");
                break;

            case IFormLinkGetter link:
                DrawLink(app, link.FormKeyNullable);
                break;

            case FormKey key:
                DrawLink(app, key);
                break;

            case ITranslatedStringGetter translated:
                ImGui.TextWrapped(translated.String ?? string.Empty);
                break;

            case System.Drawing.Color color:
                var swatch = new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, 1f);
                ImGui.ColorButton("##c", swatch, ImGuiColorEditFlags.NoTooltip, new Vector2(ImGui.GetFontSize() * 1.6f, ImGui.GetFontSize()));
                ImGui.SameLine();
                ImGui.Text($"{color.R}, {color.G}, {color.B}, {color.A}");
                break;

            case float f:
                ImGui.Text(f.ToString("0.######"));
                break;

            case bool b:
                ImGui.TextColored(b ? Theme.Good : Theme.TextDim, b ? "true" : "false");
                break;

            case Enum e:
                ImGui.Text(e.ToString());
                ImGui.SameLine();
                ImGui.TextColored(Theme.TextDim, TF($"(0x{Convert.ToUInt64(e):X})"));
                break;

            case string s:
                ImGui.TextWrapped(s);
                break;

            default:
                if (TryBytes(value, out var bytes))
                {
                    ImGui.PushFont(app.Ui.MonoFont, 0);
                    ImGui.TextWrapped(Hex(bytes));
                    ImGui.PopFont();
                }
                else
                {
                    ImGui.TextWrapped(value.ToString() ?? string.Empty);
                }
                break;
        }
    }

    private static void DrawLink(EditorApp app, FormKey? key)
    {
        if (key is not { IsNull: false } k)
        {
            ImGui.TextColored(Theme.TextDim, "NULL");
            return;
        }

        var session = app.Session!;
        var record = session.Catalog.Get(k);
        var text = record is null
            ? TF($"{session.Workspace.FormIdText(k)}  (not in catalog)")
            : $"{record.EditorId ?? record.Name ?? "?"}  [{record.Signature}:{session.Workspace.FormIdText(k)}]";

        ImGui.PushStyleColor(ImGuiCol.Text, record is null ? Theme.TextDim : Theme.Accent);
        if (ImGui.Selectable(text, false)) app.Select(k);
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(TF($"{k}\nClick to open"));
    }

    private static bool IsComposite(object? value) => value switch
    {
        null or string => false,
        IFormLinkGetter or ITranslatedStringGetter => false,
        _ when TryBytes(value, out _) => false,
        IEnumerable => true,
        ILoquiObject => true,
        _ => false,
    };

    private static string Summary(EditorApp app, object value)
    {
        switch (value)
        {
            case IMajorRecordGetter record:
                return $"{record.EditorID ?? ""} [{app.Session!.Workspace.FormIdText(record.FormKey)}]";

            case IEnumerable list:
                var count = list switch
                {
                    ICollection c => c.Count,
                    IReadOnlyCollection<object> c => c.Count,
                    _ => CountOf(value),
                };
                return count >= 0 ? $"[{count}]" : "[…]";

            default:
                return InlineSummary(value) ?? value.GetType().Name.Replace("BinaryOverlay", "");
        }
    }

    /// <summary>
    /// Small structures (bounds, coordinates, a condition's data) read better inline:
    /// "First = (-20, -20, 0), Second = (20, 20, 40)". Larger ones just show their type.
    /// </summary>
    private static string? InlineSummary(object value)
    {
        var properties = PropertiesOf(value);
        if (properties.Length is 0 or > 4) return null;

        var parts = new List<string>(properties.Length);
        foreach (var p in properties)
        {
            object? v;
            try { v = p.GetValue(value); }
            catch { return null; }

            if (IsComposite(v)) return null;
            parts.Add($"{p.Name} = {v switch { null => "—", float f => f.ToString("0.###"), ITranslatedStringGetter t => t.String, _ => v.ToString() }}");
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
                return (int)iface.GetProperty(T("Count"))!.GetValue(value)!;
        }
        return -1;
    }

    private static bool TryBytes(object? value, out ReadOnlySpan<byte> bytes)
    {
        switch (value)
        {
            case byte[] array:
                bytes = array;
                return true;
            case Noggog.ReadOnlyMemorySlice<byte> slice:
                bytes = slice.Span;
                return true;
            case Noggog.MemorySlice<byte> mutable:
                bytes = mutable.Span;
                return true;
            default:
                bytes = default;
                return false;
        }
    }

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        const int max = 64;
        var text = Convert.ToHexString(bytes.Length > max ? bytes[..max] : bytes);
        var spaced = string.Join(' ', Enumerable.Range(0, text.Length / 2).Select(i => text.Substring(i * 2, 2)));
        return bytes.Length > max ? TF($"{spaced} … ({bytes.Length} bytes)") : TF($"{spaced}  ({bytes.Length} bytes)");
    }

    /// <summary>
    /// Data members of an object, taken from its Loqui getter interface: header fields first,
    /// then the record's own fields in file order, then members from aspect interfaces.
    /// </summary>
    private static PropertyInfo[] PropertiesOf(object obj)
    {
        var getterType = obj is ILoquiObject loqui ? loqui.Registration.GetterType : obj.GetType();

        return PropertyCache.GetOrAdd(getterType, static type =>
        {
            var seen = new HashSet<string>();
            var result = new List<PropertyInfo>();

            void Add(IEnumerable<PropertyInfo> properties)
            {
                foreach (var p in properties)
                {
                    if (p.GetIndexParameters().Length > 0 || Hidden.Contains(p.Name)) continue;
                    if (seen.Add(p.Name)) result.Add(p);
                }
            }

            var all = type.IsInterface
                ? type.GetInterfaces().Prepend(type).SelectMany(i => i.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)).ToList()
                : type.GetProperties(BindingFlags.Public | BindingFlags.Instance).ToList();

            foreach (var name in HeaderOrder) Add(all.Where(p => p.Name == name).Take(1));
            Add(type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
            Add(all);

            return [.. result];
        });
    }
}
