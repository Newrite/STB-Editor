using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Strings;

namespace StbEditor.Core;

/// <summary>One text of a plugin for translation: where it is, the text, and the translation (filled by the translator).</summary>
public sealed class TextEntry
{
    public required string Form { get; init; }
    public required string Signature { get; init; }
    public string? EditorId { get; init; }
    /// <summary>The field: property names and list indices, as <see cref="RecordFields.Set"/> takes them.</summary>
    public required object[] Path { get; init; }
    /// <summary>A dialogue line's topic (lines are written under it).</summary>
    public string? Topic { get; init; }
    public required string Source { get; init; }
    public string Translation { get; set; } = "";

    [JsonIgnore] public FormKey Key => FormKey.Factory(Form);
    [JsonIgnore] public FormKey? TopicKey => Topic is null ? null : FormKey.Factory(Topic);
}

/// <summary>A plugin's texts as a file: what is exported for translation and imported back.</summary>
public sealed class TextFile
{
    public required string Plugin { get; init; }
    public string Note { get; init; } = "Fill in \"translation\"; empty or unchanged entries are skipped on import.";
    public required List<TextEntry> Strings { get; init; }
}

/// <summary>
/// Texts for translation (CK: export of localized strings; xEdit: Localization): every text the player sees in a plugin —
/// names, descriptions, book text, dialogue, log entries, messages — into a JSON file with an empty "translation" per text,
/// and the filled file back into the records (as overrides in the active file, or the plugin's own records when it is the
/// active file). JSON keeps line breaks and Cyrillic as they are, for people and for tools.
/// </summary>
public static class TextTranslation
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static TextFile FromHits(string plugin, IEnumerable<TextHit> hits) => new()
    {
        Plugin = plugin,
        Strings = hits.Where(h => h.Display).Select(h => new TextEntry
        {
            Form = h.Key.ToString(),
            Signature = h.Signature,
            EditorId = h.EditorId,
            Path = [.. h.Path],
            Topic = h.Topic?.ToString(),
            Source = h.Text,
        }).ToList(),
    };

    public static void Write(string path, TextFile file)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(file, Options));
    }

    public static TextFile Read(string path)
    {
        var file = JsonSerializer.Deserialize<TextFile>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("empty file");
        // Path segments come back as JSON elements: numbers are list indices, strings property names.
        foreach (var entry in file.Strings)
            for (var i = 0; i < entry.Path.Length; i++)
                if (entry.Path[i] is JsonElement element)
                    entry.Path[i] = element.ValueKind == JsonValueKind.Number ? element.GetInt32() : element.GetString() ?? "";
        return file;
    }

    /// <summary>Entries with a translation that differs from the text.</summary>
    public static IEnumerable<TextEntry> Translated(TextFile file) =>
        file.Strings.Where(e => !string.IsNullOrEmpty(e.Translation) && e.Translation != e.Source);

    public enum Outcome { Applied, AlreadyTranslated, SourceChanged, Missing }

    /// <summary>
    /// Puts a translation into a record's working copy: only where the text is still the exported one (a text changed
    /// since the export, or overridden by a later plugin, is reported, not overwritten — unless forced).
    /// </summary>
    /// <param name="force">Also where the text is no longer the exported one (a later plugin overrides it).</param>
    public static Outcome Apply(IMajorRecord copy, TextEntry entry, bool force = false)
    {
        object? value;
        try { value = RecordFields.Get(copy, entry.Path); }
        catch { return Outcome.Missing; }
        var text = value switch { ITranslatedStringGetter t => t.String, string s => s, _ => null };
        if (text is null) return Outcome.Missing;
        if (text == entry.Translation) return Outcome.AlreadyTranslated;
        if (text != entry.Source && !force) return Outcome.SourceChanged;
        RecordFields.Set(copy, entry.Path, entry.Translation);
        return Outcome.Applied;
    }
}
