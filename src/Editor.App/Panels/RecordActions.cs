using Mutagen.Bethesda.Plugins;
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
        Log.Info($"remove record: {removal} {id} {key}");
    }
}
