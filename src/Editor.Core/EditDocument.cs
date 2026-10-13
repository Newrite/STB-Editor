using System.Numerics;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace StbEditor.Core;

/// <summary>Placement of a reference as the editor sees it. Rotation in radians, like the record.</summary>
public readonly record struct RefState(Vector3 Position, Vector3 Rotation, float Scale, bool Deleted)
{
    public bool NearlyEquals(RefState other) =>
        Deleted == other.Deleted
        && Vector3.DistanceSquared(Position, other.Position) < 1e-6f
        && Vector3.DistanceSquared(Rotation, other.Rotation) < 1e-10f
        && MathF.Abs(Scale - other.Scale) < 1e-6f;
}

/// <summary>
/// Pending edits of the session, keyed by reference. Holds the state each reference had when
/// it was first touched, so a change that is undone or dragged back disappears instead of
/// becoming an identical-to-master override. Saving reads <see cref="Changes"/>.
/// </summary>
public sealed class EditDocument
{
    private readonly Dictionary<FormKey, RefState> _original = new();
    private readonly Dictionary<FormKey, RefState> _current = new();
    private readonly Dictionary<FormKey, RefContext> _contexts = new();

    /// <summary>State a reference created in this session starts from: it did not exist.</summary>
    public static readonly RefState Nonexistent = new(default, default, 1f, Deleted: true);

    public UndoStack History { get; } = new();

    /// <summary>A reference changed state (edit, undo or redo).</summary>
    public event Action<FormKey, RefState>? Changed;

    /// <summary>Bumped on every change, for UIs that redraw lists.</summary>
    public int Version { get; private set; }

    public int ChangedCount => _current.Count;

    /// <summary>Everything that would go into a save: changed references, record edits and a change of masters.</summary>
    public int PendingCount => _current.Count + _drafts.Count + (Masters is null ? 0 : 1);

    /// <summary>Masters to add to the active file, and whether to drop unused ones (xEdit's Add / Clean Masters).</summary>
    public MasterEdit? Masters { get; private set; }

    internal void SetMasters(MasterEdit? masters)
    {
        Masters = masters;
        Version++;
        DraftsChanged?.Invoke();
    }

    /// <summary>Changes the pending master edit as one undoable step (null = none).</summary>
    public void EditMasters(string name, MasterEdit? masters)
    {
        if (Equals(Masters, masters)) return;
        Record(new MastersCommand(name, Masters, masters is { Add.Count: 0, Clean: false, Sort: false } ? null : masters));
    }

    private readonly Dictionary<FormKey, Draft> _drafts = new();

    /// <summary>Records created in this session (cells, worldspaces), saved with the references.</summary>
    public IReadOnlyDictionary<FormKey, Draft> Drafts => _drafts;

    public Draft? Draft(FormKey key) => _drafts.GetValueOrDefault(key);

    /// <summary>Raised when a draft record is added or removed.</summary>
    public event Action? DraftsChanged;

    /// <summary>Puts a draft without an undo step (for commands).</summary>
    public void Put(Draft draft) => PutDraft(draft);

    /// <summary>Removes a draft without an undo step (for commands).</summary>
    public void Drop(FormKey key) => DropDraft(key);

    internal void PutDraft(Draft draft)
    {
        _drafts[draft.Key] = draft;
        Version++;
        DraftsChanged?.Invoke();
    }

    internal void DropDraft(FormKey key)
    {
        if (!_drafts.Remove(key)) return;
        Version++;
        DraftsChanged?.Invoke();
    }

    /// <summary>Adds a new record as one undoable step.</summary>
    public void AddDraft(string name, Draft draft)
    {
        var command = new DraftCommand(name, [draft]);
        command.Apply(this);
        History.Push(command);
    }

    /// <summary>Runs an already applied command's bookkeeping: pushes it so it can be undone.</summary>
    public void Record(IEditCommand command)
    {
        command.Apply(this);
        History.Push(command);
    }

    /// <summary>LAND being edited in a worldspace.</summary>
    public IEnumerable<LandDraft> LandDrafts(FormKey world) => _drafts.Values.OfType<LandDraft>().Where(l => l.World == world);

    /// <summary>Exterior cell drafts of a worldspace.</summary>
    public IEnumerable<CellDraft> CellDrafts(FormKey? world) => _drafts.Values.OfType<CellDraft>().Where(c => c.World == world);

    public IReadOnlyDictionary<FormKey, RefState> Changes => _current;

    public bool TryGet(FormKey key, out RefState state) => _current.TryGetValue(key, out state);

