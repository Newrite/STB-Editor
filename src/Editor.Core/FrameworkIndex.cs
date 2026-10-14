using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;

namespace StbEditor.Core;

/// <summary>
/// A record named in a framework's config file: which framework and file (and mod), the line, what the record does there
/// (<see cref="Role"/>) and the line's fields.
/// </summary>
/// <param name="Role">"gives" (SPID distributes it), "keyword" (KID adds it), "filter" (a condition of the line), "swapped out" /
/// "swapped in" / "transformed" (Base Object Swapper), "list" / "added" (FormList Manipulator), "description" (Description
/// Framework), "used" (named somewhere in the file: OAR / DAR conditions, Sound Record Distributor).</param>
/// <param name="Kind">SPID: the distributed type (Spell, Perk, Item…); KID: the item type the keyword goes to.</param>
public sealed record FrameworkRef(string Framework, string File, string Mod, int Line, string Text, FormKey Form, string Role, string Kind, IReadOnlyList<string> Fields);

/// <summary>
/// What the popular SKSE frameworks of the load order do with records, read from their config files: SPID (_DISTR.ini),
/// KID (_KID.ini), Base Object Swapper (_SWAP.ini), FormList Manipulator (_FLM.ini), Description Framework (_DESC.ini),
/// Sound Record Distributor (_SRD.yaml), Open Animation Replacer (config.json / user.json) and Dynamic Animation Replacer
/// (_conditions.txt). Forms are named as EditorIDs, <c>0x123~Plugin.esp</c>, <c>Plugin.esp|123</c> or OAR's
/// pluginName / formID pairs.
/// </summary>
public sealed class FrameworkIndex
{
    public List<FrameworkRef> All { get; } = [];
    public Dictionary<FormKey, List<FrameworkRef>> ByForm { get; } = new();
    public Dictionary<string, int> Files { get; } = new();

    public IReadOnlyList<FrameworkRef> Of(FormKey key) => ByForm.TryGetValue(key, out var list) ? list : [];

