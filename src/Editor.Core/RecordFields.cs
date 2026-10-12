using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Loqui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Strings;
using Noggog;

namespace StbEditor.Core;

/// <summary>What editor a field gets.</summary>
public enum FieldKind
{
    None, String, Translated, Bool, Integer, Float, Double, Enum, Flags, P3Float, P2Float, P3Int16, P2Int, Color, Percent, Link, Bytes,
}

/// <summary>One leaf of a record: where it is (path of property names / list indices), its value and editor.</summary>
public sealed record FieldLeaf(IReadOnlyList<object> Path, object? Value, Type? DeclaredType, FieldKind Kind, bool Editable)
{
    public string PathText => string.Join('/', Path);
}

/// <summary>
/// Generic record field access over Mutagen's Loqui types, without UI: which members a record shows,
/// which are editable and with what, and setting a value at a path on a mutable working copy. The
/// Inspector's field tree draws with it; <c>stbedit-cli formtest</c> edits every field of every
/// record type through it and checks the plugin reads back the same.
/// </summary>
public static class RecordFields
{
    /// <summary>Members of the Loqui/Mutagen plumbing, or type metadata — not record data.</summary>
    private static readonly HashSet<string> Hidden =
    [
        "Registration", "StaticRegistration", "BinaryWriteTranslator", "CommonInstance", "CommonSetterInstance",
        "CommonSetterTranslationInstance", "EqualityContract", "GameRelease",
    ];

    /// <summary>Record header fields go first, in this order.</summary>
    private static readonly string[] HeaderOrder =
        ["FormKey", "EditorID", "MajorRecordFlagsRaw", "IsDeleted", "IsCompressed", "FormVersion", "VersionControl", "Version2"];

    /// <summary>Shown but never edited: identity and format bookkeeping the writer owns.</summary>
    private static readonly HashSet<string> ReadOnlyNames =
        ["FormKey", "FormVersion", "VersionControl", "Version2", "IsCompressed", "IsDeleted", "SkyrimMajorRecordFlags", "MajorRecordFlagsRaw"];

    /// <summary>
    /// Record header bookkeeping (form version, version control, raw flags, compression): the
    /// editor and the writer own it, so the views hide it unless asked. Typed MajorFlags stay.
    /// </summary>
    public static bool IsRecordHeader(string name) =>
        name is "FormKey" or "MajorRecordFlagsRaw" or "SkyrimMajorRecordFlags" or "IsDeleted" or "IsCompressed"
            or "FormVersion" or "VersionControl" or "Version2";

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    /// <summary>
    /// Data members of an object from its Loqui getter interface: header fields first, then the
    /// type's own fields in file order, then members of aspect interfaces.
    /// </summary>
    public static PropertyInfo[] PropertiesOf(object obj)
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
                    if (p.GetIndexParameters().Length > 0 || IsInternal(p)) continue;
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

    /// <summary>
    /// Mutagen bookkeeping rather than record data: which parts of a struct were present
    /// (*DataTypeState, "Break" markers), how to export (Exporting*), format markers (ActsLike44).
    /// </summary>
    public static bool IsInternal(PropertyInfo p) =>
        Hidden.Contains(p.Name) || p.PropertyType == typeof(Type) || IsBreakState(p)
        || p.Name.StartsWith("Exporting", StringComparison.Ordinal) || p.Name == "ActsLike44";

    /// <summary>"Which parts of the struct were present" markers: *DataTypeState and Versioning enums with Break flags.</summary>
    private static bool IsBreakState(PropertyInfo p) =>
        p.Name.EndsWith("DataTypeState", StringComparison.Ordinal)
        || (p.Name == "Versioning" && p.PropertyType.IsEnum && Enum.GetNames(p.PropertyType).Any(n => n.StartsWith("Break", StringComparison.Ordinal)));

