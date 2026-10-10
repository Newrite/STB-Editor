namespace StbEditor.App.Ui;

/// <summary>
/// Interface language. Code is written with English strings wrapped in <see cref="T(string)"/>;
/// the Russian text comes from <see cref="LangRu"/> (English → Russian). A string missing there
/// stays English, so a new label never breaks anything — add its translation when you add it.
/// ImGui IDs: "Label##id" translates the visible part only; window titles use <see cref="Title"/>
/// ("Текст###Stable Name") so docking and the saved layout do not depend on the language.
/// </summary>
internal static class Loc
{
    public static bool Russian { get; set; }

    public static string T(string english)
    {
        if (!Russian) return english;
        var hash = english.IndexOf("##", StringComparison.Ordinal);
        if (hash < 0) return LangRu.Strings.TryGetValue(english, out var ru) ? ru : english;
        var visible = english[..hash];
        return (LangRu.Strings.TryGetValue(visible, out var part) ? part : visible) + english[hash..];
    }

    /// <summary>Translated format string: <c>T("{0} objects", n)</c>.</summary>
    public static string T(string english, params object?[] args) => string.Format(T(english), args);

    /// <summary>
    /// Interpolated text: <c>TF($"{n} objects")</c> looks up "{0} objects" (the composite format),
    /// so the translation can move the values around.
    /// </summary>
    public static string TF(FormattableString text) =>
        Russian && LangRu.Strings.ContainsKey(text.Format) ? string.Format(T(text.Format), text.GetArguments()) : text.ToString();

    /// <summary>Window title with a language-independent ID.</summary>
    public static string Title(string english) => T(english) + "###" + english;
}
