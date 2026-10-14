using System.Text.Json;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;

namespace StbEditor.Core;

/// <summary>Whether a condition holds for an NPC: from its record, or only in the game (weapon drawn, weather, a random roll…).</summary>
public enum Verdict { Pass, Fail, Unknown }

/// <summary>
/// An animation replacer: an Open Animation Replacer submod (its folder's <c>config.json</c>, with <c>user.json</c> over it) or a
/// Dynamic Animation Replacer folder (<c>_CustomConditions\&lt;priority&gt;\_conditions.txt</c>). The highest-priority replacer
/// whose conditions hold plays its animations.
/// </summary>
/// <param name="Folder">Asset folder of its animations (meshes\actors\…\).</param>
/// <param name="Conditions">OAR: the condition array; DAR: null (see <paramref name="DarLines"/>).</param>
public sealed record AnimReplacer(string Framework, string Mod, string Name, long Priority, string Folder, string File, bool Disabled,
    int Animations, JsonElement? Conditions, IReadOnlyList<string> DarLines);

/// <summary>What the record says about an NPC, for the conditions: SPID's facts plus what its outfit puts on it.</summary>
public sealed class AnimFacts
{
    public required SpidMatch.Npc Npc { get; init; }

    /// <summary>Armour and weapons of its default outfit and inventory (what it probably wears and holds).</summary>
    public HashSet<FormKey> Gear { get; init; } = [];

    /// <summary>Keywords of that gear, as FormKeys and EditorIDs.</summary>
    public HashSet<FormKey> GearKeywords { get; init; } = [];
    public HashSet<string> GearKeywordIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One condition's verdict and how it reads, for the table's details.</summary>
public sealed record ConditionResult(string Text, Verdict Verdict, int Depth);

public static class AnimReplacers
{
    /// <summary>Folder of a replacer config: everything up to its file name.</summary>
    private static string FolderOf(string path) => path[..(path.LastIndexOf('\\') + 1)];

    /// <summary>
    /// The replacers of the load order. <paramref name="files"/>: asset paths of OAR config.json / user.json and DAR
    /// _conditions.txt with their mod and a reader; <paramref name="animations"/> counts the .hkx files under a folder.
    /// </summary>
    public static List<AnimReplacer> Build(IEnumerable<(string Path, string Mod, Func<string?> Text)> files, Func<string, int> animations)
    {
        var result = new List<AnimReplacer>();
        var list = files.ToList();
        var users = list.Where(f => Path.GetFileName(f.Path).Equals("user.json", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => FolderOf(f.Path), StringComparer.OrdinalIgnoreCase);
        // OAR mod folders (meshes\…\OpenAnimationReplacer\<Mod>\config.json) only name the mod.
        var modNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, _, read) in list.Where(f => Path.GetFileName(f.Path).Equals("config.json", StringComparison.OrdinalIgnoreCase)))
            if (IsOarModFolder(path) && Json(read()) is { } doc && doc.TryGetProperty("name", out var n)) modNames[FolderOf(path)] = n.GetString() ?? "";

        foreach (var (path, mod, read) in list)
        {
            var name = Path.GetFileName(path);
            if (name.Equals("_conditions.txt", StringComparison.OrdinalIgnoreCase))
            {
                var folder = FolderOf(path);
                var priorityText = Path.GetFileName(folder.TrimEnd('\\'));
                if (!long.TryParse(priorityText, out var priority)) continue;
                var lines = (read() ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith(';')).ToList();
                result.Add(new AnimReplacer("DAR", mod, $"DAR {priority}", priority, folder, path, false, animations(folder), null, lines));
                continue;
            }
            if (!name.Equals("config.json", StringComparison.OrdinalIgnoreCase) || IsOarModFolder(path)) continue;
            if (Json(read()) is not { } config) continue;
            var submod = FolderOf(path);
            // user.json (the in-game editor's changes) wins over config.json.
            var user = users.TryGetValue(submod, out var u) ? Json(u.Text()) : null;
            JsonElement Pick(string key, JsonElement fallback) => user is { } us && us.TryGetProperty(key, out var v) ? v : fallback;
            var title = config.TryGetProperty("name", out var t) ? t.GetString() ?? "" : Path.GetFileName(submod.TrimEnd('\\'));
            var parent = FolderOf(submod.TrimEnd('\\'));
            var modTitle = modNames.GetValueOrDefault(parent) ?? Path.GetFileName(parent.TrimEnd('\\'));
            long prio = 0;
            if (config.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.Number) prio = p.GetInt64();
            var pu = Pick("priority", default);
            if (pu.ValueKind == JsonValueKind.Number) prio = pu.GetInt64();
            var disabled = Pick("disabled", config.TryGetProperty("disabled", out var d) ? d : default) is { ValueKind: JsonValueKind.True };
            var conditions = Pick("conditions", config.TryGetProperty("conditions", out var c) ? c : default);
            result.Add(new AnimReplacer("OAR", mod, $"{modTitle} / {title}", prio, submod, path, disabled, animations(submod),
                conditions.ValueKind == JsonValueKind.Array ? conditions.Clone() : null, []));
        }
        return result.OrderByDescending(r => r.Priority).ToList();
    }