    public RefState? Original(FormKey key) => _original.TryGetValue(key, out var s) ? s : null;

    /// <summary>Remembers where a reference lives; kept for the whole session (undo may need it again).</summary>
    public void RegisterContext(FormKey key, RefContext context) => _contexts.TryAdd(key, context);

    public RefContext? Context(FormKey key) => _contexts.GetValueOrDefault(key);

    /// <summary>Created in this session (no record in any plugin yet).</summary>
    public bool IsNew(FormKey key) => _contexts.TryGetValue(key, out var c) && c.Record is null;

    /// <summary>New references that currently exist (placed and not undone or deleted).</summary>
    public IEnumerable<(FormKey Key, RefContext Context, RefState State)> NewReferences =>
        _current.Where(kv => IsNew(kv.Key) && !kv.Value.Deleted).Select(kv => (kv.Key, _contexts[kv.Key], kv.Value));

    public bool IsChanged(FormKey key) => _current.ContainsKey(key);

    /// <summary>Sets the state of a reference whose untouched state is <paramref name="original"/>.</summary>
    public void Set(FormKey key, RefState original, RefState state)
    {
        _original.TryAdd(key, original);

        if (state.NearlyEquals(_original[key]))
        {
            _current.Remove(key);
            _original.Remove(key);
        }
        else
        {
            _current[key] = state;
        }

        Version++;
        Changed?.Invoke(key, state);
    }

    /// <summary>Applies a batch as one undoable step.</summary>
    public void Execute(string name, IReadOnlyList<(FormKey Key, RefState Original, RefState Before, RefState After)> changes)
    {
        if (changes.Count == 0) return;
        if (changes.All(c => c.Before.NearlyEquals(c.After))) return;

        var command = new SetStatesCommand(name, changes);
        command.Apply(this);
        History.Push(command);
    }

    /// <summary>Reference changes plus records they need (a cell created on the fly) as one undoable step.</summary>
    public void Execute(string name, IReadOnlyList<(FormKey Key, RefState Original, RefState Before, RefState After)> changes, IReadOnlyList<Draft> drafts)
    {
        if (drafts.Count == 0)
        {
            Execute(name, changes);
            return;
        }
        var command = new CompositeCommand(name, [new DraftCommand(name, drafts), new SetStatesCommand(name, changes)]);
        command.Apply(this);
        History.Push(command);
    }

    public void Undo()
    {
        if (History.TakeUndo() is { } command) command.Revert(this);
    }

    public void Redo()
    {
        if (History.TakeRedo() is { } command) command.Apply(this);
    }

    /// <summary>Forgets everything (after a save, or when the data are reloaded).</summary>
    public void Clear()
    {
        var keys = _current.Keys.ToList();
        var originals = keys.ToDictionary(k => k, k => _original[k]);
        _current.Clear();
        _original.Clear();
        _contexts.Clear();
        var hadDrafts = _drafts.Count > 0 || Masters is not null;
        _drafts.Clear();
        Masters = null;
        if (hadDrafts) DraftsChanged?.Invoke();
        History.Clear();
        Version++;
        foreach (var key in keys) Changed?.Invoke(key, originals[key]);
    }
}

/// <summary>Masters to add (in load order after the existing ones), whether unused masters go, and whether all are put in load order (xEdit: Sort Masters).</summary>
public sealed record MasterEdit(IReadOnlyList<ModKey> Add, bool Clean, bool Sort = false)
{
    public bool Equals(MasterEdit? other) => other is not null && Clean == other.Clean && Sort == other.Sort && Add.SequenceEqual(other.Add);
    public override int GetHashCode() => HashCode.Combine(Clean, Sort, Add.Count);
}

public sealed class MastersCommand(string name, MasterEdit? before, MasterEdit? after) : IEditCommand
{
    public string Name { get; } = name;
    public void Apply(EditDocument document) => document.SetMasters(after);
    public void Revert(EditDocument document) => document.SetMasters(before);
}

public interface IEditCommand
{
    string Name { get; }
    void Apply(EditDocument document);
    void Revert(EditDocument document);
}

public sealed class SetStatesCommand(string name, IReadOnlyList<(FormKey Key, RefState Original, RefState Before, RefState After)> changes) : IEditCommand
{
    public string Name { get; } = name;

    public int Count => changes.Count;

    public void Apply(EditDocument document)
    {
        foreach (var c in changes) document.Set(c.Key, c.Original, c.After);
    }

    public void Revert(EditDocument document)
    {
        foreach (var c in changes) document.Set(c.Key, c.Original, c.Before);
    }
}

