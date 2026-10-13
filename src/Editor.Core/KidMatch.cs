using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Data;

namespace StbEditor.Core;

/// <summary>
/// Which records a KID line gives its keyword to (<c>Keyword = Keyword|Type|Filters|Traits|Chance</c>): records of the type
/// whose EditorID, name or keywords match the filters (strings and forms mixed, ',' OR, '+' AND, '-' excludes, '*' a part).
/// Traits (enchanted, template…) are not checked and pass.
/// </summary>
public static class KidMatch
{
    /// <summary>KID's item types and the record signatures they stand for.</summary>
    public static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Weapon"] = "WEAP", ["Armor"] = "ARMO", ["Ammo"] = "AMMO", ["Magic Effect"] = "MGEF", ["Potion"] = "ALCH", ["Scroll"] = "SCRL",
        ["Location"] = "LCTN", ["Ingredient"] = "INGR", ["Book"] = "BOOK", ["Misc Item"] = "MISC", ["Key"] = "KEYM", ["Soul Gem"] = "SLGM",
        ["Spell"] = "SPEL", ["Activator"] = "ACTI", ["Flora"] = "FLOR", ["Furniture"] = "FURN", ["Race"] = "RACE", ["Talking Activator"] = "TACT",
        ["Enchantment"] = "ENCH",
    };

    /// <summary>The records of the line's type that match; with the count of those a filter could not be told for.</summary>
    public static (List<FormKey> Matched, int Unknown) Records(IReadOnlyList<string> fields, RecordLookup lookup, Func<string, FormKey?> resolve, CancellationToken cancel = default)
    {
        var matched = new List<FormKey>();
        var unknown = 0;
        if (fields.Count < 2 || !Types.TryGetValue(fields[1].Trim(), out var signature)) return (matched, 0);
        var filter = fields.Count > 2 ? fields[2].Trim() : "";
        foreach (var entry in lookup.Catalog.BySignature.GetValueOrDefault(signature) ?? [])
        {
            cancel.ThrowIfCancellationRequested();
            if (lookup.Resolve(entry.FormKey) is not { } record) continue;
            switch (Matches(filter, record, lookup, resolve))
            {
                case true: matched.Add(record.FormKey); break;
                case null: unknown++; matched.Add(record.FormKey); break;
            }
        }
        return (matched, unknown);
    }

    public static bool? Matches(string filter, IMajorRecordGetter record, RecordLookup lookup, Func<string, FormKey?> resolve)
    {
        if (filter.Length == 0 || filter.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return true;
        var editorId = record.EditorID ?? "";
        var name = (record as INamedGetter)?.Name ?? "";
        var keywords = (record as IKeywordedGetter<IKeywordGetter>)?.Keywords?.Where(k => !k.IsNull).Select(k => k.FormKey).ToHashSet() ?? [];
        var keywordIds = keywords.Select(k => lookup.Catalog.Get(k)?.EditorId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = false;
        bool? Term(string term)
        {
            if (term.StartsWith('*'))
            {
                var part = term[1..];
                return editorId.Contains(part, StringComparison.OrdinalIgnoreCase) || name.Contains(part, StringComparison.OrdinalIgnoreCase) || keywordIds.Any(k => k.Contains(part, StringComparison.OrdinalIgnoreCase));
            }
            if (editorId.Equals(term, StringComparison.OrdinalIgnoreCase) || name.Equals(term, StringComparison.OrdinalIgnoreCase) || keywordIds.Contains(term)) return true;
            if (resolve(term) is { } key)
            {
                if (key == record.FormKey || keywords.Contains(key)) return true;
                if (lookup.Resolve<IFormListGetter>(key) is { } list) return list.Items.Any(i => i.FormKey == record.FormKey || keywords.Contains(i.FormKey));
                return false;
            }
            return false;
        }
        var entries = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in entries.Where(e => e.StartsWith('-')))
            if (Term(entry[1..]) == true) return false;
        var positive = entries.Where(e => !e.StartsWith('-')).ToList();
        if (positive.Count == 0) return true;
        foreach (var entry in positive)
        {
            var results = entry.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Term).ToList();
            if (results.All(r => r != false)) { if (results.Any(r => r is null)) unknown = true; return unknown ? null : true; }
        }
        return false;
    }
}