    /// <summary>An OAR mod folder holds the submods: its config.json sits right under OpenAnimationReplacer\.</summary>
    private static bool IsOarModFolder(string path)
    {
        var parts = path.Split('\\');
        var i = Array.FindIndex(parts, s => s.Equals("OpenAnimationReplacer", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && parts.Length - i == 3;
    }

    private static JsonElement? Json(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    // ------------------------------------------------------------------ evaluation

    /// <summary>Conditions read from the record: the NPC itself, race, class, voice, combat style, faction, perk, spell, keyword.</summary>
    private static readonly HashSet<string> Membership = new(StringComparer.OrdinalIgnoreCase)
    {
        "IsActorBase", "IsRace", "IsClass", "IsVoiceType", "IsCombatStyle", "IsInFaction", "HasPerk", "HasSpell", "HasKeyword",
        "IsActor", "FactionRank",
    };

    /// <summary>Whether a replacer's conditions hold for the NPC, and each condition's verdict.</summary>
    public static (Verdict Verdict, List<ConditionResult> Details) Evaluate(AnimReplacer replacer, AnimFacts facts, Func<string, FormKey?> byEditorId)
    {
        var details = new List<ConditionResult>();
        if (replacer.Conditions is { } array) return (All(array, facts, byEditorId, details, 0), details);
        return (Dar(replacer.DarLines, facts, byEditorId, details), details);
    }

    private static Verdict All(JsonElement array, AnimFacts facts, Func<string, FormKey?> byEditorId, List<ConditionResult> details, int depth)
    {
        var verdict = Verdict.Pass;
        foreach (var c in array.EnumerateArray()) verdict = And(verdict, Oar(c, facts, byEditorId, details, depth));
        return verdict;
    }

    private static Verdict And(Verdict a, Verdict b) => a == Verdict.Fail || b == Verdict.Fail ? Verdict.Fail : a == Verdict.Unknown || b == Verdict.Unknown ? Verdict.Unknown : Verdict.Pass;
    private static Verdict Or(Verdict a, Verdict b) => a == Verdict.Pass || b == Verdict.Pass ? Verdict.Pass : a == Verdict.Unknown || b == Verdict.Unknown ? Verdict.Unknown : Verdict.Fail;
    private static Verdict Not(Verdict v) => v switch { Verdict.Pass => Verdict.Fail, Verdict.Fail => Verdict.Pass, _ => Verdict.Unknown };

    private static Verdict Oar(JsonElement c, AnimFacts facts, Func<string, FormKey?> byEditorId, List<ConditionResult> details, int depth)
    {
        var name = c.TryGetProperty("condition", out var n) ? n.GetString() ?? "?" : "?";
        var negated = c.TryGetProperty("negated", out var neg) && neg.ValueKind == JsonValueKind.True;
        if (c.TryGetProperty("disabled", out var off) && off.ValueKind == JsonValueKind.True) return Verdict.Pass;
        if (name is "AND" or "OR")
        {
            var at = details.Count;
            details.Add(new ConditionResult((negated ? "NOT " : "") + name, Verdict.Unknown, depth));
            var verdict = name == "AND" ? Verdict.Pass : Verdict.Fail;
            if (c.TryGetProperty("Conditions", out var children) && children.ValueKind == JsonValueKind.Array)
                foreach (var child in children.EnumerateArray())
                    verdict = name == "AND" ? And(verdict, Oar(child, facts, byEditorId, details, depth + 1)) : Or(verdict, Oar(child, facts, byEditorId, details, depth + 1));
            if (negated) verdict = Not(verdict);
            details[at] = details[at] with { Verdict = verdict };
            return verdict;
        }
        // The condition's form: the first field holding one (pluginName / formID or editorID).
        FormKey? form = null;
        string? formText = null;
        foreach (var property in c.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.Object && FormOf(property.Value, byEditorId, out var key, out var text)) { form = key; formText = text; break; }
        var result = Check(name, form, formText, facts);
        if (negated) result = Not(result);
        details.Add(new ConditionResult($"{(negated ? "NOT " : "")}{name}{(formText is null ? Values(name, c) : $" {formText}")}", result, depth));
        return result;
    }

    /// <summary>OAR's equipped types (IsEquippedType «Type»).</summary>
    private static readonly string[] EquippedTypes = ["unarmed", "sword", "dagger", "war axe", "mace", "greatsword", "battleaxe / warhammer", "bow", "staff", "crossbow",
        "spell", "shield", "torch"];

    /// <summary>A condition's plain values for its text: IsEquippedType's weapon type and hand, comparisons, numbers.</summary>
    private static string Values(string name, JsonElement c)
    {
        var parts = new List<string>();
        foreach (var property in c.EnumerateObject())
        {
            if (property.Name is "condition" or "requiredVersion" or "negated" or "disabled") continue;
            var v = property.Value;
            if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("value", out var inner) && inner.ValueKind == JsonValueKind.Number)
            {
                var n = inner.GetDouble();
                parts.Add(name.Equals("IsEquippedType", StringComparison.OrdinalIgnoreCase) && property.Name == "Type"
                    ? (n >= 0 && n < EquippedTypes.Length ? EquippedTypes[(int)n] : n < 0 ? "nothing" : $"type {n}")
                    : $"{property.Name} {n}");
            }
            else if (v.ValueKind is JsonValueKind.True) parts.Add(property.Name.ToLowerInvariant());
            else if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and < 12 } s) parts.Add(s);
        }
        return parts.Count == 0 ? "" : " " + string.Join(", ", parts);
    }

