using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// Stage fragments as in the CK's log entry box: Papyrus code per log entry, compiled into the quest's
/// fragment script (QF_…) next to the active file, and the quest's script data (fragment table,
/// Alias_ properties) updated when the compile succeeds.
/// </summary>
internal static partial class QuestEditor
{
    /// <summary>Code typed but not compiled yet: quest → (stage, log entry) → code.</summary>
    private static readonly Dictionary<FormKey, Dictionary<(ushort Stage, int Entry), string>> FragmentEdits = new();

    /// <summary>The quest's fragment script as found among the sources (null script: no .psc).</summary>
    private static readonly Dictionary<FormKey, (string Name, FragmentScript? Script, string? Path)> Sources = new();

    private sealed record Compile(FormKey Quest, Task<PapyrusResult> Task, string Name, List<(ushort Stage, int Entry, string Fragment)> Table, string Text, EditTarget Target);
    private static Compile? _compiling;
    private static (FormKey Quest, PapyrusResult Result, string Text, List<(ushort Stage, int Entry, string Fragment)> Table)? _lastResult;

    public static bool IsCompiling => _compiling is not null;

    /// <summary>Autotests: the last compile (success, error count) and typing a fragment.</summary>
    public static (bool Success, int Errors, string Output)? LastCompile => _lastResult is { } r ? (r.Result.Success, r.Result.Errors.Count, r.Result.Output) : null;

    public static void SetFragmentForTest(FormKey quest, ushort stage, int entry, string code) =>
        (FragmentEdits.TryGetValue(quest, out var edits) ? edits : FragmentEdits[quest] = new())[(stage, entry)] = code;

    private static (string Name, FragmentScript? Script, string? Path) SourceOf(EditorApp app, IQuestGetter quest)
    {
        var name = QuestFragments.ScriptName(quest, app.Session!.Workspace.FormIdText(quest.FormKey));
        if (Sources.TryGetValue(quest.FormKey, out var cached) && cached.Name == name) return cached;
        string? path = null;
        // Our own output first (compiled here), then the load order's sources.
        if (app.Papyrus is { } papyrus)
        {
            if (app.ActivePlugin is { } active && Path.Combine(papyrus.OutputFor(active.Path).Sources, name + ".psc") is var own && File.Exists(own)) path = own;
            path ??= papyrus.SourcePath(name);
        }
        FragmentScript? script = null;
        if (path is not null)
        {
            try { script = FragmentScript.Parse(File.ReadAllText(path)); }
            catch (IOException ex) { Log.Warn($"{path}: {ex.Message}"); }
        }
        return Sources[quest.FormKey] = (name, script, path);
    }

    private static void DrawFragment(EditorApp app, IQuestGetter quest, ushort stage, int entry, EditTarget? target)
    {
        var (name, script, path) = SourceOf(app, quest);
        var fragmentName = QuestFragments.FragmentOf(quest, stage, entry);
        FragmentEdits.TryGetValue(quest.FormKey, out var edits);
        var pending = edits is not null && edits.TryGetValue((stage, entry), out var typed) ? typed : null;
        var existing = fragmentName is not null ? script?.Get(fragmentName)?.Code : null;

        ImGui.AlignTextToFramePadding();
        ImGui.Text(T("Papyrus fragment"));
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextDim, fragmentName is not null ? $"{name}.{fragmentName}" : name);
        if (path is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(path);

        // Without the script's source the other fragments would be lost on the next compile.
        if (script is null && quest.VirtualMachineAdapter?.Fragments.Count > 0)
        {
            ImGui.TextColored(Theme.Warning, TF($"{name}.psc is not among the script sources (Scripts\\Source): its fragments cannot be shown or changed."));
            return;
        }
        var text = pending ?? existing ?? "";
        var editable = target is { Count: 1 };
        if (!editable) ImGui.BeginDisabled();
        var lines = Math.Clamp(text.Count(c => c == '\n') + 2, 3, 14);
        if (ImGui.InputTextMultiline($"##fragment{stage}_{entry}", ref text, 65536, new Vector2(-1, ImGui.GetTextLineHeight() * lines + ImGui.GetStyle().FramePadding.Y * 2), ImGuiInputTextFlags.AllowTabInput))
            (edits ??= FragmentEdits[quest.FormKey] = new())[(stage, entry)] = text;
        if (!editable) ImGui.EndDisabled();

        if (_lastResult is { } last && last.Quest == quest.FormKey && !last.Result.Success)
            foreach (var (line, message) in ErrorsOf(last.Text, last.Table, last.Result, stage, entry))
                ImGui.TextColored(Theme.Error, TF($"line {line}: {message}"));

        if (!editable) return;
        var compiling = _compiling?.Quest == quest.FormKey;
        if (pending is not null && pending != (existing ?? ""))
        {
            ImGui.TextColored(Theme.Warning, T("not compiled"));
            ImGui.SameLine();
        }
        if (compiling) ImGui.BeginDisabled();
        if (ImGui.SmallButton(compiling ? T("compiling…") : T("Compile")) && !compiling) StartCompile(app, quest, target!);
        if (compiling) ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Writes the quest's fragment script next to the active file and compiles it (all changed fragments of the quest)"));
    }

