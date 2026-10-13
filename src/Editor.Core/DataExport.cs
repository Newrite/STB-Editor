using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Data;
using StbEditor.Data.Catalog;

namespace StbEditor.Core;

/// <summary>
/// Export data (xEdit / CK: Export): a plugin's texts by kind as tab-separated files — names, descriptions, books, messages,
/// quests (names, journal, objectives), dialogue (responses, prompts) and the rest — plus its game settings with their
/// values. One row per text: FormID, signature, EditorID, field, text.
/// </summary>
public static class DataExport
{
    public static readonly string[] Kinds = ["Names", "Descriptions", "Books", "Messages", "Quests", "Dialogue", "Other texts", "Game settings"];

    /// <summary>Which kind a text belongs to.</summary>
    public static string KindOf(TextHit hit) => hit switch
    {
        { Signature: "BOOK", PathText: "BookText" } => "Books",
        { Signature: "MESG" } => "Messages",
        { Signature: "QUST" } => "Quests",
        { Signature: "INFO" or "DIAL" } => "Dialogue",
        { PathText: "Name" } => "Names",
        { PathText: "Description" } => "Descriptions",
        _ => "Other texts",
    };

    /// <summary>Writes the chosen kinds into <paramref name="folder"/>; returns (file, rows) per file written.</summary>
    public static List<(string File, int Rows)> Write(PluginInfo plugin, RecordCatalog catalog, Func<FormKey, string> formId, IReadOnlySet<string> kinds, string folder,
        IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(folder);
        var written = new List<(string, int)>();
        var name = Path.GetFileNameWithoutExtension(plugin.Name);
        static string Clean(string s) => s.Replace('\t', ' ').Replace("\r\n", "\\n").Replace('\n', ' ').Replace('\r', ' ');

        if (kinds.Any(k => k != "Game settings"))
        {
            var hits = TextSearch.DisplayTexts(plugin, catalog, progress, cancel);
            foreach (var group in hits.GroupBy(KindOf))
            {
                if (!kinds.Contains(group.Key)) continue;
                var text = new StringBuilder("FORMID\tSIGNATURE\tEDITORID\tFIELD\tTEXT\r\n");
                foreach (var hit in group)
                    text.Append(formId(hit.Key)).Append('\t').Append(hit.Signature).Append('\t').Append(hit.EditorId).Append('\t').Append(hit.PathText).Append('\t').Append(Clean(hit.Text)).Append("\r\n");
                var file = Path.Combine(folder, $"{name} - {group.Key}.txt");
                File.WriteAllText(file, text.ToString(), new UTF8Encoding(true));
                written.Add((file, group.Count()));
            }
        }
        if (kinds.Contains("Game settings") && plugin.Mod is { } mod)
        {
            var text = new StringBuilder("FORMID\tEDITORID\tTYPE\tVALUE\r\n");
            var rows = 0;
            foreach (var setting in mod.GameSettings)
            {
                var (type, value) = setting switch
                {
                    IGameSettingStringGetter s => ("string", s.Data?.String ?? ""),
                    IGameSettingFloatGetter f => ("float", f.Data?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""),
                    IGameSettingIntGetter i => ("int", i.Data?.ToString() ?? ""),
                    IGameSettingBoolGetter b => ("bool", b.Data?.ToString() ?? ""),
                    _ => (setting.GetType().Name, ""),
                };
                text.Append(formId(setting.FormKey)).Append('\t').Append(setting.EditorID).Append('\t').Append(type).Append('\t').Append(Clean(value)).Append("\r\n");
                rows++;
            }
            if (rows > 0)
            {
                var file = Path.Combine(folder, $"{name} - Game settings.txt");
                File.WriteAllText(file, text.ToString(), new UTF8Encoding(true));
                written.Add((file, rows));
            }
        }
        return written;
    }
}

/// <summary>
/// xEdit's ModGroups: plugins that belong together (a mod and its patches) — when every one of a group is loaded, only the
/// group's last version of a record takes part in conflict detection, so the group's own overrides of each other are not
/// reported. Kept in a JSON file.
/// </summary>
public sealed record ModGroup(string Name, List<string> Plugins);

public static class ModGroups
{
    public static List<ModGroup> Load(string path)
    {
        try { return File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<List<ModGroup>>(File.ReadAllText(path)) ?? [] : []; }
        catch { return []; }
    }

    public static void Save(string path, IReadOnlyList<ModGroup> groups)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(groups, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Groups whose plugins are all loaded (the others do nothing, as in xEdit).</summary>
    public static List<ModGroup> Active(IEnumerable<ModGroup> groups, IEnumerable<string> loaded)
    {
        var set = loaded.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return groups.Where(g => g.Plugins.Count > 1 && g.Plugins.All(set.Contains)).ToList();
    }

    /// <summary>
    /// Versions of a record, winner first, with each active group's versions but its winning one left out.
    /// </summary>
    public static List<T> Collapse<T>(IReadOnlyList<T> winnerFirst, Func<T, string> plugin, IReadOnlyList<ModGroup> active)
    {
        if (active.Count == 0) return [.. winnerFirst];
        var seen = new HashSet<ModGroup>();
        var result = new List<T>();
        foreach (var version in winnerFirst)
        {
            var name = plugin(version);
            var group = active.FirstOrDefault(g => g.Plugins.Contains(name, StringComparer.OrdinalIgnoreCase));
            if (group is not null && !seen.Add(group)) continue;
            result.Add(version);
        }
        return result;
    }
}
