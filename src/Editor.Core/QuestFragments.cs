using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace StbEditor.Core;

/// <summary>
/// A quest's fragment script as the Creation Kit writes it (QF_&lt;EditorID&gt;_&lt;FormID&gt;.psc): a
/// generated block — next fragment index, Scriptname, one property per alias, one Function
/// Fragment_N per stage log entry with code — and after ";END FRAGMENT CODE" the author's own
/// properties, kept as they are.
/// </summary>
public sealed class FragmentScript
{
    public sealed class Fragment
    {
        public required string Name { get; init; }

        /// <summary>The "Function …" line as written (dialogue fragments take akSpeakerRef); null: no parameters.</summary>
        public string? Header { get; set; }

        /// <summary>Lines between "Function" and ";BEGIN CODE" (the kmyQuest autocast), kept.</summary>
        public string Prologue { get; set; } = "";
        public string Code { get; set; } = "";
    }

    public required string Name { get; set; }
    public string Extends { get; set; } = "Quest";
    public int NextIndex { get; set; }
    public List<Fragment> Fragments { get; } = [];

    /// <summary>Everything after ";END FRAGMENT CODE": properties the author added.</summary>
    public string Tail { get; set; } = "";

    private const string Begin = ";BEGIN FRAGMENT CODE - Do not edit anything between this and the end comment";
    private const string End = ";END FRAGMENT CODE - Do not edit anything between this and the begin comment";
    private static readonly Regex ScriptLine = new(@"^\s*Scriptname\s+(\w+)(?:\s+extends\s+(\w+))?", RegexOptions.IgnoreCase);

    public Fragment? Get(string name) => Fragments.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public static FragmentScript Parse(string source)
    {
        var script = new FragmentScript { Name = "" };
        var lines = source.Replace("\r\n", "\n").Split('\n');
        Fragment? current = null;
        StringBuilder? prologue = null, code = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith(";END FRAGMENT CODE", StringComparison.OrdinalIgnoreCase))
            {
                script.Tail = string.Join("\r\n", lines.Skip(i + 1)).TrimEnd() is { Length: > 0 } tail ? tail + "\r\n" : "";
                break;
            }
            if (trimmed.StartsWith(";NEXT FRAGMENT INDEX", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(trimmed[";NEXT FRAGMENT INDEX".Length..].Trim(), out var next))
            {
                script.NextIndex = next;
                continue;
            }
            if (current is null && ScriptLine.Match(line) is { Success: true } header)
            {
                script.Name = header.Groups[1].Value;
                if (header.Groups[2].Success) script.Extends = header.Groups[2].Value;
                continue;
            }
            if (trimmed.StartsWith(";BEGIN FRAGMENT ", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith(";BEGIN FRAGMENT CODE", StringComparison.OrdinalIgnoreCase))
            {
                current = new Fragment { Name = trimmed[";BEGIN FRAGMENT ".Length..].Trim() };
                prologue = new StringBuilder();
                code = null;
                continue;
            }
            if (current is null) continue;
            if (trimmed.StartsWith("Function ", StringComparison.OrdinalIgnoreCase) && code is null && prologue!.Length == 0)
            {
                current.Header = trimmed;
                continue;
            }
            if (trimmed.Equals(";BEGIN CODE", StringComparison.OrdinalIgnoreCase)) { code = new StringBuilder(); continue; }
            if (trimmed.Equals(";END CODE", StringComparison.OrdinalIgnoreCase))
            {
                current.Prologue = prologue!.ToString().TrimEnd();
                current.Code = code?.ToString().TrimEnd() ?? "";
                script.Fragments.Add(current);
                current = null;
                continue;
            }
            if (code is not null) code.Append(line.TrimEnd('\r')).Append('\n');
            else prologue!.Append(line.TrimEnd('\r')).Append('\n');
        }
        // Indices of fragments already there are never handed out again.
        foreach (var f in script.Fragments)
            if (f.Name.StartsWith("Fragment_", StringComparison.Ordinal) && int.TryParse(f.Name["Fragment_".Length..], out var n) && n >= script.NextIndex)
                script.NextIndex = n + 1;
        return script;
    }

