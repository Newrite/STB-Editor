using System.Text;
using Mutagen.Bethesda.Strings;
using Mutagen.Bethesda.Strings.DI;

namespace StbEditor.Data.Raw;

public enum PluginStringEncoding
{
    /// <summary>Strings live in .STRINGS files (Localized flag).</summary>
    Localized,

    /// <summary>No non-ASCII text, any encoding reads it the same.</summary>
    Ascii,
    Utf8,

    /// <summary>The language's legacy code page (1251 for Russian).</summary>
    Ansi,
}

/// <summary>
/// Finds out how a non-localized plugin stores its text. Skyrim itself has no notion of it: a
/// Russian build may hold UTF-8 plugins (STB.esp) next to cp1251 ones, and reading or writing
/// with the wrong one silently ruins every name. The editor keeps each plugin as it found it.
/// </summary>
public static class StringEncodingDetector
{
    /// <summary>Subrecords that hold player-visible text in non-localized plugins.</summary>
    private static readonly HashSet<string> TextSubrecords =
        ["FULL", "DESC", "NAM1", "RNAM", "ITXT", "SHRT", "NNAM", "EPF2", "TNAM"];

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    /// <summary>
    /// The encoding most strings of the plugin use, and whether both occur. Mixed plugins exist
    /// (Valtheim.esp: UTF-8 location names next to cp1251 ones), which is why reading always
    /// decodes per string with <see cref="ReadEncoding"/>.
    /// </summary>
    public static (PluginStringEncoding Encoding, bool Mixed) Detect(string path, PluginHeader header)
    {
        if (header.IsLocalized) return (PluginStringEncoding.Localized, false);

        var file = File.ReadAllBytes(path);
        var utf8 = 0;
        var invalid = 0;

        foreach (var record in RawFormat.Records(file))
        {
            if (record.Type == "TES4") continue;

            foreach (var sub in RawFormat.Subrecords(record.Data))
            {
                if (!TextSubrecords.Contains(sub.Type)) continue;

                var span = sub.Data.Span;
                var end = span.IndexOf((byte)0);
                if (end >= 0) span = span[..end];
                if (!HasHighBytes(span)) continue;

                // TNAM/NNAM/EPF2 are text only in some records; a binary hit just looks invalid,
                // so they only count when they decode cleanly.
                if (IsValidUtf8(span)) utf8++;
                else if (sub.Type is "FULL" or "DESC" or "NAM1" or "RNAM" or "ITXT" or "SHRT") invalid++;
            }
        }

        var mixed = utf8 > 0 && invalid > 0;
        if (invalid > 0) return (invalid > utf8 ? PluginStringEncoding.Ansi : PluginStringEncoding.Utf8, mixed);
        return (utf8 > 0 ? PluginStringEncoding.Utf8 : PluginStringEncoding.Ascii, false);
    }

    /// <summary>
    /// Read side for non-localized text: strict UTF-8, and the language's code page for strings
    /// that are not valid UTF-8. Real cp1251 Cyrillic practically never forms valid UTF-8.
    /// </summary>
    public static IMutagenEncoding ReadEncoding(Language language) =>
        new MutagenEncodingFallbackWrapper(new MutagenEncodingWrapper(StrictUtf8), new MutagenEncodingWrapper(AnsiFor(language)));

    /// <summary>Encoding new or edited text of a plugin is written in.</summary>
    public static Encoding WriteEncoding(PluginStringEncoding encoding, Language language) => encoding switch
    {
        PluginStringEncoding.Ansi => AnsiFor(language),
        _ => new UTF8Encoding(false),
    };

    /// <summary>Legacy code page the game uses for a language.</summary>
    public static Encoding AnsiFor(Language language) => language switch
    {
        Language.Russian => Encoding.GetEncoding(1251),
        Language.Polish or Language.Czech => Encoding.GetEncoding(1250),
        Language.Japanese => Encoding.GetEncoding(932),
        _ => Encoding.GetEncoding(1252),
    };

    private static bool HasHighBytes(ReadOnlySpan<byte> span)
    {
        foreach (var b in span)
            if (b >= 0x80) return true;
        return false;
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> span)
    {
        try
        {
            StrictUtf8.GetCharCount(span);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
