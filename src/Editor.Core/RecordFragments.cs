using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace StbEditor.Core;

/// <summary>
/// Papyrus fragments of packages (PF_ scripts: Begin / End / Change, each taking akActor), scenes (SF_ scripts:
/// Begin / End of the scene and the start / completion of each phase, no parameters) and perks (PRKF_ scripts: one per
/// «Activate» entry, taking akTargetRef and akActor), as the Creation Kit writes them (checked on the 350 PF_, 587 SF_
/// and 9 PRKF_ sources of the game). A record's fragments are a table slot → Fragment_N; slots are "Begin", "End",
/// "Change", "Phase{i}Start", "Phase{i}End", "Entry{i}" (i from 0: the phase or the perk entry).
/// </summary>
public static class RecordFragments
{
    public static bool Supports(IMajorRecordGetter record) => record is IPackageGetter or ISceneGetter or IPerkGetter;

    public static string Extends(IMajorRecordGetter record) => record switch { IPackageGetter => "Package", IPerkGetter => "Perk", _ => "Scene" };

    /// <summary>The "Function …" line of a new fragment.</summary>
    public static string Header(IMajorRecordGetter record, string name) => record switch
    {
        IPackageGetter => $"Function {name}(Actor akActor)",
        IPerkGetter => $"Function {name}(ObjectReference akTargetRef, Actor akActor)",
        _ => $"Function {name}()",
    };

    /// <summary>
    /// The fragment script's name: the one the record has, else the CK's PF_ / SF_&lt;EditorID&gt;_&lt;FormID&gt; (the CK
    /// cuts the EditorID to 25 characters).
    /// </summary>
    public static string ScriptName(IMajorRecordGetter record, string formIdText)
    {
        if (FileName(record) is { Length: > 0 } name) return name;
        var id = Regex.IsMatch(formIdText, "^[0-9A-Fa-f]{8}$") ? formIdText.ToUpperInvariant() : record.FormKey.ID.ToString("X8");
        var editorId = Regex.Replace(record.EditorID ?? "", @"\W", "");
        if (editorId.Length > 25) editorId = editorId[..25];
        return $"{record switch { IPackageGetter => "PF", IPerkGetter => "PRKF", _ => "SF" }}_{editorId}_{id}";
    }

    private static string? FileName(IMajorRecordGetter record) => record switch
    {
        IPackageGetter p => p.VirtualMachineAdapter?.ScriptFragments?.FileName,
        ISceneGetter s => s.VirtualMachineAdapter?.ScriptFragments?.FileName,
        IPerkGetter k => k.VirtualMachineAdapter?.ScriptFragments?.FileName,
        _ => null,
    };

    /// <summary>The slots a record offers, in the CK's order.</summary>
    public static IReadOnlyList<string> Slots(IMajorRecordGetter record) => record switch
    {
        IPackageGetter => ["Begin", "End", "Change"],
        ISceneGetter s => ["Begin", "End", .. Enumerable.Range(0, s.Phases.Count).SelectMany(i => new[] { $"Phase{i}Start", $"Phase{i}End" })],
        // Only «Activate» entries run a fragment (CK: Run Script on the activate choice).
        IPerkGetter k => [.. k.Effects.Select((e, i) => (e, i)).Where(x => x.e is IPerkEntryPointAddActivateChoiceGetter).Select(x => $"Entry{x.i}")],
        _ => [],
    };

    /// <summary>Slot → fragment name, from the record's script data.</summary>
    public static Dictionary<string, string> Table(IMajorRecordGetter record)
    {
        var table = new Dictionary<string, string>();
        void Put(string slot, IScriptFragmentGetter? fragment)
        {
            if (fragment?.FragmentName is { Length: > 0 } name) table[slot] = name;
        }
        switch (record)
        {
            case IPackageGetter { VirtualMachineAdapter.ScriptFragments: { } f }:
                Put("Begin", f.OnBegin);
                Put("End", f.OnEnd);
                Put("Change", f.OnChange);
                break;
            case ISceneGetter { VirtualMachineAdapter.ScriptFragments: { } f }:
                Put("Begin", f.OnBegin);
                Put("End", f.OnEnd);
                foreach (var phase in f.PhaseFragments)
                    if (phase.FragmentName is { Length: > 0 } name)
                        table[$"Phase{phase.Index}{(phase.Flags.HasFlag(ScenePhaseFragment.Flag.OnCompletion) ? "End" : "Start")}"] = name;
                break;
            case IPerkGetter { VirtualMachineAdapter.ScriptFragments: { } f }:
                foreach (var fragment in f.Fragments)
                    if (fragment.FragmentName is { Length: > 0 } name) table[$"Entry{fragment.FragmentIndex}"] = name;
                break;
        }
        return table;
    }