    /// <summary>The source as the CK writes it; <paramref name="aliases"/> become Alias_ properties.</summary>
    public string Write(IEnumerable<(string Name, bool IsLocation)> aliases)
    {
        var s = new StringBuilder();
        void Line(string text = "") => s.Append(text).Append("\r\n");
        Line(Begin);
        Line($";NEXT FRAGMENT INDEX {NextIndex}");
        Line($"Scriptname {Name} Extends {Extends} Hidden");
        Line();
        foreach (var (alias, location) in aliases)
        {
            var type = location ? "LocationAlias" : "ReferenceAlias";
            Line($";BEGIN ALIAS PROPERTY {alias}");
            Line($";ALIAS PROPERTY TYPE {type}");
            Line($"{type} Property Alias_{alias} Auto");
            Line(";END ALIAS PROPERTY");
            Line();
        }
        foreach (var fragment in Fragments)
        {
            Line($";BEGIN FRAGMENT {fragment.Name}");
            Line(fragment.Header ?? $"Function {fragment.Name}()");
            if (fragment.Prologue.Length > 0) Line(fragment.Prologue.Replace("\n", "\r\n"));
            Line(";BEGIN CODE");
            if (fragment.Code.Length > 0) Line(fragment.Code.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            Line(";END CODE");
            Line("EndFunction");
            Line(";END FRAGMENT");
            Line();
        }
        Line(End);
        s.Append(Tail);
        return s.ToString();
    }
}

/// <summary>
/// Dialogue line fragments (CK: Begin / End in the Topic Info window): a TIF__&lt;FormID&gt; script extending
/// TopicInfo, each fragment taking the speaker (akSpeakerRef, cast to akSpeaker).
/// </summary>
public static class InfoFragments
{
    public static string ScriptName(IDialogResponsesGetter line, string formIdText)
    {
        if (line.VirtualMachineAdapter?.ScriptFragments?.FileName is { Length: > 0 } name) return name;
        var id = Regex.IsMatch(formIdText, "^[0-9A-Fa-f]{8}$") ? formIdText.ToUpperInvariant() : line.FormKey.ID.ToString("X8");
        return $"TIF__{id}";
    }

    public static string? FragmentOf(IDialogResponsesGetter line, bool begin) =>
        (begin ? line.VirtualMachineAdapter?.ScriptFragments?.OnBegin : line.VirtualMachineAdapter?.ScriptFragments?.OnEnd)?.FragmentName;

    /// <summary>
    /// Puts edited Begin / End code into the script (null: unchanged, blank: removed); returns the
    /// fragment names the line's script data should have.
    /// </summary>
    public static (string? Begin, string? End) Merge(IDialogResponsesGetter line, FragmentScript script, string? beginCode, string? endCode)
    {
        string? One(bool begin, string? code)
        {
            var existing = FragmentOf(line, begin);
            if (code is null) return existing is not null && script.Get(existing) is not null ? existing : existing;
            if (string.IsNullOrWhiteSpace(code))
            {
                if (existing is not null) script.Fragments.RemoveAll(f => string.Equals(f.Name, existing, StringComparison.OrdinalIgnoreCase));
                return null;
            }
            var name = existing ?? $"Fragment_{script.NextIndex++}";
            if (script.Get(name) is { } fragment) fragment.Code = code.TrimEnd();
            else script.Fragments.Add(new FragmentScript.Fragment
            {
                Name = name,
                Header = $"Function {name}(ObjectReference akSpeakerRef)",
                Prologue = "Actor akSpeaker = akSpeakerRef as Actor",
                Code = code.TrimEnd(),
            });
            return name;
        }
        var begin = One(true, beginCode);
        var end = One(false, endCode);
        script.Fragments.RemoveAll(f => !string.Equals(f.Name, begin, StringComparison.OrdinalIgnoreCase) && !string.Equals(f.Name, end, StringComparison.OrdinalIgnoreCase));
        return (begin, end);
    }