    private static bool FormOf(JsonElement value, Func<string, FormKey?> byEditorId, out FormKey? key, out string text)
    {
        key = null;
        text = "";
        if (value.TryGetProperty("form", out var inner) && inner.ValueKind == JsonValueKind.Object) value = inner;
        if (value.TryGetProperty("editorID", out var id) && id.GetString() is { Length: > 0 } editorId)
        {
            key = byEditorId(editorId);
            text = editorId;
            return true;
        }
        if (value.TryGetProperty("pluginName", out var plugin) && value.TryGetProperty("formID", out var formId) && plugin.GetString() is { } p && formId.GetString() is { } f)
        {
            try { key = new FormKey(ModKey.FromFileName(p), Convert.ToUInt32(f, 16) & 0xFFFFFF); } catch { key = null; }
            text = $"{p}:{f}";
            return true;
        }
        return false;
    }

    /// <summary>A condition by its name against the record's facts; the rest only the game can tell.</summary>
    private static Verdict Check(string name, FormKey? form, string? formText, AnimFacts facts)
    {
        var npc = facts.Npc;
        if (name.Equals("IsFemale", StringComparison.OrdinalIgnoreCase)) return npc.Female ? Verdict.Pass : Verdict.Fail;
        if (name.Equals("IsChild", StringComparison.OrdinalIgnoreCase)) return npc.Child ? Verdict.Pass : Verdict.Fail;
        if (name.Equals("IsUnique", StringComparison.OrdinalIgnoreCase)) return npc.Unique ? Verdict.Pass : Verdict.Fail;
        if (Membership.Contains(name))
        {
            if (form is { } key) return npc.Forms.Contains(key) ? Verdict.Pass : Verdict.Fail;
            if (formText is { Length: > 0 } t && npc.KeywordIds.Contains(t)) return Verdict.Pass;
            return formText is null ? Verdict.Unknown : Verdict.Fail;
        }
        // Worn / equipped: what its outfit and inventory hold says it may; without it, it may still pick something up.
        if (name.StartsWith("IsWorn", StringComparison.OrdinalIgnoreCase) || name.StartsWith("IsEquipped", StringComparison.OrdinalIgnoreCase))
        {
            if (name.EndsWith("HasKeyword", StringComparison.OrdinalIgnoreCase))
                return (form is { } k && facts.GearKeywords.Contains(k)) || (formText is { } t && facts.GearKeywordIds.Contains(t)) ? Verdict.Pass : Verdict.Unknown;
            if (form is { } item) return facts.Gear.Contains(item) ? Verdict.Pass : Verdict.Unknown;
        }
        return Verdict.Unknown;
    }

    private static readonly Regex DarLine = new(@"^(NOT\s+)?(\w+)\s*\((.*)\)\s*(AND|OR)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DarForm = new(@"""([^""]+\.(?:esp|esm|esl))""\s*\|\s*(?:0x)?([0-9A-Fa-f]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>DAR: lines joined by AND; a line ending in OR forms a group with the lines after it up to one without OR.</summary>
    private static Verdict Dar(IReadOnlyList<string> lines, AnimFacts facts, Func<string, FormKey?> byEditorId, List<ConditionResult> details)
    {
        var verdict = Verdict.Pass;
        Verdict? group = null;
        foreach (var line in lines)
        {
            var m = DarLine.Match(line);
            if (!m.Success) { details.Add(new ConditionResult(line, Verdict.Unknown, 0)); verdict = And(verdict, Verdict.Unknown); continue; }
            var name = m.Groups[2].Value;
            FormKey? form = null;
            string? text = null;
            if (DarForm.Match(m.Groups[3].Value) is { Success: true } f)
            {
                try { form = new FormKey(ModKey.FromFileName(f.Groups[1].Value), Convert.ToUInt32(f.Groups[2].Value, 16) & 0xFFFFFF); } catch { }
                text = $"{f.Groups[1].Value}:{f.Groups[2].Value}";
            }
            var result = Check(name, form, text, facts);
            if (m.Groups[1].Success) result = Not(result);
            details.Add(new ConditionResult(line.Replace(" AND", "").Replace(" OR", ""), result, group is null ? 0 : 1));
            group = group is { } g ? Or(g, result) : result;
            if (!m.Groups[4].Value.Equals("OR", StringComparison.OrdinalIgnoreCase))
            {
                verdict = And(verdict, group.Value);
                group = null;
            }
        }
        if (group is { } open) verdict = And(verdict, open);
        return verdict;
    }
}
