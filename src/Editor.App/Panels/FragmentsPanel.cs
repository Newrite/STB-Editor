using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// Papyrus fragments of packages, scenes and perks (CK: the Begin / End / Change boxes of a package, the scene's and
/// its phases' fragments, a perk's «Activate» entries): the PF_ / SF_ script next to the active file, compiled; the record's script data points at
/// its fragments when it compiles (one undo step). Same flow as quest stage and dialogue line fragments.
/// </summary>
internal static class FragmentsPanel
{
    /// <summary>Typed, not compiled: record → slot → code.</summary>
    private static readonly Dictionary<FormKey, Dictionary<string, string>> Edits = new();

    private sealed record Job(FormKey Record, Task<PapyrusResult> Task, string Name, Dictionary<string, string> Table, EditTarget Target);
    private static Job? _job;
    private static readonly Dictionary<FormKey, PapyrusResult> LastResults = new();

    public static bool IsCompiling => _job is not null;
    public static PapyrusResult? LastResultFor(FormKey record) => LastResults.GetValueOrDefault(record);

    public static void SetForTest(FormKey record, string slot, string code) =>
        (Edits.TryGetValue(record, out var map) ? map : Edits[record] = new())[slot] = code;

    private static string SlotLabel(IMajorRecordGetter record, string slot)
    {
        if (slot.StartsWith("Entry", StringComparison.Ordinal) && int.TryParse(slot[5..], out var entry))
        {
            var label = record is Mutagen.Bethesda.Skyrim.IPerkGetter perk && entry < perk.Effects.Count
                && perk.Effects[entry] is Mutagen.Bethesda.Skyrim.IPerkEntryPointAddActivateChoiceGetter choice ? choice.ButtonLabel?.String : null;
            return string.IsNullOrEmpty(label) ? TF($"Perk entry {entry + 1}: activate") : TF($"Perk entry {entry + 1}: activate \"{label}\"");
        }
        if (!slot.StartsWith("Phase", StringComparison.Ordinal)) return T(slot);
        var end = slot.EndsWith("End", StringComparison.Ordinal);
        var number = int.Parse(slot[5..^(end ? 3 : 5)]) + 1;
        return end ? TF($"Phase {number}: completion") : TF($"Phase {number}: start");
    }

    private static (string Name, FragmentScript? Script) SourceOf(EditorApp app, IMajorRecordGetter record)
    {
        var name = RecordFragments.ScriptName(record, app.Session!.Workspace.FormIdText(record.FormKey));
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
        return (name, script);
    }

    /// <summary>
    /// The fragment boxes of <paramref name="slots"/> (all of the record's when null: a scene's phase shows only its own).
    /// </summary>
    public static void Draw(EditorApp app, IMajorRecordGetter record, EditTarget? target, IReadOnlyList<string>? slots = null)
    {
        var (name, script) = SourceOf(app, record);
        var table = RecordFragments.Table(record);
        slots ??= RecordFragments.Slots(record);
        ImGui.PushID("fragments" + string.Join(",", slots));
        ImGui.SeparatorText(TF($"Papyrus fragments ({name})"));
        if (script is null && table.Count > 0)
        {
            ImGui.TextColored(Theme.Warning, TF($"{name}.psc is not among the script sources (Scripts\\Source): its fragments cannot be shown or changed."));
            ImGui.PopID();
            return;
        }
        Edits.TryGetValue(record.FormKey, out var edits);
        string Existing(string slot) => table.TryGetValue(slot, out var f) ? script?.Get(f)?.Code ?? "" : "";
        var editable = target is { Count: 1 };
        var changed = false;
        foreach (var slot in slots)
        {
            var text = edits?.GetValueOrDefault(slot) ?? Existing(slot);
            ImGui.Text(SlotLabel(record, slot));
            if (!editable) ImGui.BeginDisabled();
            var lines = Math.Clamp(text.Count(c => c == '\n') + 2, 2, 10);
            if (ImGui.InputTextMultiline("##" + slot, ref text, 65536, new Vector2(-1, ImGui.GetTextLineHeight() * lines + ImGui.GetStyle().FramePadding.Y * 2), ImGuiInputTextFlags.AllowTabInput))
            {
                edits ??= Edits[record.FormKey] = new();
                edits[slot] = text;
            }
            if (!editable) ImGui.EndDisabled();
            changed |= edits?.GetValueOrDefault(slot) is { } typed && typed != Existing(slot);
        }
        if (LastResults.TryGetValue(record.FormKey, out var last) && !last.Success)
        {
            ImGui.TextColored(Theme.Error, TF($"Compile failed: {last.Errors.Count} error(s)"));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(last.Output.Length > 3000 ? last.Output[..3000] + "…" : last.Output);
            foreach (var error in last.Errors.Take(5)) ImGui.TextColored(Theme.Error, TF($"line {error.Line}: {error.Message}"));
        }
        if (editable)
        {
            if (changed)
            {
                ImGui.TextColored(Theme.Warning, T("not compiled"));
                ImGui.SameLine();
            }
            var busy = _job?.Record == record.FormKey;
            if (busy) ImGui.BeginDisabled();
            if (ImGui.SmallButton(busy ? T("compiling…") : T("Compile")) && !busy) StartCompile(app, record, target!);
            if (busy) ImGui.EndDisabled();
        }
        ImGui.PopID();
    }

    public static void StartCompile(EditorApp app, IMajorRecordGetter record, EditTarget target)
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
        var (name, script) = SourceOf(app, record);
        var working = script is null ? new FragmentScript { Name = name } : FragmentScript.Parse(script.Write([]));
        working.Name = name;
        working.Extends = RecordFragments.Extends(record);
        var table = RecordFragments.Merge(record, working, Edits.GetValueOrDefault(record.FormKey) ?? new());
        var text = working.Write([]);
        var (scripts, sources) = papyrus.OutputFor(active.Path);
        Directory.CreateDirectory(sources);
        var psc = Path.Combine(sources, name + ".psc");
        File.WriteAllText(psc, text);
        Log.Info($"compiling {psc} into {scripts}");
        _job = new Job(record.FormKey, Task.Run(() => papyrus.Compile(psc, scripts)), name, table, target);
    }

    /// <summary>Every frame: a finished compile points the record at its fragments (one undo step).</summary>
    public static void Update(EditorApp app)
    {
        if (_job is not { Task.IsCompleted: true } done) return;
        _job = null;
        var result = done.Task.IsCompletedSuccessfully ? done.Task.Result : new PapyrusResult(false, done.Task.Exception?.GetBaseException().Message ?? "failed", [], null);
        LastResults[done.Record] = result;
        if (!result.Success)
        {
            Log.Warn($"{done.Name}: compile failed, {result.Errors.Count} error(s)");
            return;
        }
        Log.Info($"{done.Name}: compiled {result.PexPath}");
        if (app.Session is { } session && result.PexPath is { } pex) session.Assets.AddLoose($@"scripts\{done.Name}.pex", pex, T("compiled"));
        ScriptsEditor.Library?.Forget(done.Name);
        Edits.Remove(done.Record);
        done.Target.Apply(TF($"Compile fragments {done.Name}"), copy => RecordFragments.Apply(copy, done.Name, done.Table));
    }
}