    /// <summary>
    /// Puts edited code into the script (slot → code; blank removes the fragment, new code gets the next Fragment_N);
    /// returns the table the record's script data should have. Fragments nothing points at are dropped, as are
    /// slots the record no longer has (a removed phase).
    /// </summary>
    public static Dictionary<string, string> Merge(IMajorRecordGetter record, FragmentScript script, IReadOnlyDictionary<string, string> edits)
    {
        var slots = Slots(record).ToHashSet();
        var table = Table(record).Where(kv => slots.Contains(kv.Key)).ToDictionary();
        foreach (var (slot, code) in edits)
        {
            if (!slots.Contains(slot)) continue;
            if (string.IsNullOrWhiteSpace(code))
            {
                table.Remove(slot);
                continue;
            }
            if (!table.TryGetValue(slot, out var name)) table[slot] = name = $"Fragment_{script.NextIndex++}";
            if (script.Get(name) is { } fragment) fragment.Code = code.TrimEnd();
            else script.Fragments.Add(new FragmentScript.Fragment { Name = name, Header = Header(record, name), Code = code.TrimEnd() });
        }
        script.Fragments.RemoveAll(f => !table.Values.Contains(f.Name, StringComparer.OrdinalIgnoreCase));
        // A slot pointing at a function the source does not have (38 in the game's data) would point the compiled
        // record at nothing: it goes.
        foreach (var slot in table.Where(kv => script.Get(kv.Value) is null).Select(kv => kv.Key).ToList()) table.Remove(slot);
        return table;
    }

    /// <summary>The record's script data after a compile: the fragment script attached, its slots pointing at the fragments.</summary>
    public static void Apply(IMajorRecord record, string scriptName, IReadOnlyDictionary<string, string> table)
    {
        ScriptFragment? One(string slot) => table.TryGetValue(slot, out var name)
            ? new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = scriptName, FragmentName = name }
            : null;
        IList<ScriptEntry> scripts;
        switch (record)
        {
            case Package package:
            {
                var vmad = package.VirtualMachineAdapter ??= new PackageAdapter { Version = 5, ObjectFormat = 2 };
                var fragments = vmad.ScriptFragments ??= new PackageScriptFragments { ExtraBindDataVersion = 2 };
                fragments.FileName = scriptName;
                fragments.OnBegin = One("Begin");
                fragments.OnEnd = One("End");
                fragments.OnChange = One("Change");
                scripts = vmad.Scripts;
                break;
            }
            case Scene scene:
            {
                var vmad = scene.VirtualMachineAdapter ??= new SceneAdapter { Version = 5, ObjectFormat = 2 };
                var fragments = vmad.ScriptFragments ??= new SceneScriptFragments { ExtraBindDataVersion = 2 };
                fragments.FileName = scriptName;
                fragments.OnBegin = One("Begin");
                fragments.OnEnd = One("End");
                fragments.PhaseFragments.Clear();
                for (var i = 0; i < scene.Phases.Count; i++)
                    foreach (var (suffix, flag) in new[] { ("Start", ScenePhaseFragment.Flag.OnStart), ("End", ScenePhaseFragment.Flag.OnCompletion) })
                        if (table.TryGetValue($"Phase{i}{suffix}", out var name))
                            fragments.PhaseFragments.Add(new ScenePhaseFragment { Flags = flag, Index = (byte)i, Unknown = 0x01000000, ScriptName = scriptName, FragmentName = name });
                scripts = vmad.Scripts;
                break;
            }
            case Perk perk:
            {
                var vmad = perk.VirtualMachineAdapter ??= new PerkAdapter { Version = 5, ObjectFormat = 2 };
                var fragments = vmad.ScriptFragments ??= new PerkScriptFragments { ExtraBindDataVersion = 2 };
                fragments.FileName = scriptName;
                fragments.Fragments.Clear();
                for (var i = 0; i < perk.Effects.Count; i++)
                    if (table.TryGetValue($"Entry{i}", out var name))
                        fragments.Fragments.Add(new IndexedScriptFragment { FragmentIndex = (ushort)i, Unknown = 0, Unknown2 = 1, ScriptName = scriptName, FragmentName = name });
                scripts = vmad.Scripts;
                break;
            }
            default:
                throw new ArgumentException($"{record.GetType().Name} has no fragments here");
        }
        if (!scripts.Any(s => string.Equals(s.Name, scriptName, StringComparison.OrdinalIgnoreCase)))
            scripts.Add(new ScriptEntry { Name = scriptName, Flags = ScriptEntry.Flag.Local });
    }
}