    /// <summary>The line's script data after a compile: the TIF script attached, Begin / End pointing at its fragments.</summary>
    public static void Apply(DialogResponses line, string scriptName, string? begin, string? end)
    {
        var vmad = line.VirtualMachineAdapter ??= new DialogResponsesAdapter { Version = 5, ObjectFormat = 2 };
        var fragments = vmad.ScriptFragments ??= new ScriptFragments { ExtraBindDataVersion = 2 };
        fragments.FileName = scriptName;
        fragments.OnBegin = begin is null ? null : new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = scriptName, FragmentName = begin };
        fragments.OnEnd = end is null ? null : new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = scriptName, FragmentName = end };
        if (!vmad.Scripts.Any(s => string.Equals(s.Name, scriptName, StringComparison.OrdinalIgnoreCase)))
            vmad.Scripts.Add(new ScriptEntry { Name = scriptName, Flags = ScriptEntry.Flag.Local });
    }
}

/// <summary>Quest stage fragments: which log entry runs which Fragment_N, kept in the quest's script data (VMAD).</summary>
public static class QuestFragments
{
    /// <summary>The fragment script's name: the one the quest has, else the CK's QF_&lt;EditorID&gt;_&lt;FormID&gt;.</summary>
    public static string ScriptName(IQuestGetter quest, string formIdText)
    {
        if (quest.VirtualMachineAdapter?.FileName is { Length: > 0 } name) return name;
        // Load-order FormID as 8 hex digits; a record of a file outside the load order: its own ID.
        var id = Regex.IsMatch(formIdText, "^[0-9A-Fa-f]{8}$") ? formIdText.ToUpperInvariant() : quest.FormKey.ID.ToString("X8");
        return $"QF_{Regex.Replace(quest.EditorID ?? "Quest", @"\W", "")}_{id}";
    }

    /// <summary>The fragment of a log entry (stage index, entry number), from the quest's table.</summary>
    public static string? FragmentOf(IQuestGetter quest, ushort stage, int entry) =>
        quest.VirtualMachineAdapter?.Fragments.FirstOrDefault(f => f.Stage == stage && f.StageIndex == entry)?.FragmentName;

    /// <summary>
    /// Puts edited code into the script: blank code drops the fragment, new code gets the next
    /// Fragment_N. Returns the table (stage, entry, fragment) the quest's script data should have —
    /// entries whose stage or log entry no longer exists are left out.
    /// </summary>
    public static List<(ushort Stage, int Entry, string Fragment)> Merge(IQuestGetter quest, FragmentScript script, IReadOnlyDictionary<(ushort Stage, int Entry), string> edits, string? autocast = null)
    {
        var table = (quest.VirtualMachineAdapter?.Fragments ?? [])
            .Where(f => quest.Stages.FirstOrDefault(s => s.Index == f.Stage) is { } stage && f.StageIndex >= 0 && f.StageIndex < stage.LogEntries.Count)
            .Select(f => (f.Stage, f.StageIndex, Fragment: f.FragmentName))
            .ToList();
        foreach (var ((stage, entry), code) in edits)
        {
            var at = table.FindIndex(t => t.Stage == stage && t.StageIndex == entry);
            if (string.IsNullOrWhiteSpace(code))
            {
                if (at < 0) continue;
                script.Fragments.RemoveAll(f => string.Equals(f.Name, table[at].Fragment, StringComparison.OrdinalIgnoreCase));
                table.RemoveAt(at);
                continue;
            }
            var name = at >= 0 ? table[at].Fragment : $"Fragment_{script.NextIndex++}";
            if (at < 0) table.Add((stage, entry, name));
            if (script.Get(name) is { } fragment) fragment.Code = code.TrimEnd();
            else script.Fragments.Add(new FragmentScript.Fragment { Name = name, Code = code.TrimEnd(), Prologue = Autocast(autocast) });
        }
        // Fragments nothing points at any more are not written.
        script.Fragments.RemoveAll(f => !table.Any(t => string.Equals(t.Fragment, f.Name, StringComparison.OrdinalIgnoreCase)));
        return table.OrderBy(t => t.Stage).ThenBy(t => t.StageIndex).Select(t => (t.Stage, t.StageIndex, t.Fragment)).ToList();
    }