    /// <summary>Errors of a compile that fall in one log entry's fragment, with the line inside the fragment.</summary>
    private static IEnumerable<(int Line, string Message)> ErrorsOf(string text, List<(ushort Stage, int Entry, string Fragment)> table, PapyrusResult result, ushort stage, int entry)
    {
        if (table.FirstOrDefault(t => t.Stage == stage && t.Entry == entry) is not { Fragment: { } fragment }) yield break;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var error in result.Errors)
        {
            var at = error.Line - 1;
            if (at < 0 || at >= lines.Length) continue;
            var begin = -1;
            for (var i = at; i >= 0; i--)
            {
                if (lines[i].StartsWith(";BEGIN FRAGMENT ", StringComparison.Ordinal)) { if (lines[i][";BEGIN FRAGMENT ".Length..].Trim() == fragment) begin = i; break; }
            }
            if (begin < 0) continue;
            var code = Array.FindIndex(lines, begin, l => l.Trim() == ";BEGIN CODE");
            yield return (code >= 0 ? at - code : at - begin, error.Message);
        }
    }

    /// <summary>The result of the last compile of this quest (above the stage), and errors outside any fragment.</summary>
    private static void DrawCompileResult(EditorApp app, IQuestGetter quest)
    {
        if (app.Papyrus?.Problem is { } problem)
        {
            ImGui.TextColored(Theme.Warning, TF($"Papyrus: {problem}"));
            return;
        }
        if (_lastResult is not { } last || last.Quest != quest.FormKey) return;
        if (last.Result.Success) ImGui.TextColored(Theme.TextDim, TF($"Compiled {Path.GetFileName(last.Result.PexPath)}"));
        else
        {
            ImGui.TextColored(Theme.Error, TF($"Compile failed: {last.Result.Errors.Count} error(s)"));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(last.Result.Output.Length > 3000 ? last.Result.Output[..3000] + "…" : last.Result.Output);
        }
    }

    public static void StartCompile(EditorApp app, IQuestGetter quest, EditTarget target)
    {
        if (_compiling is not null) return;
        if (app.ActivePlugin is not { } active)
        {
            Log.Warn(T("Choose the active file first: compiled scripts go next to it"));
            return;
        }
        var papyrus = app.Papyrus!;
        if (papyrus.Problem is { } problem)
        {
            Log.Warn($"Papyrus: {problem}");
            return;
        }
        var (name, script, _) = SourceOf(app, quest);
        var aliases = QuestFragments.AliasProperties(quest).ToList();
        // A copy to change: the parsed source stays as it was if the compile fails.
        var working = script is null ? new FragmentScript { Name = name } : FragmentScript.Parse(script.Write(aliases));
        working.Name = name;
        FragmentEdits.TryGetValue(quest.FormKey, out var edits);
        var table = QuestFragments.Merge(quest, working, edits ?? [], QuestFragments.QuestScript(quest, name));
        var text = working.Write(aliases);
        var (scripts, sources) = papyrus.OutputFor(active.Path);
        Directory.CreateDirectory(sources);
        var psc = Path.Combine(sources, name + ".psc");
        File.WriteAllText(psc, text);
        Log.Info($"compiling {psc} ({table.Count} fragment(s)) into {scripts}");
        _compiling = new Compile(quest.FormKey, Task.Run(() => papyrus.Compile(psc, scripts)), name, table, text, target);
    }

    /// <summary>Every frame: a finished compile updates the quest (one undo step) and the script list.</summary>
    public static void Update(EditorApp app)
    {
        if (_compiling is not { Task.IsCompleted: true } done) return;
        _compiling = null;
        var result = done.Task.IsCompletedSuccessfully ? done.Task.Result : new PapyrusResult(false, done.Task.Exception?.GetBaseException().Message ?? "failed", [], null);
        _lastResult = (done.Quest, result, done.Text, done.Table);
        Sources.Remove(done.Quest);
        if (!result.Success)
        {
            Log.Warn($"{done.Name}: compile failed, {result.Errors.Count} error(s)");
            return;
        }
        Log.Info($"{done.Name}: compiled {result.PexPath}");
        if (app.Session is { } session && result.PexPath is { } pex)
        {
            session.Assets.AddLoose($@"scripts\{done.Name}.pex", pex, T("compiled"));
            ScriptsEditor.Library?.Forget(done.Name);
        }
        FragmentEdits.Remove(done.Quest);
        done.Target.Apply(TF($"Compile fragments {done.Name}"), copy => QuestFragments.Apply((Quest)copy, done.Name, done.Table));
    }
}
