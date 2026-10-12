using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace StbEditor.Core;

/// <summary>Where an edited record goes in the plugin.</summary>
public enum RecordPlace
{
    /// <summary>A top-level record (STAT, NPC_, ACTI…), in its type's group.</summary>
    TopLevel,

    /// <summary>A placed reference (REFR/ACHR) in its cell.</summary>
    Reference,

    /// <summary>A navmesh (NAVM) under its cell.</summary>
    Navmesh,
}

/// <summary>
/// An edited copy of a record (field editors, reference properties, scripts, navmesh). The copy is
/// a mutable Mutagen record; saving puts it into the active plugin as an override (or as the
/// plugin's own record when it is one). References keep their placement in <see cref="RefState"/>:
/// the writer applies that on top.
/// </summary>
public sealed class OverrideDraft(IMajorRecord record, RecordPlace place, RefContext? reference = null, FormKey? cell = null, FormKey? world = null)
    : Draft(record.FormKey)
{
    public IMajorRecord Record { get; } = record;
    public RecordPlace Place { get; } = place;

    /// <summary>For references: where they live and what they are.</summary>
    public RefContext? Reference { get; } = reference;

    /// <summary>For navmeshes: the owning cell and its worldspace (null for interiors).</summary>
    public FormKey? Cell { get; } = cell;
    public FormKey? World { get; } = world;

    public override string EditorId => Record.EditorID ?? Key.ToString();

    public OverrideDraft With(IMajorRecord copy) => new(copy, Place, Reference, Cell, World);
}

/// <summary>A field edit: the record's state before (null = no edit yet) and after, as copies.</summary>
public sealed class RecordEditCommand(string name, OverrideDraft? before, OverrideDraft after) : IEditCommand
{
    public string Name { get; } = name;

    public void Apply(EditDocument document) => document.PutDraft(after.With((IMajorRecord)after.Record.DeepCopy()));

    public void Revert(EditDocument document)
    {
        if (before is null) document.DropDraft(after.Key);
        else document.PutDraft(before.With((IMajorRecord)before.Record.DeepCopy()));
    }
}

/// <summary>
/// A top-level record to take out of the active plugin on save: its own record (deleted from the
/// file) or its override of another plugin's record (the previous version wins again).
/// </summary>
public sealed class RemovalDraft(FormKey key, string signature, string? editorId, bool ownRecord) : Draft(key)
{
    public string Signature { get; } = signature;
    public bool OwnRecord { get; } = ownRecord;
    public override string EditorId => editorId ?? Key.ToString();
}

/// <summary>Removing a record: what the document held for it before (an edit, a new record or nothing) and the removal (null = just drop a new record).</summary>
public sealed class RemoveRecordCommand(string name, Draft? before, RemovalDraft? removal, FormKey key) : IEditCommand
{
    public string Name { get; } = name;

    public void Apply(EditDocument document)
    {
        if (removal is not null) document.PutDraft(removal);
        else document.DropDraft(key);
    }

    public void Revert(EditDocument document)
    {
        if (before is not null) document.PutDraft(before is OverrideDraft o ? o.With((IMajorRecord)o.Record.DeepCopy()) : before);
        else document.DropDraft(key);
    }
}

public static class OverrideEditing
{
    /// <summary>
    /// Takes a top-level record out of the active plugin (one undo step). <paramref name="inPlugin"/>:
    /// the active plugin already has a version of it on disk; otherwise only an unsaved new record or
    /// edit is dropped.
    /// </summary>
    public static void RemoveRecord(this EditDocument document, string name, FormKey key, string signature, string? editorId, bool inPlugin, bool ownRecord)
    {
        var before = document.Draft(key);
        if (!inPlugin && before is null) return;
        document.Record(new RemoveRecordCommand(name, before, inPlugin ? new RemovalDraft(key, signature, editorId, ownRecord) : null, key));
    }

    /// <summary>The record is to be taken out of the active plugin on save.</summary>
    public static bool IsRemoved(this EditDocument document, FormKey key) => document.Draft(key) is RemovalDraft;

    /// <summary>The edited copy of a record, if any.</summary>
    public static OverrideDraft? Override(this EditDocument document, FormKey key) => document.Draft(key) as OverrideDraft;

    /// <summary>
    /// A working copy to edit: the current edit, else a fresh copy of <paramref name="winning"/>.
    /// Changes become a step with <see cref="CommitEdit"/>.
    /// </summary>
    public static IMajorRecord WorkingCopy(this EditDocument document, FormKey key, IMajorRecordGetter winning) =>
        (IMajorRecord)(document.Override(key)?.Record ?? winning).DeepCopy();

    /// <summary>Records an edited copy as one undoable step (no-op when nothing changed).</summary>
    public static void CommitEdit(this EditDocument document, string name, IMajorRecord edited, RecordPlace place,
        RefContext? reference = null, FormKey? cell = null, FormKey? world = null)
    {
        var before = document.Override(edited.FormKey);
        var after = before is not null ? before.With(edited) : new OverrideDraft(edited, place, reference, cell, world);
        document.Record(new RecordEditCommand(name, before is null ? null : before.With((IMajorRecord)before.Record.DeepCopy()), after));
    }
}