/// <summary>A record created in this session; <see cref="PluginWriter"/> adds it to the active plugin.</summary>
public abstract class Draft(FormKey key)
{
    public FormKey Key { get; } = key;
    public abstract string EditorId { get; }
}

/// <summary>A new CELL: interior when <see cref="World"/> is null, else an exterior cell of that worldspace.</summary>
public sealed class CellDraft(Cell cell, FormKey? world) : Draft(cell.FormKey)
{
    public Cell Cell { get; } = cell;
    public FormKey? World { get; } = world;
    public bool Interior => World is null;
    public override string EditorId => Cell.EditorID ?? (Cell.Grid is { } g ? $"({g.Point.X}, {g.Point.Y})" : Key.ToString());
}

/// <summary>A new WRLD with its persistent cell (<see cref="Worldspace.TopCell"/>).</summary>
public sealed class WorldDraft(Worldspace world) : Draft(world.FormKey)
{
    public Worldspace World { get; } = world;
    public FormKey PersistentCell => World.TopCell!.FormKey;
    public override string EditorId => World.EditorID ?? Key.ToString();
}

/// <summary>
/// A LAND being edited: an override of an existing record (<see cref="Original"/>) or new land
/// for a cell. Saved into the active plugin under its cell (the cell header is copied if needed).
/// </summary>
public sealed class LandDraft(FormKey key, FormKey cell, FormKey world, int gridX, int gridY, LandscapeData data, Mutagen.Bethesda.Skyrim.ILandscapeGetter? original)
    : Draft(key)
{
    public FormKey Cell { get; } = cell;
    public FormKey World { get; } = world;
    public int GridX { get; } = gridX;
    public int GridY { get; } = gridY;
    public LandscapeData Data { get; set; } = data;
    public Mutagen.Bethesda.Skyrim.ILandscapeGetter? Original { get; } = original;
    public bool IsNew => Original is null;
    public override string EditorId => $"Land ({GridX}, {GridY})";
}

/// <summary>One brush stroke over one or more cells: the land before (null = no draft yet) and after.</summary>
public sealed class LandStrokeCommand(string name, IReadOnlyList<(LandDraft Draft, LandscapeData? Before, LandscapeData After)> cells) : IEditCommand
{
    public string Name { get; } = name;

    public void Apply(EditDocument document)
    {
        foreach (var (draft, _, after) in cells)
        {
            draft.Data = after.Clone();
            document.PutDraft(draft);
        }
    }

    public void Revert(EditDocument document)
    {
        foreach (var (draft, before, _) in cells)
        {
            if (before is null)
            {
                document.DropDraft(draft.Key);
            }
            else
            {
                draft.Data = before.Clone();
                document.PutDraft(draft);
            }
        }
    }
}

public sealed class CompositeCommand(string name, IReadOnlyList<IEditCommand> parts) : IEditCommand
{
    public string Name { get; } = name;

    public void Apply(EditDocument document)
    {
        foreach (var part in parts) part.Apply(document);
    }

    public void Revert(EditDocument document)
    {
        for (var i = parts.Count - 1; i >= 0; i--) parts[i].Revert(document);
    }
}

public sealed class DraftCommand(string name, IReadOnlyList<Draft> drafts) : IEditCommand
{
    public string Name { get; } = name;

    public void Apply(EditDocument document)
    {
        foreach (var d in drafts) document.PutDraft(d);
    }

    public void Revert(EditDocument document)
    {
        foreach (var d in drafts) document.DropDraft(d.Key);
    }
}

/// <summary>Linear undo/redo history.</summary>
public sealed class UndoStack
{
    private const int Limit = 500;
    private readonly List<IEditCommand> _undo = [];
    private readonly List<IEditCommand> _redo = [];

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoName => _undo.Count > 0 ? _undo[^1].Name : null;
    public string? RedoName => _redo.Count > 0 ? _redo[^1].Name : null;
    public IReadOnlyList<IEditCommand> Entries => _undo;

    /// <summary>Bumped on every push, undo, redo and clear (lists that show edited state refresh on it).</summary>
    public int Version { get; private set; }

    public void Push(IEditCommand command)
    {
        Version++;
        _undo.Add(command);
        if (_undo.Count > Limit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    internal IEditCommand? TakeUndo()
    {
        if (_undo.Count == 0) return null;
        Version++;
        var command = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(command);
        return command;
    }

    internal IEditCommand? TakeRedo()
    {
        if (_redo.Count == 0) return null;
        Version++;
        var command = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(command);
        return command;
    }

    public void Clear()
    {
        Version++;
        _undo.Clear();
        _redo.Clear();
    }
}
