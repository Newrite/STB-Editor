using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Data;

namespace StbEditor.Core;

/// <summary>
/// Which NPCs a SPID line gives its form to, by the line's filters as SPID reads them
/// (<c>Type = Form|StringFilters|FormFilters|LevelFilters|Traits|Count|Chance</c>): comma-separated entries are OR, '+' joins
/// what must all hold, '-' excludes, '*' matches a part. Strings are matched against the NPC's EditorID, name and keywords
/// (its own and its race's); forms against its factions, race, class, combat style, outfit, voice, keywords, spells, perks, the
/// NPC itself and form lists of these. Levels and traits (F / M, U unique, S summonable, C child) are checked where the record
/// says; levels that follow the player cannot be told and pass. Chance is reported, not rolled.
/// </summary>
public static class SpidMatch
{
    /// <summary>What a filter can be checked against, for one NPC (template traits followed).</summary>
    public sealed class Npc
    {
        public required FormKey Key { get; init; }
        public string EditorId { get; init; } = "";
        public string Name { get; init; } = "";
        public HashSet<string> KeywordIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<FormKey> Forms { get; init; } = [];
        public bool Female { get; init; }
        public bool Unique { get; init; }
        public bool Summonable { get; init; }
        public bool Child { get; init; }
        public short? Level { get; init; }

        public static Npc Of(INpcGetter npc, RecordLookup lookup)
        {
            var traits = npc;
            for (var i = 0; i < 8 && traits.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) && lookup.Resolve<INpcGetter>(traits.Template.FormKey) is { } t; i++) traits = t;
            var forms = new HashSet<FormKey> { npc.FormKey };
            void AddLink(FormKey? k) { if (k is { IsNull: false } v) forms.Add(v); }
            AddLink(traits.Race.FormKey);
            AddLink(traits.Class.FormKey);
            AddLink(npc.CombatStyle.FormKeyNullable);
            AddLink(npc.DefaultOutfit.FormKeyNullable);
            AddLink(traits.Voice.FormKeyNullable);
            foreach (var f in npc.Factions) AddLink(f.Faction.FormKey);
            foreach (var spell in npc.ActorEffect ?? []) AddLink(spell.FormKey);
            foreach (var p in npc.Perks ?? []) AddLink(p.Perk.FormKey);
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Keywords(IEnumerable<IFormLinkGetter<IKeywordGetter>>? list)
            {
                foreach (var k in list ?? [])
                {
                    if (k.IsNull) continue;
                    forms.Add(k.FormKey);
                    if (lookup.Catalog.Get(k.FormKey)?.EditorId is { } id) keywords.Add(id);
                }
            }
            Keywords(npc.Keywords);
            var race = lookup.Resolve<IRaceGetter>(traits.Race.FormKey);
            Keywords(race?.Keywords);
            var config = npc.Configuration;
            return new Npc
            {
                Key = npc.FormKey,
                EditorId = npc.EditorID ?? "",
                Name = npc.Name?.String ?? "",
                KeywordIds = keywords,
                Forms = forms,
                Female = traits.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female),
                Unique = config.Flags.HasFlag(NpcConfiguration.Flag.Unique),
                Summonable = config.Flags.HasFlag(NpcConfiguration.Flag.Summonable),
                Child = race?.Flags.HasFlag(Race.Flag.Child) ?? false,
                Level = config.Level is INpcLevelGetter fixedLevel ? fixedLevel.Level : null,
            };
        }
    }

    /// <summary>The result for one NPC: true / false, or null when a filter could not be told (it counts as passing).</summary>
    public static bool? Matches(IReadOnlyList<string> fields, Npc npc, Func<string, FormKey?> resolve, Func<FormKey, IEnumerable<FormKey>> listItems)
    {
        var unknown = false;
        bool Field(int index, Func<string, bool?> term)
        {
            if (index >= fields.Count) return true;
            var field = fields[index].Trim();
            if (field.Length == 0 || field.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return true;
            var entries = field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // Exclusions first: any excluded match rules the NPC out.
            foreach (var entry in entries.Where(e => e.StartsWith('-')))
                if (term(entry[1..]) == true) return false;
            var positive = entries.Where(e => !e.StartsWith('-')).ToList();
            if (positive.Count == 0) return true;
            foreach (var entry in positive)
            {
                var parts = entry.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var results = parts.Select(term).ToList();
                if (results.All(r => r != false))
                {
                    if (results.Any(r => r is null)) unknown = true;
                    return true;
                }
            }
            return false;
        }

        bool? String(string term)
        {
            if (term.StartsWith('*'))
            {
                var part = term[1..];
                return npc.EditorId.Contains(part, StringComparison.OrdinalIgnoreCase) || npc.Name.Contains(part, StringComparison.OrdinalIgnoreCase)
                    || npc.KeywordIds.Any(k => k.Contains(part, StringComparison.OrdinalIgnoreCase));
            }
            return npc.EditorId.Equals(term, StringComparison.OrdinalIgnoreCase) || npc.Name.Equals(term, StringComparison.OrdinalIgnoreCase) || npc.KeywordIds.Contains(term);
        }

        bool? FormTerm(string term)
        {
            // A plugin name alone: records from that plugin.
            if (term.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || term.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || term.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
                return npc.Key.ModKey.FileName.String.Equals(term, StringComparison.OrdinalIgnoreCase);
            if (resolve(term) is not { } key) return null;
            if (npc.Forms.Contains(key)) return true;
            var items = listItems(key).ToList();
            if (items.Count > 0) return items.Any(npc.Forms.Contains);
            // A location, a skill… cannot be told from the record.
            return null;
        }

        bool? LevelTerm(string term)
        {
            // "5/20" (actor level min/max) or "Skill(min/max)": only a fixed level is known.
            if (term.Contains('(')) return null;
            var p = term.Split('/');
            if (npc.Level is not { } level) return null;
            var min = p.Length > 0 && short.TryParse(p[0], out var a) ? a : (short)0;
            var max = p.Length > 1 && short.TryParse(p[1], out var b) ? b : short.MaxValue;
            return level >= min && level <= max;
        }

        if (!Field(1, String) || !Field(2, FormTerm) || !Field(3, LevelTerm)) return false;
        // Traits: "F/U/-S…" all must hold.
        if (fields.Count > 4 && fields[4].Trim() is { Length: > 0 } traits && !traits.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            foreach (var trait in traits.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var not = trait.StartsWith('-');
                var holds = trait.TrimStart('-').ToUpperInvariant() switch
                {
                    "F" => npc.Female,
                    "M" => !npc.Female,
                    "U" => npc.Unique,
                    "S" => npc.Summonable,
                    "C" => npc.Child,
                    _ => (bool?)null,
                };
                if (holds is null) { unknown = true; continue; }
                if (holds.Value == not) return false;
            }
        return unknown ? null : true;
    }

    /// <summary>The chance field of a line (the last one, "100" when missing).</summary>
    public static string Chance(IReadOnlyList<string> fields) =>
        fields.Count > 6 && fields[6].Trim() is { Length: > 0 } c && !c.Equals("NONE", StringComparison.OrdinalIgnoreCase) ? c : "100";
}