    /// <summary>Male/female pairs (GenderedItem&lt;T&gt;) are shown as their two members, not as a list.</summary>
    public static bool IsGendered(object? value) =>
        value is not null && value.GetType().GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition().Name.StartsWith("IGenderedItemGetter", StringComparison.Ordinal));

    /// <summary>The member on the mutable class behind a getter (getter interfaces have no setters).</summary>
    public static PropertyInfo? ClassProperty(object owner, string name)
    {
        var classType = owner is ILoquiObject loqui ? loqui.Registration.ClassType : owner.GetType();
        return classType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
    }

    public static bool IsComposite(object? value) => value switch
    {
        null or string => false,
        IFormLinkGetter or ITranslatedStringGetter => false,
        _ when IsBytes(value) => false,
        IEnumerable => true,
        ILoquiObject => true,
        _ when IsGendered(value) => true,
        _ => false,
    };

    public static bool IsBytes(object? value) => value is byte[] or ReadOnlyMemorySlice<byte> or MemorySlice<byte>;

    /// <summary>Editor kind for a value, or for its declared type when the value is null.</summary>
    public static FieldKind KindOf(object? value, Type? declared)
    {
        if (value is IFormLinkGetter) return FieldKind.Link;
        if (value is not null && IsBytes(value)) return FieldKind.Bytes;
        var type = value?.GetType() ?? declared;
        if (type is null) return FieldKind.None;
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (typeof(ITranslatedStringGetter).IsAssignableFrom(type)) return FieldKind.Translated;
        if (typeof(IFormLinkGetter).IsAssignableFrom(type)) return FieldKind.Link;
        if (type == typeof(string)) return FieldKind.String;
        if (type == typeof(bool)) return FieldKind.Bool;
        if (type == typeof(float)) return FieldKind.Float;
        if (type == typeof(double)) return FieldKind.Double;
        if (type.IsEnum) return type.IsDefined(typeof(FlagsAttribute), false) ? FieldKind.Flags : FieldKind.Enum;
        if (type == typeof(int) || type == typeof(uint) || type == typeof(short) || type == typeof(ushort) || type == typeof(byte)
            || type == typeof(sbyte) || type == typeof(long) || type == typeof(ulong)) return FieldKind.Integer;
        if (type == typeof(P3Float)) return FieldKind.P3Float;
        if (type == typeof(P2Float)) return FieldKind.P2Float;
        if (type == typeof(P3Int16)) return FieldKind.P3Int16;
        if (type == typeof(P2Int)) return FieldKind.P2Int;
        if (type == typeof(System.Drawing.Color)) return FieldKind.Color;
        if (type == typeof(Percent)) return FieldKind.Percent;
        return FieldKind.None;
    }

    /// <summary>Whether the field at <paramref name="name"/> of <paramref name="owner"/> can be edited, and its declared type.</summary>
    public static (bool Editable, Type? Declared) Editability(object owner, string name, object? value)
    {
        if (ReadOnlyNames.Contains(name)) return (false, null);
        // Format of the script data (VMAD version / object format): Mutagen writes only the one it read.
        if (owner is Mutagen.Bethesda.Skyrim.IAVirtualMachineAdapterGetter or Mutagen.Bethesda.Skyrim.IQuestFragmentAliasGetter && name is "Version" or "ObjectFormat") return (false, null);
        var property = ClassProperty(owner, name);
        if (property is null) return (false, null);
        var kind = KindOf(value, property.PropertyType);
        if (kind is FieldKind.None or FieldKind.Bytes) return (false, property.PropertyType);
        if (kind == FieldKind.Link) return (value is not null, property.PropertyType); // set through the link object
        return (property.CanWrite && property.SetMethod?.IsPublic == true, property.PropertyType);
    }

    /// <summary>Key → value maps (an NPC's skill values): edited per key, not as a list.</summary>
    /// <summary>A list rows can be added to (arrays such as a weather's four cloud layers cannot).</summary>
    public static bool CanGrow(object? list) => list is IList { IsFixedSize: false, IsReadOnly: false } || (list is not IList && list is IEnumerable and not string);

    public static bool IsDictionary(object? value) =>
        value is IDictionary || (value is not null && value.GetType().GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

    /// <summary>(key, value) pairs of a map, read without knowing its generic types.</summary>
    public static IEnumerable<(object Key, object? Value)> Entries(object map)
    {
        foreach (var entry in (IEnumerable)map)
        {
            var type = entry!.GetType();
            yield return (type.GetProperty("Key")!.GetValue(entry)!, type.GetProperty("Value")!.GetValue(entry));
        }
    }

    public static Type? ValueType(object map) =>
        map.GetType().GetInterfaces().FirstOrDefault(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IDictionary<,>)))?.GetGenericArguments()[1];

    /// <summary>The element type of a mutable list (for adding items and editing leaf items).</summary>
    public static Type? ElementType(object list) =>
        list.GetType().GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>))?.GetGenericArguments()[0]
        ?? list.GetType().GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))?.GetGenericArguments()[0];

    /// <summary>A class that can be created for an element / member type (Loqui getter interface → its class).</summary>
    public static Type? Creatable(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (!type.IsInterface && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is not null) return type;
        // Mutagen getter interface IFooGetter / IFoo → class Foo in the same namespace and assembly.
        if (type.IsInterface && type.Name.StartsWith('I'))
        {
            var name = type.Name[1..];
            if (name.EndsWith("Getter", StringComparison.Ordinal)) name = name[..^6];
            var cls = type.Assembly.GetType($"{type.Namespace}.{name}");
            // Generic getters (IGenderedItemGetter<T>, IAssetLinkGetter<T>) map to the class closed over the same arguments.
            if (cls is { IsGenericTypeDefinition: true } && type.IsGenericType && cls.GetGenericArguments().Length == type.GetGenericArguments().Length)
                cls = cls.MakeGenericType(type.GetGenericArguments());
            if (cls is { IsAbstract: false, ContainsGenericParameters: false } && type.IsAssignableFrom(cls) && cls.GetConstructor(Type.EmptyTypes) is not null) return cls;
        }
        // IFormLinkGetter<T> lists hold FormLink<T>.
        if (type.IsGenericType && type.GetGenericArguments() is [var t] && typeof(IFormLinkGetter).IsAssignableFrom(type))
            return typeof(FormLink<>).MakeGenericType(t);
        return null;
    }

    /// <summary>
    /// Concrete classes an abstract element can be (a condition: ConditionFloat / ConditionGlobal; a
    /// perk entry: ability, quest stage, entry point…), for «add» to offer.
    /// </summary>
    public static IReadOnlyList<Type> ConcreteTypes(Type element)
    {
        var type = Nullable.GetUnderlyingType(element) ?? element;
        Type? root = type;
        if (type.IsInterface && type.Name.StartsWith('I'))
        {
            var name = type.Name[1..];
            if (name.EndsWith("Getter", StringComparison.Ordinal)) name = name[..^6];
            root = type.Assembly.GetType($"{type.Namespace}.{name}");
        }
        if (root is null || root.ContainsGenericParameters) return [];
        if (!root.IsAbstract) return root.GetConstructor(Type.EmptyTypes) is null ? [] : [root];
        return ConcreteCache.GetOrAdd(root, static r => r.Assembly.GetTypes()
            .Where(t => !t.IsAbstract && r.IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.Name, StringComparer.Ordinal).ToArray());
    }

    private static readonly ConcurrentDictionary<Type, Type[]> ConcreteCache = new();

    /// <summary>The kind a new abstract part starts as: a condition's data is GetIsID (as the CK starts it), otherwise the first.</summary>
    public static Type? DefaultConcrete(Type type)
    {
        var kinds = ConcreteTypes(type);
        return kinds.FirstOrDefault(k => k.Name == "GetIsIDConditionData") ?? kinds.FirstOrDefault();
    }

    // ------------------------------------------------------------------ walking

    /// <summary>Every leaf of an object (lists and sub-records descended), up to <paramref name="maxDepth"/>.</summary>
    public static IEnumerable<FieldLeaf> Leaves(object root, int maxDepth = 12, int maxListItems = 64)
    {
        var path = new List<object>();
        return Walk(root, path, 0, maxDepth, maxListItems).ToList();
    }

    private static IEnumerable<FieldLeaf> Walk(object obj, List<object> path, int depth, int maxDepth, int maxItems)
    {
        if (IsDictionary(obj))
        {
            var valueType = ValueType(obj);
            foreach (var (key, value) in Entries(obj))
            {
                path.Add(key);
                var kind = KindOf(value, valueType);
                yield return new FieldLeaf([.. path], value, valueType, kind, kind is not (FieldKind.None or FieldKind.Bytes));
                path.RemoveAt(path.Count - 1);
            }
            yield break;
        }

        if (obj is IEnumerable list and not string && !IsGendered(obj))
        {
            var element = ElementType(obj);
            var index = 0;
            foreach (var item in list)
            {
                if (index >= maxItems) yield break;
                path.Add(index);
                if (IsComposite(item) && depth < maxDepth)
                {
                    foreach (var leaf in Walk(item!, path, depth + 1, maxDepth, maxItems)) yield return leaf;
                }
                else
                {
                    var kind = KindOf(item, element);
                    yield return new FieldLeaf([.. path], item, element, kind, kind is not (FieldKind.None or FieldKind.Bytes));
                }
                path.RemoveAt(path.Count - 1);
                index++;
            }
            yield break;
        }

        foreach (var property in PropertiesOf(obj))
        {
            object? value;
            try { value = property.GetValue(obj); }
            catch (TargetInvocationException) { continue; }

            path.Add(property.Name);
            if (IsComposite(value) && depth < maxDepth)
            {
                foreach (var leaf in Walk(value!, path, depth + 1, maxDepth, maxItems)) yield return leaf;
            }
            else
            {
                var (editable, declared) = Editability(obj, property.Name, value);
                yield return new FieldLeaf([.. path], value, declared ?? property.PropertyType, KindOf(value, declared ?? property.PropertyType), editable);
            }
            path.RemoveAt(path.Count - 1);
        }
    }

    // ------------------------------------------------------------------ editing a working copy

    /// <summary>Value at a path, or null when a step on the way is missing.</summary>
    public static object? Get(object root, IReadOnlyList<object> path)
    {
        object? current = root;
        foreach (var segment in path)
        {
            if (current is null) return null;
            current = Step(current, segment);
        }
        return current;
    }

    private static object? Step(object owner, object segment) => segment switch
    {
        _ when IsDictionary(owner) && segment is not string => Entries(owner).FirstOrDefault(e => Equals(e.Key, segment)).Value,
        int i when owner is IList list => i < list.Count ? list[i] : null,
        int i when owner is IEnumerable e => e.Cast<object?>().ElementAtOrDefault(i),
        // Read through the getter interface: works on mutable copies and on read-only overlays alike.
        string name => (PropertiesOf(owner).FirstOrDefault(p => p.Name == name) ?? ClassProperty(owner, name))?.GetValue(owner),
        _ => null,
    };

    /// <summary>
    /// Sets the field at <paramref name="path"/> of a mutable copy. Missing sub-records on the way
    /// are created (setting Lock/Level creates Lock). Links are set through their own SetTo.
    /// </summary>
    public static void Set(object root, IReadOnlyList<object> path, object? value)
    {
        var owner = Parent(root, path, create: true);
        var last = path[^1];
        Unbreak(root, path);

        if (Step(owner, last) is IFormLinkGetter link && value is null or FormKey)
        {
            SetLink(link, (FormKey?)value);
            return;
        }

        if (owner is IDictionary map && last is not string)
        {
            map[last] = Coerce(value, ValueType(owner));
            return;
        }

        switch (last)
        {
            case int i when owner is IList list:
                list[i] = Coerce(value, ElementType(list));
                break;
            case string name:
                var property = ClassProperty(owner, name) ?? throw new InvalidOperationException($"{name}: no such field");
                if (!property.CanWrite) throw new InvalidOperationException($"{name}: read-only");
                property.SetValue(owner, Coerce(value, property.PropertyType));
                break;
            default:
                throw new InvalidOperationException($"bad path segment {last}");
        }
    }

    /// <summary>
    /// Records read from older or hand-made plugins can carry a short data struct (Mutagen marks
    /// where it ended with "Break" flags in *DataTypeState) and Mutagen then writes only up to that
    /// point — an edited field past it would be lost. The Creation Kit writes the full struct; so
    /// does an edit here: every break on the way to the field is cleared.
    /// </summary>
    public static void Unbreak(object root, IReadOnlyList<object> path)
    {
        object? current = root;
        for (var i = 0; i <= path.Count && current is not null; i++)
        {
            foreach (var state in current.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => IsBreakState(p) && p.PropertyType.IsEnum && p.CanWrite))
            {
                var raw = unchecked((ulong)Convert.ToInt64(state.GetValue(current), CultureInfo.InvariantCulture));
                ulong breaks = 0;
                foreach (var name in Enum.GetNames(state.PropertyType))
                    if (name.StartsWith("Break", StringComparison.Ordinal))
                        breaks |= unchecked((ulong)Convert.ToInt64(Enum.Parse(state.PropertyType, name), CultureInfo.InvariantCulture));
                if ((raw & breaks) != 0) state.SetValue(current, Enum.ToObject(state.PropertyType, unchecked((long)(raw & ~breaks))));
            }
            if (i < path.Count) current = Step(current, path[i]);
        }
    }

    /// <summary>The object that holds the last segment, creating missing sub-records when asked.</summary>
    public static object Parent(object root, IReadOnlyList<object> path, bool create)
    {
        var current = root;
        for (var i = 0; i < path.Count - 1; i++)
        {
            var next = Step(current, path[i]);
            if (next is null && create && path[i] is string name && ClassProperty(current, name) is { CanWrite: true } property
                && Creatable(property.PropertyType) is { } type)
            {
                next = Activator.CreateInstance(type)!;
                property.SetValue(current, next);
            }
            current = next ?? throw new InvalidOperationException($"{path[i]} is empty");
        }
        return current;
    }

    /// <summary>Creates the (null) sub-record at <paramref name="path"/>.</summary>
    public static void Create(object root, IReadOnlyList<object> path)
    {
        var owner = Parent(root, path, create: true);
        if (path[^1] is not string name || ClassProperty(owner, name) is not { CanWrite: true } property)
            throw new InvalidOperationException("cannot create here");
        var type = Creatable(property.PropertyType) ?? throw new InvalidOperationException($"cannot create {property.PropertyType.Name}");
        var created = Activator.CreateInstance(type)!;
        FillRequired(created);
        property.SetValue(owner, created);
    }

    /// <summary>
    /// A freshly created sub-record gets its required (non-nullable) sub-structures: a new inventory
    /// entry needs its Item part, or Mutagen fails copying it.
    /// </summary>
    public static void FillRequired(object obj, int depth = 0, bool includeOptional = false)
    {
        if (depth > 6) return;
        var nullability = new NullabilityInfoContext();
        foreach (var property in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || property.SetMethod?.IsPublic != true || property.GetIndexParameters().Length > 0) continue;
            if (property.PropertyType.IsValueType || KindOf(null, property.PropertyType) != FieldKind.None) continue;
            if (property.GetValue(obj) is not null) continue;
            if (!includeOptional && nullability.Create(property).WriteState == NullabilityState.Nullable) continue;
            var type = Creatable(property.PropertyType) ?? DefaultConcrete(property.PropertyType);
            if (type is null) continue;
            var value = Activator.CreateInstance(type)!;
            FillRequired(value, depth + 1);
            property.SetValue(obj, value);
        }
    }

    /// <summary>Sets a nullable member back to null (an optional sub-record or subrecord).</summary>
    public static void Clear(object root, IReadOnlyList<object> path)
    {
        var owner = Parent(root, path, create: false);
        if (path[^1] is not string name || ClassProperty(owner, name) is not { CanWrite: true } property)
            throw new InvalidOperationException("cannot clear here");
        property.SetValue(owner, null);
    }

    /// <summary>
    /// Appends a new item (a default value, empty sub-record or null link) to the list at
    /// <paramref name="path"/>; <paramref name="concrete"/> picks the class for abstract elements.
    /// </summary>
    public static void AddItem(object root, IReadOnlyList<object> path, Type? concrete = null)
    {
        if (Get(root, path) is not IList list)
        {
            // A null optional list (ExtendedList?) is created first.
            var owner = Parent(root, path, create: true);
            if (path[^1] is string name && ClassProperty(owner, name) is { CanWrite: true } property && Creatable(property.PropertyType) is { } listType)
            {
                property.SetValue(owner, Activator.CreateInstance(listType));
                list = (IList)property.GetValue(owner)!;
            }
            else throw new InvalidOperationException("not a list");
        }
        if (list.IsFixedSize) throw new InvalidOperationException("this list has a fixed number of items");
        var element = ElementType(list) ?? throw new InvalidOperationException("unknown item type");
        object? item = concrete is not null ? Activator.CreateInstance(concrete)
            : element.IsValueType ? Activator.CreateInstance(element)
            : element == typeof(string) ? string.Empty
            : typeof(ITranslatedStringGetter).IsAssignableFrom(element) ? new TranslatedString(TranslatedString.DefaultLanguage, string.Empty)
            : Creatable(element) is { } type ? Activator.CreateInstance(type)
            : DefaultConcrete(element) is { } first ? Activator.CreateInstance(first)
            : throw new InvalidOperationException($"cannot create {element.Name}");
        // A new row gets all of its own parts (a leveled entry's data is optional to Mutagen, needed by the game).
        if (item is not null) FillRequired(item, includeOptional: true);
        list.Add(item);
    }

    public static void RemoveItem(object root, IReadOnlyList<object> path, int index)
    {
        if (Get(root, path) is not IList list || index < 0 || index >= list.Count) throw new InvalidOperationException("no such item");
        list.RemoveAt(index);
    }

    private static void SetLink(IFormLinkGetter link, FormKey? value)
    {
        var methods = link.GetType().GetMethods().Where(m => m.Name == "SetTo" && m.GetParameters().Length == 1).ToList();
        var nullable = methods.FirstOrDefault(m => m.GetParameters()[0].ParameterType == typeof(FormKey?));
        var plain = methods.FirstOrDefault(m => m.GetParameters()[0].ParameterType == typeof(FormKey));
        if (nullable is not null) { nullable.Invoke(link, [value]); return; }
        if (value is { } key && plain is not null) { plain.Invoke(link, [key]); return; }
        if (value is null && link.GetType().GetMethod("Clear", Type.EmptyTypes) is { } clear) { clear.Invoke(link, null); return; }
        if (value is null && plain is not null) { plain.Invoke(link, [FormKey.Null]); return; }
        throw new InvalidOperationException("this link cannot be set");
    }

    /// <summary>A value converted to the member's type (numbers widened/narrowed, strings to translated strings).</summary>
    public static object? Coerce(object? value, Type? target)
    {
        if (value is null || target is null) return value;
        var type = Nullable.GetUnderlyingType(target) ?? target;
        if (type.IsInstanceOfType(value)) return value;
        if (value is string s && typeof(ITranslatedStringGetter).IsAssignableFrom(type))
            return new TranslatedString(TranslatedString.DefaultLanguage, s);
        if (type.IsEnum) return Enum.ToObject(type, Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture));
        if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(type)) return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        return value;
    }

    // ------------------------------------------------------------------ number text

    /// <summary>Comparable text of a value (floats exactly, links by FormKey).</summary>
    public static string Repr(object? value) => value switch
    {
        null => "null",
        float f => FloatText(f),
        double d => DoubleText(d),
        ITranslatedStringGetter t => t.String ?? "null",
        IFormLinkGetter l => l.FormKeyNullable?.ToString() ?? "null",
        P3Float p => $"{FloatText(p.X)},{FloatText(p.Y)},{FloatText(p.Z)}",
        P2Float p => $"{FloatText(p.X)},{FloatText(p.Y)}",
        System.Drawing.Color c => c.ToArgb().ToString("X8"),
        Percent p => p.Value.ToString("R", CultureInfo.InvariantCulture),
        Enum e => $"{e} ({Convert.ToDecimal(e, CultureInfo.InvariantCulture)})",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };


    /// <summary>
    /// A float as text with every digit it has (round-trip), not rounded to two or four places —
    /// the Creation Kit shows perk entry values as "0.00" and loses anything finer.
    /// </summary>
    public static string FloatText(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string DoubleText(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Parses "0,0005", "0.0005", "1e-4" (either decimal separator).</summary>
    public static bool TryParseNumber(string text, out double value)
    {
        text = text.Trim().Replace(',', '.');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryParseInteger(string text, out long value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
