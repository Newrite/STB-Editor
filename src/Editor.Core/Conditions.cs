using System.Collections;
using System.Globalization;
using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace StbEditor.Core;

/// <summary>
/// Condition items (CTDA) as the Creation Kit edits them. In Mutagen the function is the class of
/// the condition's data (one class per function, holding its parameters), "use global" is the
/// condition's own class (<see cref="ConditionGlobal"/> instead of <see cref="ConditionFloat"/>),
/// OR and swap subject/target are flags, and "parameters are aliases / package data" live on the data.
/// </summary>
public static class Conditions
{
    private static readonly Assembly Skyrim = typeof(Condition).Assembly;

    /// <summary>Members every data class has (run on, reference…) — not parameters of the function.</summary>
    private static readonly HashSet<string> Common = ["Function", "RunOnType", "Reference", "RunOnTypeIndex", "UseAliases", "UsePackageData", "StaticRegistration"];

    /// <summary>Every function that has a data class, by name (GetVATSValue's sub-kinds are not offered).</summary>
    public static IReadOnlyList<Condition.Function> Functions { get; } = Enum.GetValues<Condition.Function>()
        .Where(f => DataClass(f) is not null)
        .OrderBy(f => f.ToString(), StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static Type? DataClass(Condition.Function function) => Skyrim.GetType($"Mutagen.Bethesda.Skyrim.{function}ConditionData");

    /// <summary>The function a data object stands for (from its class; read-only overlays included).</summary>
    public static string FunctionName(IConditionDataGetter data)
    {
        var name = data.GetType().Name;
        if (name.EndsWith("BinaryOverlay", StringComparison.Ordinal)) name = name[..^"BinaryOverlay".Length];
        if (name.EndsWith("ConditionData", StringComparison.Ordinal)) name = name[..^"ConditionData".Length];
        return name.StartsWith("GetVATSValue", StringComparison.Ordinal) ? "GetVATSValue" : name;
    }

    public static Condition.Function? FunctionOf(IConditionDataGetter data) =>
        Enum.TryParse<Condition.Function>(FunctionName(data), out var f) ? f : null;

    /// <summary>The function's own parameters (first, second), without the unused placeholders.</summary>
    public static IReadOnlyList<PropertyInfo> Parameters(IConditionDataGetter data) =>
        RecordFields.PropertiesOf(data).Where(p => !Common.Contains(p.Name) && !p.Name.Contains("Unused", StringComparison.Ordinal)).ToList();

    /// <summary>Some parameter can be a quest alias / package data index instead of a form.</summary>
    public static bool HasFormParameter(IConditionDataGetter data) =>
        Parameters(data).Any(p => IsLinkOrIndex(p.PropertyType));

    public static bool IsLinkOrIndex(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition().Name.StartsWith("IFormLinkOrIndex", StringComparison.Ordinal)
        || type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition().Name.StartsWith("IFormLinkOrIndexGetter", StringComparison.Ordinal));

    // ------------------------------------------------------------------ text

    public static string Operator(CompareOperator op) => op switch
    {
        CompareOperator.EqualTo => "==",
        CompareOperator.NotEqualTo => "!=",
        CompareOperator.GreaterThan => ">",
        CompareOperator.GreaterThanOrEqualTo => ">=",
        CompareOperator.LessThan => "<",
        CompareOperator.LessThanOrEqualTo => "<=",
        _ => op.ToString(),
    };

    /// <summary>Run On as the CK lists it: Subject, Target, a reference, Combat Target, Linked Ref, an alias…</summary>
    public static string RunOn(IConditionDataGetter data, Func<FormKey?, string> name) => data.RunOnType switch
    {
        Condition.RunOnType.Subject => "Subject",
        Condition.RunOnType.Target => "Target",
        Condition.RunOnType.Reference => name(data.Reference.FormKeyNullable),
        Condition.RunOnType.CombatTarget => "Combat Target",
        Condition.RunOnType.LinkedReference => "Linked Ref",
        Condition.RunOnType.QuestAlias => $"Alias {data.RunOnTypeIndex}",
        Condition.RunOnType.PackageData => $"Pack Data {data.RunOnTypeIndex}",
        Condition.RunOnType.EventData => "Event Data",
        _ => data.RunOnType.ToString(),
    };

    /// <summary>A parameter's value for the condition line: a form's name, an alias or package data index, a number.</summary>
    public static string ParameterText(IConditionDataGetter data, PropertyInfo parameter, Func<FormKey?, string> name)
    {
        object? value;
        try { value = parameter.GetValue(data); } catch { return "?"; }
        switch (value)
        {
            case null: return "NONE";
            case IFormLinkGetter link: return name(link.FormKeyNullable);
            case string text: return $"\"{text}\"";
            case float f: return RecordFields.FloatText(f);
            case Enum e: return e.ToString();
        }
        if (IsLinkOrIndex(value.GetType()))
        {
            var index = value.GetType().GetProperty("Index")?.GetValue(value);
            var link = value.GetType().GetProperty("Link")?.GetValue(value) as IFormLinkGetter;
            if (data.UseAliases) return $"Alias {index}";
            if (data.UsePackageData) return $"Pack Data {index}";
            return name(link?.FormKeyNullable);
        }
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>The function with its parameters: GetIsID(Hulda), GetStage(MQ101), GetActorValue(Health).</summary>
    public static string Call(IConditionDataGetter data, Func<FormKey?, string> name)
    {
        var parameters = Parameters(data);
        var function = FunctionName(data);
        if (data is IGetEventDataConditionDataGetter ev) return $"{function}({ev.Function}, {ev.Member}, {name(ev.Record.FormKeyNullable)})";
        return parameters.Count == 0 ? function : $"{function}({string.Join(", ", parameters.Select(p => ParameterText(data, p, name)))})";
    }

    public static string Value(IConditionGetter condition, Func<FormKey?, string> name) => condition switch
    {
        IConditionFloatGetter f => RecordFields.FloatText(f.ComparisonValue),
        IConditionGlobalGetter g => name(g.ComparisonValue.FormKeyNullable),
        _ => "?",
    };

    /// <summary>One condition as a line: Subject.GetIsID(Hulda) == 1 AND.</summary>
    public static string Describe(IConditionGetter condition, Func<FormKey?, string> name)
    {
        var data = condition.Data;
        var swap = condition.Flags.HasFlag(Condition.Flag.SwapSubjectAndTarget) ? " [swap]" : "";
        var or = condition.Flags.HasFlag(Condition.Flag.OR) ? "OR" : "AND";
        return $"{RunOn(data, name)}.{Call(data, name)} {Operator(condition.CompareOperator)} {Value(condition, name)}{swap} {or}";
    }

    // ------------------------------------------------------------------ editing (mutable copies)

    /// <summary>A new condition as the CK starts one: Subject.GetIsID(NONE) == 1.</summary>
    public static Condition New()
    {
        var condition = new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1 };
        condition.Data = NewData(Condition.Function.GetIsID);
        return condition;
    }

    private static ConditionData NewData(Condition.Function function)
    {
        var type = DataClass(function) ?? throw new InvalidOperationException($"{function}: no data class");
        var data = (ConditionData)System.Activator.CreateInstance(type)!;
        RecordFields.FillRequired(data);
        return data;
    }

    /// <summary>
    /// Changes the function: new data of that function's class; run on, its reference/index and the
    /// alias / package data switches stay, the parameters start empty (the CK does the same).
    /// </summary>
    public static void SetFunction(Condition condition, Condition.Function function)
    {
        var old = condition.Data;
        var data = NewData(function);
        data.RunOnType = old.RunOnType;
        data.Reference.SetTo(old.Reference.FormKeyNullable ?? FormKey.Null);
        data.RunOnTypeIndex = old.RunOnTypeIndex;
        data.UseAliases = old.UseAliases;
        data.UsePackageData = old.UsePackageData;
        condition.Data = data;
    }

    /// <summary>
    /// Use Global on or off: the condition becomes the other class (compare value a global variable or
    /// a number); operator, flags and the function with its parameters stay.
    /// </summary>
    public static Condition WithGlobal(Condition condition, bool global)
    {
        if (global == condition is ConditionGlobal) return condition;
        Condition result = global
            ? new ConditionGlobal()
            : new ConditionFloat { ComparisonValue = 1 };
        result.CompareOperator = condition.CompareOperator;
        result.Flags = condition.Flags;
        result.Unknown1 = condition.Unknown1;
        result.Unknown2 = condition.Unknown2;
        result.Data = condition.Data;
        return result;
    }

    /// <summary>Copies (for the condition clipboard): independent of the record they came from.</summary>
    public static List<Condition> Copy(IEnumerable<IConditionGetter> conditions) => conditions.Select(c => c.DeepCopy()).ToList();

    /// <summary>The conditions list at a path of a mutable copy (created when it is missing).</summary>
    public static IList ListAt(object root, IReadOnlyList<object> path)
    {
        RecordFields.Unbreak(root, path);
        if (RecordFields.Get(root, path) is IList list) return list;
        RecordFields.Create(root, path);
        return RecordFields.Get(root, path) as IList ?? throw new InvalidOperationException("not a conditions list");
    }

    /// <summary>A list whose items are conditions (any record's Conditions, perk entries, quest aliases…).</summary>
    public static bool IsConditionList(object? value) =>
        value is IEnumerable and not string && value.GetType().GetInterfaces()
            .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>) && typeof(IConditionGetter).IsAssignableFrom(i.GetGenericArguments()[0]));
}
