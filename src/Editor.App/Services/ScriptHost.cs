using System.Text;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Core;
using StbEditor.Data;

namespace StbEditor.App.Services;

/// <summary>
/// What a user script sees (xEdit: Apply Script, here in C#): the load order's records, the Object Window's selection,
/// edits that become one undo step in the active file, and an output pane. Scripts run on a worker; their edits are
/// committed on the UI thread when they finish, only if the script did not throw.
/// </summary>
public sealed class ScriptGlobals
{
    private readonly EditorSession _session;
    private readonly RecordLookup _lookup;
    private readonly EditDocument _document;
    private readonly StringBuilder _output;
    internal readonly Dictionary<FormKey, IMajorRecord> Edits = new();

    internal ScriptGlobals(EditorSession session, EditDocument document, IReadOnlyList<FormKey> selected, StringBuilder output, CancellationToken cancel)
    {
        _session = session;
        _lookup = new RecordLookup(session.Workspace, session.Catalog);
        _document = document;
        _output = output;
        Selected = selected;
        Cancel = cancel;
    }

    /// <summary>The records picked in the Object Window (Ctrl/Shift click), or the one selected.</summary>
    public IReadOnlyList<FormKey> Selected { get; }

    /// <summary>Set when Stop is pressed: long loops should check it (<c>Cancel.ThrowIfCancellationRequested()</c>).</summary>
    public CancellationToken Cancel { get; }

    /// <summary>Plugins in load order.</summary>
    public IReadOnlyList<string> Plugins => _session.Workspace.Active.Select(p => p.Name).ToList();

    /// <summary>The winning version of every record of a type: <c>Records&lt;IWeaponGetter&gt;()</c> (the script's own edits included).</summary>
    public IEnumerable<T> Records<T>() where T : class, IMajorRecordGetter
    {
        var signature = Signature(typeof(T)) ?? throw new ArgumentException($"{typeof(T).Name} is not a record type a script can list");
        foreach (var entry in _session.Catalog.BySignature.GetValueOrDefault(signature) ?? [])
        {
            Cancel.ThrowIfCancellationRequested();
            if (Get<T>(entry.FormKey) is { } record) yield return record;
        }
    }

    /// <summary>One record as it is now (this script's edit, the editor's unsaved edit, else the winning version).</summary>
    public T? Get<T>(FormKey key) where T : class, IMajorRecordGetter
        => Edits.TryGetValue(key, out var edited) ? edited as T : _document.Override(key)?.Record as T ?? _lookup.Resolve<T>(key);

    /// <summary>Any record as it is now, whatever its type.</summary>
    public IMajorRecordGetter? Get(FormKey key)
        => Edits.TryGetValue(key, out var edited) ? edited : _document.Override(key)?.Record ?? _lookup.Resolve(key);

    /// <summary>Any record by FormKey ("012E49:Skyrim.esm"), FormID ("00012E49") or EditorID.</summary>
    public IMajorRecordGetter? Find(string reference)
    {
        var key = _session.Workspace.ParseFormReference(reference)
            ?? _session.Catalog.ByKey.Values.FirstOrDefault(r => string.Equals(r.EditorId, reference, StringComparison.OrdinalIgnoreCase))?.FormKey;
        if (key is not { } k) return null;
        if (Edits.TryGetValue(k, out var edited)) return edited;
        return _document.Override(k)?.Record ?? _lookup.Resolve(k);
    }

    /// <summary>
    /// Changes a record: <c>Edit&lt;Weapon&gt;(weapon, w =&gt; w.BasicStats!.Damage += 2)</c>. The change goes to a copy that
    /// becomes an override in the active file (with the run's other edits as one undo step) when the script ends.
    /// </summary>
    public T Edit<T>(IMajorRecordGetter record, Action<T> change) where T : class, IMajorRecord
    {
        if (!Edits.TryGetValue(record.FormKey, out var copy))
        {
            var source = _document.Override(record.FormKey)?.Record ?? record;
            copy = (IMajorRecord)source.DeepCopy();
            Edits[record.FormKey] = copy;
        }
        var typed = copy as T ?? throw new ArgumentException($"{record.EditorID} is a {copy.GetType().Name}, not a {typeof(T).Name}");
        change(typed);
        return typed;
    }

    /// <summary>A line in the output pane.</summary>
    public void Print(object? value) { lock (_output) _output.AppendLine(value?.ToString() ?? "null"); }

    /// <summary>A record's EditorID and FormID, for printing.</summary>
    public string Name(IMajorRecordGetter record) => $"{record.EditorID ?? "?"} [{_session.Workspace.FormIdText(record.FormKey)}]";

    private static string? Signature(Type getter) => RecordLookup.SignatureOf(getter);
}

/// <summary>Compiles and runs user scripts (C# script syntax, .csx).</summary>
internal static class ScriptHost
{
    public sealed record Outcome(string Output, IReadOnlyList<IMajorRecord> Edits, string? Error, TimeSpan Time);

    private static readonly ScriptOptions Options = ScriptOptions.Default
        .AddReferences(typeof(ISkyrimModGetter).Assembly, typeof(FormKey).Assembly, typeof(IMajorRecordGetter).Assembly, typeof(Noggog.P3Float).Assembly,
            typeof(ScriptGlobals).Assembly, typeof(Enumerable).Assembly, typeof(System.Drawing.Color).Assembly, typeof(RecordLookup).Assembly)
        .AddImports("System", "System.Linq", "System.Collections.Generic", "Mutagen.Bethesda", "Mutagen.Bethesda.Skyrim", "Mutagen.Bethesda.Plugins",
            "Mutagen.Bethesda.Plugins.Records", "Noggog");

    /// <summary>Compile errors only (for the editor's Check button); null when it compiles.</summary>
    public static string? Check(string code)
    {
        var diagnostics = CSharpScript.Create(code, Options, typeof(ScriptGlobals)).Compile();
        var errors = diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
        return errors.Count == 0 ? null : string.Join("\n", errors.Select(e => e.ToString()));
    }

    public static async Task<Outcome> Run(string code, EditorSession session, EditDocument document, IReadOnlyList<FormKey> selected, CancellationToken cancel)
    {
        var output = new StringBuilder();
        var globals = new ScriptGlobals(session, document, selected, output, cancel);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var state = await CSharpScript.RunAsync(code, Options, globals, typeof(ScriptGlobals), cancel);
            if (state.ReturnValue is { } value) output.AppendLine(value.ToString());
            return new Outcome(output.ToString(), [.. globals.Edits.Values], null, clock.Elapsed);
        }
        catch (CompilationErrorException ex) { return new Outcome(output.ToString(), [], string.Join("\n", ex.Diagnostics), clock.Elapsed); }
        catch (OperationCanceledException) { return new Outcome(output.ToString(), [], "stopped", clock.Elapsed); }
        catch (Exception ex) { return new Outcome(output.ToString(), [], ex.ToString(), clock.Elapsed); }
    }
}
