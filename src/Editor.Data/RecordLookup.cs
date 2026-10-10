using System.Reflection;
using Loqui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Data.Catalog;

namespace StbEditor.Data;

/// <summary>
/// Fast record access. The untyped link-cache lookups index every record type of all plugins
/// (a million placed references) on first use; knowing the type from the catalog keeps each
/// lookup to its own type, and cells are found by walking only the plugins that contain them.
/// </summary>
public sealed class RecordLookup(Workspace workspace, RecordCatalog catalog)
{
    private static readonly Lazy<Dictionary<string, Type>> GetterTypes = new(BuildGetterTypes);

    /// <summary>Signature → Mutagen getter interface (e.g. "STAT" → IStaticGetter).</summary>
    public static Type? GetterType(string signature) => GetterTypes.Value.GetValueOrDefault(signature);

    private static Dictionary<string, Type> BuildGetterTypes()
    {
        var map = new Dictionary<string, Type>();
        foreach (var type in typeof(ISkyrimModGetter).Assembly.GetTypes())
        {
            if (!type.Name.EndsWith("_Registration", StringComparison.Ordinal)) continue;
            if (type.GetField("TriggeringRecordType", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not RecordType rt) continue;
            if (type.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not ILoquiRegistration registration) continue;
            if (!typeof(IMajorRecordGetter).IsAssignableFrom(registration.GetterType)) continue;
            map.TryAdd(rt.Type, registration.GetterType);
        }
        return map;
    }

    /// <summary>Winning version of a record, resolved through its own type only.</summary>
    public IMajorRecordGetter? Resolve(FormKey key)
    {
        if (key.IsNull) return null;
        var signature = catalog.Get(key)?.Signature;
        var type = signature is null ? null : GetterType(signature);

        try
        {
            if (type is not null)
                return workspace.LinkCache.TryResolve(key, type, out var typed) ? typed : null;
            return workspace.LinkCache.TryResolve(key, out var any) ? any : null;
        }
        catch
        {
            return null;
        }
    }

    public T? Resolve<T>(FormKey key) where T : class, IMajorRecordGetter
    {
        if (key.IsNull) return null;
        try
        {
            return workspace.LinkCache.TryResolve<T>(key, out var record) ? record : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Every plugin's copy of a CELL (with its children), lowest priority first. Exterior cells
    /// are looked up by block/sub-block from their grid position; interiors by scanning the
    /// plugin's interior blocks (a few thousand overlay entries at most).
    /// </summary>
    public List<ICellGetter> CellVersions(FormKey cellKey)
    {
        var record = catalog.Get(cellKey);
        var result = new List<ICellGetter>();
        if (record?.Winner.Cell is not { } placement) return result;

        foreach (var plugin in record.Plugins)
        {
            if (plugin.Mod is not { } mod) continue;
            try
            {
                var cell = placement.IsInterior
                    ? FindInterior(mod, cellKey)
                    : FindExterior(mod, cellKey, placement);
                if (cell is not null) result.Add(cell);
            }
            catch
            {
                // A plugin with a broken cell group loses that version only.
            }
        }

        return result;
    }

    /// <summary>Every plugin's copy of a worldspace's persistent cell.</summary>
    public List<ICellGetter> PersistentCellVersions(FormKey worldKey)
    {
        var record = catalog.Get(worldKey);
        var result = new List<ICellGetter>();
        if (record is null) return result;

        foreach (var plugin in record.Plugins)
        {
            if (plugin.Mod is not { } mod) continue;
            try
            {
                if (ModCells.Of(mod).World(worldKey)?.TopCell is { } top) result.Add(top);
            }
            catch
            {
            }
        }

        return result;
    }

    private static ICellGetter? FindInterior(ISkyrimModGetter mod, FormKey key) => ModCells.Of(mod).Interior(key);

    private static ICellGetter? FindExterior(ISkyrimModGetter mod, FormKey key, CellPlacement placement) =>
        ModCells.Of(mod).Exterior(placement.Worldspace!.Value, key);

    /// <summary>
    /// Cells of one plugin, indexed once. A stream overlay reads a group's bytes into memory each
    /// time the group is walked, and every record taken from it keeps that copy alive: walking the
    /// worldspace for each exterior cell kept one copy of Skyrim.esm's whole Tamriel group
    /// (~200 MB) per loaded cell — 5 GB for a 5×5 grid. Here each group is walked once per plugin.
    /// </summary>
    private sealed class ModCells
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ISkyrimModGetter, ModCells> All = new();

        public static ModCells Of(ISkyrimModGetter mod) => All.GetValue(mod, m => new ModCells(m));

        private readonly ISkyrimModGetter _mod;
        private readonly Lazy<Dictionary<FormKey, ICellGetter>> _interiors;
        private readonly Dictionary<FormKey, (IWorldspaceGetter? World, Dictionary<FormKey, ICellGetter>? Cells)> _worlds = new();

        private ModCells(ISkyrimModGetter mod)
        {
            _mod = mod;
            _interiors = new(() =>
            {
                var map = new Dictionary<FormKey, ICellGetter>();
                foreach (var block in mod.Cells.Records)
                    foreach (var subBlock in block.SubBlocks)
                        foreach (var cell in subBlock.Cells)
                            map.TryAdd(cell.FormKey, cell);
                return map;
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public ICellGetter? Interior(FormKey key) => _interiors.Value.GetValueOrDefault(key);

        public IWorldspaceGetter? World(FormKey worldKey)
        {
            lock (_worlds)
            {
                if (_worlds.TryGetValue(worldKey, out var known)) return known.World;
                IWorldspaceGetter? found = null;
                foreach (var world in _mod.Worldspaces)
                    if (world.FormKey == worldKey) { found = world; break; }
                _worlds[worldKey] = (found, null);
                return found;
            }
        }

        public ICellGetter? Exterior(FormKey worldKey, FormKey cellKey)
        {
            var world = World(worldKey);
            if (world is null) return null;
            lock (_worlds)
            {
                var entry = _worlds[worldKey];
                if (entry.Cells is null)
                {
                    // Hand-made plugins sometimes file cells under the wrong block: index them all.
                    var cells = new Dictionary<FormKey, ICellGetter>();
                    foreach (var block in world.SubCells)
                        foreach (var subBlock in block.Items)
                            foreach (var cell in subBlock.Items)
                                cells.TryAdd(cell.FormKey, cell);
                    _worlds[worldKey] = entry = (world, cells);
                }
                return entry.Cells!.GetValueOrDefault(cellKey);
            }
        }
    }
}
