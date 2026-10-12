using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using StbEditor.App.Panels;
using StbEditor.Core;
using StbEditor.Data;

namespace StbEditor.App.Services;

/// <summary>
/// Text edits of many records at once (Find text → Replace, importing a translation): each record as it is now (this
/// session's edit, a dialogue line under its topic, else the winning version), changed on a working copy and kept
/// where the writer expects it — dialogue lines under their topic, cells and worldspaces as header edits, the rest as
/// top-level records — all in one undo step.
/// </summary>
internal sealed class TextEdits(EditorApp app, string name)
{
    private readonly List<IEditCommand> _steps = [];
    private readonly RecordLookup _lookup = new(app.Session!.Workspace, app.Session.Catalog);
    private readonly Dictionary<FormKey, List<Mutagen.Bethesda.Skyrim.IDialogResponsesGetter>> _lines = new();

    public int Records => _steps.Count;

    /// <summary>The record as it is now (null: not found).</summary>
    public IMajorRecordGetter? Current(FormKey key, FormKey? topic)
    {
        IMajorRecordGetter? current = app.Document.Override(key)?.Record;
        if (current is null && topic is { } t)
        {
            // A topic's lines are looked up once (an import touches many lines of one topic).
            if (!_lines.TryGetValue(t, out var lines)) _lines[t] = lines = DialogueEditor.Lines(app, t).Select(l => l.Line).ToList();
            current = lines.FirstOrDefault(l => l.FormKey == key);
        }
        // A line not under its topic is not looked up by FormID alone (that resolves every record type).
        return current ?? (topic is null ? _lookup.Resolve(key) : null);
    }

    /// <summary>Changes one record (<paramref name="change"/> returns whether it changed anything).</summary>
    public void Edit(FormKey key, FormKey? topic, Func<IMajorRecord, bool> change)
    {
        if (Current(key, topic) is not { } current) return;
        var document = app.Document;
        var copy = document.WorkingCopy(key, current);
        if (!change(copy)) return;
        var before = document.Override(key);
        var after = before?.With(copy) ?? New(copy, key, topic);
        _steps.Add(new RecordEditCommand(name, before?.With((IMajorRecord)before.Record.DeepCopy()), after));
    }

    private OverrideDraft New(IMajorRecord copy, FormKey key, FormKey? topic)
    {
        if (topic is not null) return new OverrideDraft(copy, RecordPlace.DialogInfo, null, topic);
        var catalog = app.Session!.Catalog.Get(key);
        if (catalog?.Signature is "CELL" or "WRLD")
        {
            var world = catalog.Winner.Cell is { IsInterior: false, Worldspace: { } w } ? w : (FormKey?)null;
            return new OverrideDraft(copy, RecordPlace.Header, null, key, world);
        }
        return new OverrideDraft(copy, RecordPlace.TopLevel);
    }

    /// <summary>Records the edits as one undo step.</summary>
    public void Commit()
    {
        if (_steps.Count > 0) app.Document.Record(new CompositeCommand(name, _steps));
    }
}
