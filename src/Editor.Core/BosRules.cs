using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;

namespace StbEditor.Core;

/// <summary>
/// One line of a Base Object Swapper config (<c>_SWAP.ini</c>): in <c>[Forms]</c> placed copies of the original bases get the
/// swap base instead, in <c>[References]</c> the named references do, <c>[Transforms]</c> only move / turn / scale. Fields:
/// <c>originals|swaps|properties|chance</c> (Transforms: <c>originals|properties|chance</c>); several originals or swaps are
/// separated by commas (BOS picks one swap at random). A section name with "|…" after it carries conditions (location,
/// keyword…) the editor does not check.
/// </summary>
public sealed record BosRule(FrameworkRef Source, bool References, IReadOnlyList<FormKey> Originals, IReadOnlyList<FormKey> Swaps,
    string Properties, float Chance, bool Conditional)
{
    /// <summary>Only moves / turns / scales (Transforms), no other base.</summary>
    public bool TransformOnly => Swaps.Count == 0;

    /// <summary>Not certain to happen in the game: a chance below 100 %, a random pick among several swaps, or conditions.</summary>
    public bool Uncertain => Chance < 100 || Swaps.Count > 1 || Conditional;
}

public static class BosRules
{
    /// <summary>The BOS lines of the index as rules, by reference (References) and by base (Forms, Transforms by base).</summary>
    public static (Dictionary<FormKey, BosRule> ByReference, Dictionary<FormKey, BosRule> ByBase) Build(FrameworkIndex index)
    {
        var byReference = new Dictionary<FormKey, BosRule>();
        var byBase = new Dictionary<FormKey, BosRule>();
        foreach (var line in index.All.Where(r => r.Framework == "BOS").GroupBy(r => (r.File, r.Line)))
        {
            var first = line.First();
            var section = first.Kind;
            var name = section.Split('|')[0].Trim();
            var transforms = name.Equals("Transforms", StringComparison.OrdinalIgnoreCase);
            var references = name.Equals("References", StringComparison.OrdinalIgnoreCase);
            var originals = line.Where(r => r.Role is "swapped out" or "transformed").Select(r => r.Form).Distinct().ToList();
            var swaps = transforms ? [] : line.Where(r => r.Role == "swapped in").Select(r => r.Form).Distinct().ToList();
            var fields = first.Fields;
            string Field(int i) => i < fields.Count ? fields[i].Trim() : "";
            var properties = transforms ? Field(1) : Field(2);
            var chance = ChanceOf(transforms ? Field(2) : Field(3));
            var rule = new BosRule(first, references, originals, swaps, properties.Equals("NONE", StringComparison.OrdinalIgnoreCase) ? "" : properties,
                chance, section.Contains('|'));
            // The first line for a form wins here (BOS: later lines of a random set compete; the first is shown).
            foreach (var original in originals)
            {
                // Transforms in [Transforms] may name references or bases: both lookups get them.
                if (references || transforms) byReference.TryAdd(original, rule);
                if (!references) byBase.TryAdd(original, rule);
            }
        }
        return (byReference, byBase);
    }

    /// <summary>"chanceR(33)", "chance(50)", "50": percent; empty or NONE: 100.</summary>
    public static float ChanceOf(string text)
    {
        if (text.Length == 0 || text.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return 100;
        var m = Regex.Match(text, @"(-?\d+(?:\.\d+)?)");
        return m.Success && float.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 100) : 100;
    }

    private static readonly Regex Property = new(@"(posA|pos|rotA|rot|scaleA|scale)\s*\(([^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// A reference's place after the line's properties: <c>posA(x,y,z)</c> / <c>rotA(x,y,z)</c> (degrees) set it, <c>pos</c> /
    /// <c>rot</c> add to it, <c>scale(s)</c> sets the scale. A range "a/b" counts as its middle (BOS rolls a value in it).
    /// </summary>
    public static (Vector3 Position, Vector3 Rotation, float Scale) Apply(string properties, Vector3 position, Vector3 rotation, float scale)
    {
        foreach (Match m in Property.Matches(properties))
        {
            var values = m.Groups[2].Value.Split(',').Select(Value).ToArray();
            Vector3 V() => new(values.ElementAtOrDefault(0), values.ElementAtOrDefault(1), values.ElementAtOrDefault(2));
            switch (m.Groups[1].Value.ToLowerInvariant())
            {
                case "posa": position = V(); break;
                case "pos": position += V(); break;
                case "rota": rotation = V() * (MathF.PI / 180); break;
                case "rot": rotation += V() * (MathF.PI / 180); break;
                case "scale" or "scalea" when values.Length > 0 && values[0] > 0: scale = values[0]; break;
            }
        }
        return (position, rotation, scale);
    }

    private static float Value(string text)
    {
        var parts = text.Trim().Split('/');
        float Parse(string s) => float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return parts.Length == 2 ? (Parse(parts[0]) + Parse(parts[1])) / 2 : Parse(parts[0]);
    }
}
