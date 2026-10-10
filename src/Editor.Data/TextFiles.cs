using System.Text;

namespace StbEditor.Data;

/// <summary>
/// Reads build text files whose encoding varies from file to file.
///
/// In one MO2 profile loadorder.txt can be UTF-8 while plugins.txt is the system ANSI page
/// (1251 here). Reading both as UTF-8 turns a Cyrillic plugin name into U+FFFD, the names stop
/// matching, the plugin counts as disabled and every FormID below it shifts. So lines are decoded
/// one by one: strict UTF-8 first, ANSI on failure.
/// </summary>
public static class TextFiles
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    static TextFiles()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Ansi = Encoding.GetEncoding(1251);
    }

    public static Encoding Ansi { get; }

    public static string[] ReadLines(string path)
    {
        if (!File.Exists(path)) return [];

        var bytes = File.ReadAllBytes(path);
        var start = HasBom(bytes) ? 3 : 0;

        var lines = new List<string>();
        var lineStart = start;

        for (var i = start; i <= bytes.Length; i++)
        {
            if (i != bytes.Length && bytes[i] != (byte)'\n') continue;

            var end = i;
            if (end > lineStart && bytes[end - 1] == (byte)'\r') end--;

            lines.Add(Decode(bytes, lineStart, end - lineStart));
            lineStart = i + 1;
        }

        return [.. lines];
    }

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Ansi.GetString(bytes);
        }
    }

    private static string Decode(byte[] bytes, int offset, int count) =>
        count <= 0 ? string.Empty : Decode(bytes.AsSpan(offset, count));

    private static bool HasBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    /// <summary>Value of <c>key=value</c> from an INI file, section ignored. Last occurrence wins, like the engine.</summary>
    public static string? ReadIniValue(string path, string key)
    {
        string? result = null;

        foreach (var raw in ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#' or '[') continue;

            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            if (line[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                result = line[(separator + 1)..].Trim();
        }

        return result;
    }
}