    /// <summary>
    /// What the CK puts before a new fragment's code when the quest has its own script: kmyQuest, the
    /// quest as that script, so fragments can call its functions.
    /// </summary>
    public static string Autocast(string? type) => type is null ? "" :
        $";BEGIN AUTOCAST TYPE {type}\nQuest __temp = self as Quest\n{type} kmyQuest = __temp as {type}\n;END AUTOCAST";

    /// <summary>The quest's own script (not the fragment script), the type of kmyQuest in fragments.</summary>
    public static string? QuestScript(IQuestGetter quest, string fragmentScript) =>
        quest.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => !string.Equals(s.Name, fragmentScript, StringComparison.OrdinalIgnoreCase))?.Name;

    /// <summary>
    /// The quest's script data after a compile: the fragment script, its fragment table, and one filled
    /// Alias_ property per alias (the CK fills these itself); other properties of the script stay.
    /// </summary>
    public static void Apply(Quest quest, string scriptName, IReadOnlyList<(ushort Stage, int Entry, string Fragment)> table)
    {
        var vmad = quest.VirtualMachineAdapter ??= new QuestAdapter { Version = 5, ObjectFormat = 2, ExtraBindDataVersion = 2 };
        vmad.FileName = table.Count > 0 || vmad.Scripts.Any(s => s.Name == scriptName) ? scriptName : vmad.FileName;
        vmad.Fragments.Clear();
        foreach (var (stage, entry, fragment) in table)
            vmad.Fragments.Add(new QuestScriptFragment { Stage = stage, StageIndex = entry, Unknown2 = 1, ScriptName = scriptName, FragmentName = fragment });

        var script = vmad.Scripts.FirstOrDefault(s => string.Equals(s.Name, scriptName, StringComparison.OrdinalIgnoreCase));
        if (script is null)
        {
            script = new ScriptEntry { Name = scriptName, Flags = ScriptEntry.Flag.Local };
            vmad.Scripts.Add(script);
        }
        var valid = AliasProperties(quest).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aliasNames = valid.Select(n => "Alias_" + n).ToHashSet(StringComparer.OrdinalIgnoreCase);
        script.Properties.RemoveAll(p => p.Name.StartsWith("Alias_", StringComparison.OrdinalIgnoreCase) && !aliasNames.Contains(p.Name));
        foreach (var alias in quest.Aliases.Where(a => a.Name is { } n && valid.Contains(n)))
        {
            var name = "Alias_" + alias.Name;
            if (script.Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is ScriptObjectProperty existing)
            {
                existing.Object.SetTo(quest.FormKey);
                existing.Alias = (short)alias.ID;
                continue;
            }
            script.Properties.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            var property = new ScriptObjectProperty { Name = name, Flags = ScriptProperty.Flag.Edited, Alias = (short)alias.ID };
            property.Object.SetTo(quest.FormKey);
            script.Properties.Add(property);
        }
    }

    /// <summary>Aliases for the script's Alias_ properties (names with spaces cannot be property names).</summary>
    public static IEnumerable<(string Name, bool IsLocation)> AliasProperties(IQuestGetter quest) =>
        quest.Aliases.Where(a => !string.IsNullOrEmpty(a.Name) && Regex.IsMatch(a.Name, @"^\w+$"))
            .Select(a => (a.Name!, a.Type == QuestAlias.TypeEnum.Location));
}
