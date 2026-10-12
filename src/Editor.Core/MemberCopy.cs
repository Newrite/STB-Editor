using System.Reflection;
using Mutagen.Bethesda.Plugins.Records;

namespace StbEditor.Core;

/// <summary>
/// One field of a record copied from another version of it (xEdit: drag a value across columns, or "Copy
/// to" a field): the whole member — a number, a link, a structure, a list — through Mutagen's own deep copy
/// with a translation mask that selects only that member.
/// </summary>
public static class MemberCopy
{
    /// <summary>Copies <paramref name="member"/> of <paramref name="source"/> onto <paramref name="target"/> (a mutable copy of the same type).</summary>
    public static void Copy(IMajorRecord target, IMajorRecordGetter source, string member)
    {
        var cls = target.GetType();
        var maskType = MaskType(cls) ?? throw new InvalidOperationException($"{cls.Name}: no translation mask");
        var mask = System.Activator.CreateInstance(maskType, false, true)!;
        var field = AllFields(maskType).FirstOrDefault(f => f.Name == member) ?? throw new InvalidOperationException($"{cls.Name}.{member}: not a copyable field");
        // Every other field explicitly off: a null sub-mask (male/female pairs) would mean "copy it".
        foreach (var other in AllFields(maskType))
            other.SetValue(mask, other == field ? Mask(other.FieldType, true) : Mask(other.FieldType, false));
        var deepCopyIn = DeepCopyIn(cls, maskType) ?? throw new InvalidOperationException($"{cls.Name}: no deep copy");
        var crystal = maskType.GetMethod("GetCrystal", Type.EmptyTypes)!.Invoke(mask, null);
        // Mutagen copies some male/female pairs (a race's skeleton) whatever the mask says: every other field is
        // put back from a snapshot if it changed.
        var snapshot = (IMajorRecord)target.DeepCopy();
        var before = Fingerprints(target, member);
        try
        {
            deepCopyIn.Invoke(null, [target, source, null, crystal]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
        foreach (var (name, print) in Fingerprints(target, member))
        {
            if (before.TryGetValue(name, out var was) && was == print) continue;
            var property = cls.GetProperty(name);
            if (property is { CanWrite: true }) property.SetValue(target, property.GetValue(snapshot));
        }
    }

    /// <summary>What each member other than <paramref name="except"/> holds, as text.</summary>
    private static Dictionary<string, string> Fingerprints(IMajorRecord record, string except) =>
        RecordFields.PropertiesOf(record).Where(p => p.Name != except && p.Name != "FormKey")
            .ToDictionary(p => p.Name, p =>
            {
                object? value;
                try { value = p.GetValue(record); } catch { return "?"; }
                return value is null ? "null" : RecordFields.IsComposite(value)
                    ? string.Join("|", RecordFields.Leaves(value, 16, int.MaxValue).Select(l => l.PathText + "=" + RecordFields.Repr(l.Value)))
                    : RecordFields.Repr(value);
            });

    /// <summary>Whether <see cref="Copy"/> can copy that member of records of this class.</summary>
    public static bool CanCopy(Type cls, string member) =>
        MaskType(cls) is { } maskType && AllFields(maskType).Any(f => f.Name == member) && DeepCopyIn(cls, maskType) is not null;

    private static Type? MaskType(Type cls) => cls.GetNestedType("TranslationMask", BindingFlags.Public);

    private static IEnumerable<FieldInfo> AllFields(Type type)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                yield return f;
    }

    /// <summary>A mask field all on or all off: a bool, a sub-mask, or a gendered pair of those.</summary>
    private static object Mask(Type fieldType, bool on)
    {
        var type = Nullable.GetUnderlyingType(fieldType) ?? fieldType;
        if (type == typeof(bool)) return on;
        if (type.IsGenericType && type.GetGenericTypeDefinition().Name.StartsWith("GenderedItem", StringComparison.Ordinal))
        {
            var inner = type.GetGenericArguments()[0];
            return System.Activator.CreateInstance(type, Mask(inner, on), Mask(inner, on))!;
        }
        // A sub-structure's mask: (bool defaultOn, bool onOverall).
        var ctor = type.GetConstructors().FirstOrDefault(c => c.GetParameters() is [{ ParameterType: var a }, { ParameterType: var b }] && a == typeof(bool) && b == typeof(bool));
        if (ctor is not null) return ctor.Invoke([on, on]);
        return System.Activator.CreateInstance(type, on)!;
    }

    private static readonly Dictionary<Type, MethodInfo?> DeepCopyIns = new();

    /// <summary>The generated DeepCopyIn(this IXInternal, IXGetter, ErrorMaskBuilder?, TranslationCrystal?) of the record class.</summary>
    private static MethodInfo? DeepCopyIn(Type cls, Type maskType)
    {
        lock (DeepCopyIns)
        {
            if (DeepCopyIns.TryGetValue(cls, out var cached)) return cached;
            var method = cls.Assembly.GetTypes()
                .Where(t => t.IsAbstract && t.IsSealed && t.Name == cls.Name + "MixIn")
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .FirstOrDefault(m => m.Name == "DeepCopyIn" && m.GetParameters() is [var item, var rhs, var errors, var crystal]
                    && item.ParameterType.IsAssignableFrom(cls) && rhs.ParameterType.IsInterface
                    && errors.ParameterType.Name == "ErrorMaskBuilder" && crystal.ParameterType.Name.StartsWith("TranslationCrystal", StringComparison.Ordinal));
            return DeepCopyIns[cls] = method;
        }
    }
}
