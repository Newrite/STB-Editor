using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using StbEditor.App.Services;
using StbEditor.Core;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>Removing records from the active file (Object Window, forms): what can be removed and how it reads.</summary>
internal static class RecordActions
{
    public enum Removal
    {
        /// <summary>Nothing of it is in the active file (another plugin's record: never deleted, it would break that plugin's users).</summary>
        None,

        /// <summary>A new record of this session: dropped.</summary>
        NewRecord,

        /// <summary>An unsaved edit of a record the active file does not have yet: the edit is dropped.</summary>
        UnsavedEdit,

        /// <summary>The active file's own record: deleted from the file on save.</summary>
        OwnRecord,

        /// <summary>The active file's override of another plugin's record: taken out, the previous version wins again.</summary>
        Override,
    }

    public static Removal RemovalOf(EditorApp app, FormKey key)
    {
        if (app.Session is not { } session || app.ActivePlugin is not { } active) return Removal.None;
        if (app.Document.IsRemoved(key)) return Removal.None;
        var record = session.Catalog.Get(key);
        var onDisk = record is { IsTransient: false } && record.Plugins.Any(p => p.ModKey == active.ModKey);
        if (!onDisk)
        {
            if (app.Document.Override(key) is not { Place: RecordPlace.TopLevel }) return Removal.None;
            return record is null or { IsTransient: true } ? Removal.NewRecord : Removal.UnsavedEdit;
        }
        return key.ModKey == active.ModKey ? Removal.OwnRecord : Removal.Override;
    }

    /// <summary>The menu item for removing <paramref name="key"/> ("Delete from X" / "Remove override from X").</summary>
    public static string Label(EditorApp app, Removal removal) => removal switch
    {
        Removal.NewRecord => T("Delete new record"),
        Removal.UnsavedEdit => T("Discard unsaved edit"),
        Removal.OwnRecord => TF($"Delete from {app.ActivePlugin!.Name}"),
        Removal.Override => TF($"Remove override from {app.ActivePlugin!.Name}"),
        _ => T("Delete"),
    };

    /// <summary>Tooltip, with how many records and references point at an own record about to be deleted.</summary>
    public static string Tooltip(EditorApp app, Removal removal, FormKey key)
    {
        var text = Tooltip(app, removal);
        if (removal == Removal.OwnRecord && app.Session?.Uses is { IsCompletedSuccessfully: true } uses && uses.Result.CountOf(key) is > 0 and var n)
            text += Environment.NewLine + TF($"Up to {n} link(s) point at it — check Use Info first.");
        return text;
    }

    public static string Tooltip(EditorApp app, Removal removal) => removal switch
    {
        Removal.NewRecord => T("The record created in this session goes (Ctrl+Z brings it back)"),
        Removal.UnsavedEdit => T("Your unsaved changes to this record go (Ctrl+Z brings them back)"),
        Removal.OwnRecord => T("Deleted from the file on save (Ctrl+Z before saving brings it back). References placed from it and links to it stop working."),
        Removal.Override => T("Your plugin's version is taken out on save: the previous plugin's version wins again"),
        _ => app.ActivePlugin is null
            ? T("Choose the active file first (File → Active File)")
            : T("Not in the active file. Records of other plugins are not deleted (it breaks what uses them)."),
    };

    /// <summary>Types copied as overrides (cells, worlds, quests and dialogue carry children or need their own tools).</summary>
    private static readonly HashSet<string> NotCopyable = ["CELL", "WRLD", "LAND", "NAVM", "NAVI", "DIAL", "INFO", "QUST", "SCEN", "TES4"];

    /// <summary>Records that "Copy as override" would put into the active file (not already there, copyable types).</summary>
    public static List<FormKey> CopyableAsOverride(EditorApp app, IEnumerable<FormKey> keys)
    {
        if (app.Session is not { } session || app.ActivePlugin is not { } active) return [];
        return keys.Where(k => session.Catalog.Get(k) is { IsTransient: false } r && !NotCopyable.Contains(r.Signature)
            && !r.Plugins.Any(p => p.ModKey == active.ModKey) && app.Document.Draft(k) is null).ToList();
    }

    /// <summary>
    /// xEdit's "Copy as override into…": the winning version goes into the active file unchanged, as
    /// one undo step — to edit it there or to carry it forward over later plugins.
    /// </summary>
    public static void CopyAsOverride(EditorApp app, IReadOnlyList<FormKey> keys)
    {
        var edits = new List<(Mutagen.Bethesda.Plugins.Records.IMajorRecord, RecordPlace, RefContext?)>();
        foreach (var key in CopyableAsOverride(app, keys))
        {
            if (CompareWindows.Versions(app, key).FirstOrDefault().Record is not { } winner) continue;
            edits.Add(((Mutagen.Bethesda.Plugins.Records.IMajorRecord)winner.DeepCopy(), RecordPlace.TopLevel, null));
        }
        if (edits.Count == 0) return;
        app.Document.CommitEdits(edits.Count == 1 ? TF($"Copy {edits[0].Item1.EditorID} as override") : TF($"Copy {edits.Count} records as overrides"), edits);
        Log.Info($"copied {edits.Count} record(s) as override(s) into {app.ActivePlugin!.Name}");
    }

    public static void Remove(EditorApp app, FormKey key)
    {
        var removal = RemovalOf(app, key);
        if (removal == Removal.None || app.Session is not { } session) return;
        var record = session.Catalog.Get(key);
        var id = record?.EditorId ?? app.Document.Override(key)?.Record.EditorID ?? session.Workspace.FormIdText(key);
        var signature = record?.Signature ?? StbEditor.Data.Catalog.RecordCatalog.SignatureOf(app.Document.Override(key)!.Record);
        var name = removal switch
        {
            Removal.Override => TF($"Remove override {id}"),
            Removal.UnsavedEdit => TF($"Discard edit of {id}"),
            _ => TF($"Delete {id}"),
        };
        app.Document.RemoveRecord(name, key, signature, id, inPlugin: removal is Removal.OwnRecord or Removal.Override, ownRecord: removal == Removal.OwnRecord);
        if (removal == Removal.OwnRecord && session.Uses is { IsCompletedSuccessfully: true } uses && uses.Result.CountOf(key) is > 0 and var n)
            Log.Warn($"{id} is deleted on save, but up to {n} link(s) point at it (Use Info lists them); Ctrl+Z brings it back");
        Log.Info($"remove record: {removal} {id} {key}");
    }
}
