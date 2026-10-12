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
/// Begin / End fragments of a dialogue line (CK: Topic Info → Papyrus fragments): the TIF__ script next
/// to the active file, compiled; the line's script data points at its fragments when it compiles.
/// </summary>
internal static partial class DialogueEditor
{
    /// <summary>Typed, not compiled: line → (begin, end); null = unchanged.</summary>
    private static readonly Dictionary<FormKey, (string? Begin, string? End)> FragmentEdits = new();

    private sealed record Job(FormKey Line, Task<PapyrusResult> Task, string Name, string? Begin, string? End, EditTarget Target);
    private static Job? _job;
    private static readonly Dictionary<FormKey, PapyrusResult> LastResults = new();

    public static bool IsCompiling => _job is not null;
    public static PapyrusResult? LastResultFor(FormKey line) => LastResults.GetValueOrDefault(line);

    public static void SetFragmentsForTest(FormKey line, string? begin, string? end) => FragmentEdits[line] = (begin, end);

    private static (string Name, FragmentScript? Script, string? Path) SourceOf(EditorApp app, IDialogResponsesGetter line)
    {
        var name = InfoFragments.ScriptName(line, app.Session!.Workspace.FormIdText(line.FormKey));
        string? path = null;
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
        return (name, script, path);
    }

    private static void DrawFragments(EditorApp app, IDialogResponsesGetter line, EditTarget? target)
    {
        var (name, script, _) = SourceOf(app, line);
        var hasFragments = InfoFragments.FragmentOf(line, true) is not null || InfoFragments.FragmentOf(line, false) is not null;
        ImGui.SeparatorText(TF($"Papyrus fragments ({name})"));
        if (script is null && hasFragments)
        {
            ImGui.TextColored(Theme.Warning, TF($"{name}.psc is not among the script sources (Scripts\\Source): its fragments cannot be shown or changed."));
            return;
        }
        FragmentEdits.TryGetValue(line.FormKey, out var edits);
        string Existing(bool begin) => InfoFragments.FragmentOf(line, begin) is { } f ? script?.Get(f)?.Code ?? "" : "";
        var editable = target is { Count: 1 };
        var changed = false;
        foreach (var begin in new[] { true, false })
        {
            var text = (begin ? edits.Begin : edits.End) ?? Existing(begin);
            ImGui.Text(begin ? T("Begin") : T("End"));
            if (!editable) ImGui.BeginDisabled();
            var lines = Math.Clamp(text.Count(c => c == '\n') + 2, 2, 10);
            if (ImGui.InputTextMultiline(begin ? "##begin" : "##end", ref text, 65536, new Vector2(-1, ImGui.GetTextLineHeight() * lines + ImGui.GetStyle().FramePadding.Y * 2), ImGuiInputTextFlags.AllowTabInput))
            {
                edits = begin ? (text, edits.End) : (edits.Begin, text);
                FragmentEdits[line.FormKey] = edits;
            }
            if (!editable) ImGui.EndDisabled();
            changed |= (begin ? edits.Begin : edits.End) is { } typed && typed != Existing(begin);
        }
        if (LastResults.TryGetValue(line.FormKey, out var last) && !last.Success)
        {
            ImGui.TextColored(Theme.Error, TF($"Compile failed: {last.Errors.Count} error(s)"));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(last.Output.Length > 3000 ? last.Output[..3000] + "…" : last.Output);
            foreach (var error in last.Errors.Take(5)) ImGui.TextColored(Theme.Error, TF($"line {error.Line}: {error.Message}"));
        }
        if (!editable) return;
        if (changed)
        {
            ImGui.TextColored(Theme.Warning, T("not compiled"));
            ImGui.SameLine();
        }
        var busy = _job?.Line == line.FormKey;
        if (busy) ImGui.BeginDisabled();
        if (ImGui.SmallButton(busy ? T("compiling…") : T("Compile")) && !busy) StartCompile(app, line, target!);
        if (busy) ImGui.EndDisabled();
    }

    public static void StartCompile(EditorApp app, IDialogResponsesGetter line, EditTarget target)
    {
        if (_job is not null) return;
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
        var (name, script, _) = SourceOf(app, line);
        var working = script is null ? new FragmentScript { Name = name, Extends = "TopicInfo" } : FragmentScript.Parse(script.Write([]));
        working.Name = name;
        working.Extends = "TopicInfo";
        FragmentEdits.TryGetValue(line.FormKey, out var edits);
        var (begin, end) = InfoFragments.Merge(line, working, edits.Begin, edits.End);
        var text = working.Write([]);
        var (scripts, sources) = papyrus.OutputFor(active.Path);
        Directory.CreateDirectory(sources);
        var psc = Path.Combine(sources, name + ".psc");
        File.WriteAllText(psc, text);
        Log.Info($"compiling {psc} into {scripts}");
        _job = new Job(line.FormKey, Task.Run(() => papyrus.Compile(psc, scripts)), name, begin, end, target);
    }

    /// <summary>Every frame: a finished compile points the line at its fragments (one undo step).</summary>
    public static void Update(EditorApp app)
    {
        if (_job is not { Task.IsCompleted: true } done) return;
        _job = null;
        var result = done.Task.IsCompletedSuccessfully ? done.Task.Result : new PapyrusResult(false, done.Task.Exception?.GetBaseException().Message ?? "failed", [], null);
        LastResults[done.Line] = result;
        if (!result.Success)
        {
            Log.Warn($"{done.Name}: compile failed, {result.Errors.Count} error(s)");
            return;
        }
        Log.Info($"{done.Name}: compiled {result.PexPath}");
        if (app.Session is { } session && result.PexPath is { } pex) session.Assets.AddLoose($@"scripts\{done.Name}.pex", pex, T("compiled"));
        ScriptsEditor.Library?.Forget(done.Name);
        FragmentEdits.Remove(done.Line);
        done.Target.Apply(TF($"Compile fragments {done.Name}"), copy => InfoFragments.Apply((DialogResponses)copy, done.Name, done.Begin, done.End));
    }
}