    /// <summary>Which framework reads a file, by its name and folder; null when none.</summary>
    public static string? FrameworkOf(string path)
    {
        var name = Path.GetFileName(path);
        var lower = path.ToLowerInvariant();
        if (name.EndsWith("_DISTR.ini", StringComparison.OrdinalIgnoreCase)) return "SPID";
        if (name.EndsWith("_KID.ini", StringComparison.OrdinalIgnoreCase)) return "KID";
        if (name.EndsWith("_SWAP.ini", StringComparison.OrdinalIgnoreCase)) return "BOS";
        if (name.EndsWith("_FLM.ini", StringComparison.OrdinalIgnoreCase)) return "FLM";
        if (name.EndsWith("_DESC.ini", StringComparison.OrdinalIgnoreCase)) return "DESC";
        if (name.EndsWith("_SRD.yaml", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_SRD.yml", StringComparison.OrdinalIgnoreCase)) return "SRD";
        if (lower.Contains("openanimationreplacer") && (name.Equals("config.json", StringComparison.OrdinalIgnoreCase) || name.Equals("user.json", StringComparison.OrdinalIgnoreCase))) return "OAR";
        if (lower.Contains("_customconditions") && name.Equals("_conditions.txt", StringComparison.OrdinalIgnoreCase)) return "DAR";
        return null;
    }

    private static readonly Regex TildeForm = new(@"(?:0x)?([0-9A-Fa-f]{1,8})\s*~\s*([^|,\s""\]\)]+\.(?:esp|esm|esl))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PipeForm = new(@"([^\s|,""'(\[]+\.(?:esp|esm|esl))\s*\|\s*(?:0x)?([0-9A-Fa-f]{1,8})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OarForm = new(@"""pluginName""\s*:\s*""([^""]+)""\s*,\s*""formID""\s*:\s*""([0-9A-Fa-f]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DarForm = new(@"""([^""]+\.(?:esp|esm|esl))""\s*\|\s*0x([0-9A-Fa-f]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="resolve">An EditorID or a form text (0x123~Plugin.esp) to a FormKey, or null.</param>
    public static FrameworkIndex Build(IEnumerable<(string Path, string Mod, Func<string?> Text)> files, Func<string, FormKey?> resolve, CancellationToken cancel = default)
    {
        var index = new FrameworkIndex();
        foreach (var (path, mod, read) in files)
        {
            cancel.ThrowIfCancellationRequested();
            if (FrameworkOf(path) is not { } framework) continue;
            string? text;
            try { text = read(); } catch { continue; }
            if (text is null) continue;
            index.Files[framework] = index.Files.GetValueOrDefault(framework) + 1;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            switch (framework)
            {
                case "SPID" or "KID" or "FLM" or "BOS" or "DESC":
                    ReadIni(index, framework, path, mod, lines, resolve);
                    break;
                default:
                    ReadScan(index, framework, path, mod, lines, text);
                    break;
            }
        }
        foreach (var r in index.All) (index.ByForm.TryGetValue(r.Form, out var list) ? list : index.ByForm[r.Form] = []).Add(r);
        return index;
    }

    /// <summary>A form named in a field: the field as a form text or EditorID (sign and wildcard prefixes taken off).</summary>
    private static FormKey? Form(string token, Func<string, FormKey?> resolve)
    {
        var t = token.Trim().TrimStart('+', '-', '!').Trim();
        if (t.Length == 0 || t.Contains('*') || t.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return null;
        if (TildeForm.Match(t) is { Success: true } m) return Make(m.Groups[2].Value, m.Groups[1].Value);
        if (PipeForm.Match(t) is { Success: true } p) return Make(p.Groups[1].Value, p.Groups[2].Value);
        return resolve(t);
    }

    private static FormKey? Make(string plugin, string hex)
    {
        try { return new FormKey(ModKey.FromFileName(plugin.Trim()), Convert.ToUInt32(hex, 16) & 0xFFFFFF); }
        catch { return null; }
    }

    /// <summary>Forms of a filter field: entries split by ',' (OR) and '+' (AND).</summary>
    private static IEnumerable<FormKey> FieldForms(string field, Func<string, FormKey?> resolve)
    {
        foreach (var entry in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
            foreach (var part in entry.Split('+', StringSplitOptions.RemoveEmptyEntries))
                if (Form(part, resolve) is { } key) yield return key;
    }

    private static void ReadIni(FrameworkIndex index, string framework, string path, string mod, string[] lines, Func<string, FormKey?> resolve)
    {
        var section = "";
        for (var n = 0; n < lines.Length; n++)
        {
            var raw = lines[n].Trim().TrimStart('﻿');
            if (raw.Length == 0 || raw.StartsWith(';') || raw.StartsWith('#')) continue;
            var comment = raw.IndexOf(';');
            var line = framework == "DESC" ? raw : (comment > 0 ? raw[..comment] : raw).Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            void Add(FormKey key, string role, string kind, IReadOnlyList<string> fields) =>
                index.All.Add(new FrameworkRef(framework, path, mod, n + 1, raw, key, role, kind, fields));
            switch (framework)
            {
                case "SPID":
                case "KID":
                case "FLM":
                {
                    var eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    var type = line[..eq].Trim();
                    var fields = line[(eq + 1)..].Split('|').Select(f => f.Trim()).ToList();
                    if (fields.Count == 0) continue;
                    var kind = framework == "KID" && fields.Count > 1 ? fields[1] : type;
                    var main = framework == "SPID" ? "gives" : framework == "KID" ? "keyword" : "list";
                    foreach (var key in FieldForms(fields[0], resolve)) Add(key, main, kind, fields);
                    // SPID: string filters (keywords by EditorID), form filters; KID: its filter field; FLM: the forms added.
                    var filterFields = framework switch { "SPID" => new[] { 1, 2 }, "KID" => new[] { 2 }, _ => new[] { 1 } };
                    foreach (var f in filterFields)
                        if (f < fields.Count)
                            foreach (var key in FieldForms(fields[f], resolve)) Add(key, framework == "FLM" ? "added" : "filter", kind, fields);
                    break;
                }
                case "BOS":
                {
                    var fields = line.Split('|').Select(f => f.Trim()).ToList();
                    if (fields.Count < 2) continue;
                    var transforms = section.StartsWith("Transforms", StringComparison.OrdinalIgnoreCase);
                    foreach (var key in FieldForms(fields[0], resolve)) Add(key, transforms ? "transformed" : "swapped out", section, fields);
                    if (!transforms)
                        foreach (var key in FieldForms(fields[1], resolve)) Add(key, "swapped in", section, fields);
                    break;
                }
                case "DESC":
                {
                    var bar = line.IndexOf('|');
                    if (bar <= 0) continue;
                    if (Form(line[..bar], resolve) is { } key) Add(key, "description", "", [line[..bar].Trim(), line[(bar + 1)..].Trim()]);
                    break;
                }
            }
        }
    }

    /// <summary>Files read for the forms they name (YAML, JSON, condition text): each form once per line.</summary>
    private static void ReadScan(FrameworkIndex index, string framework, string path, string mod, string[] lines, string text)
    {
        var seen = new HashSet<(FormKey, int)>();
        void Add(FormKey? key, int line)
        {
            if (key is not { } k || !seen.Add((k, line))) return;
            index.All.Add(new FrameworkRef(framework, path, mod, line + 1, lines[Math.Clamp(line, 0, lines.Length - 1)].Trim(), k, "used", "", []));
        }
        // OAR's pairs can run over lines: matched in the whole text, the line from the offset.
        if (framework == "OAR")
        {
            foreach (Match m in OarForm.Matches(text)) Add(Make(m.Groups[1].Value, m.Groups[2].Value), LineOf(text, m.Index));
            return;
        }
        for (var n = 0; n < lines.Length; n++)
        {
            foreach (Match m in TildeForm.Matches(lines[n])) Add(Make(m.Groups[2].Value, m.Groups[1].Value), n);
            foreach (Match m in (framework == "DAR" ? DarForm : PipeForm).Matches(lines[n]))
                Add(Make(m.Groups[1].Value, m.Groups[2].Value), n);
        }
    }

    private static int LineOf(string text, int offset)
    {
        var line = 0;
        for (var i = 0; i < offset && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }
}
