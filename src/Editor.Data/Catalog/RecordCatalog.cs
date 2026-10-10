using System.Collections.Concurrent;
using System.Diagnostics;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace StbEditor.Data.Catalog;

/// <summary>What one plugin says about one record. Placed references are not catalogued here.</summary>
public sealed record CatalogEntry(
    FormKey FormKey,
    string Signature,
    uint Flags,
    string? EditorId,
    string? Name,
    CellPlacement? Cell);

/// <summary>Where a CELL sits: interior, or a grid cell of a worldspace.</summary>
public sealed record CellPlacement(FormKey? Worldspace, short GridX, short GridY, bool IsInterior, bool IsPersistentCell);

/// <summary>A record across the load order: who defines it, who overrides it, which version wins.</summary>
public sealed class CatalogRecord
{
    internal CatalogRecord(FormKey key) => FormKey = key;

    public FormKey FormKey { get; }

    /// <summary>Plugins containing the record, in load order. The last one wins.</summary>
    public List<PluginInfo> Plugins { get; } = new(1);

    public CatalogEntry Winner { get; internal set; } = null!;

    public string Signature => Winner.Signature;
    public string? EditorId => Winner.EditorId;
    public string? Name => Winner.Name;
    public bool IsDeleted => (Winner.Flags & 0x20) != 0;
    public bool IsOverridden => Plugins.Count > 1;

    /// <summary>Created in this session, not saved yet (see <see cref="RecordCatalog.AddTransient"/>).</summary>
    public bool IsTransient { get; internal init; }
    public PluginInfo WinningPlugin => Plugins[^1];
}

/// <summary>
/// Every non-placed record of the load order, merged by FormKey. This is what the Object Window,
/// search and the cell list read; record bodies stay in Mutagen and are only touched on demand.
/// </summary>
public sealed class RecordCatalog
{
    private readonly Dictionary<FormKey, CatalogRecord> _byKey;
    private readonly Dictionary<string, List<CatalogRecord>> _bySignature;

    public IReadOnlyDictionary<FormKey, CatalogRecord> ByKey => _byKey;
    public IReadOnlyDictionary<string, List<CatalogRecord>> BySignature => _bySignature;
    public IReadOnlyList<string> Errors { get; }
    public TimeSpan BuildTime { get; }
    public int CachedPlugins { get; }

    private RecordCatalog(
        Dictionary<FormKey, CatalogRecord> byKey,
        List<string> errors,
        TimeSpan buildTime,
        int cachedPlugins)
    {
        _byKey = byKey;
        Errors = errors;
        BuildTime = buildTime;
        CachedPlugins = cachedPlugins;

        _bySignature = byKey.Values
            .GroupBy(r => r.Signature)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.EditorId ?? "￿", StringComparer.OrdinalIgnoreCase).ToList());
    }

    public CatalogRecord? Get(FormKey key) => _byKey.GetValueOrDefault(key);

    /// <summary>Raised when transient (session-only) records come or go.</summary>
    public event Action? Changed;

    public int Version { get; private set; }

    /// <summary>
    /// A record created in the editor (duplicate, new object) so the Object Window, placement and
    /// search see it before it is saved. <paramref name="plugin"/> is the active file.
    /// </summary>
    public void AddTransient(FormKey key, string signature, string? editorId, string? name, PluginInfo plugin)
    {
        if (_byKey.ContainsKey(key)) RemoveTransient(key);
        var record = new CatalogRecord(key) { IsTransient = true };
        record.Plugins.Add(plugin);
        record.Winner = new CatalogEntry(key, signature, 0, editorId, name, null);
        _byKey[key] = record;
        if (!_bySignature.TryGetValue(signature, out var list)) _bySignature[signature] = list = [];
        list.Insert(0, record);
        Version++;
        Changed?.Invoke();
    }

    public void RemoveTransient(FormKey key)
    {
        if (!_byKey.TryGetValue(key, out var record) || !record.IsTransient) return;
        _byKey.Remove(key);
        _bySignature.GetValueOrDefault(record.Signature)?.Remove(record);
        Version++;
        Changed?.Invoke();
    }

    public IEnumerable<CatalogRecord> Transient => _byKey.Values.Where(r => r.IsTransient);

    public string Describe(FormKey key)
    {
        if (key.IsNull) return "NULL";
        var record = Get(key);
        if (record is null) return key.ToString();
        return record.EditorId ?? record.Name ?? key.ToString();
    }

    public static RecordCatalog Build(Workspace workspace, string cacheFolder, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var stopwatch = Stopwatch.StartNew();
        Directory.CreateDirectory(cacheFolder);

        var loaded = workspace.Active.Where(p => p.Mod is not null).ToList();
        var perPlugin = new List<CatalogEntry>[loaded.Count];
        var errors = new ConcurrentBag<string>();
        var cached = 0;
        var done = 0;

        // Biggest plugins first so Skyrim.esm does not start last and hold everything up.
        var order = Enumerable.Range(0, loaded.Count)
            .OrderByDescending(i => loaded[i].Header?.RecordCount ?? 0)
            .ToList();

        Parallel.ForEach(order, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            var plugin = loaded[i];
            try
            {
                var cache = CatalogCache.PathFor(cacheFolder, plugin);
                var entries = CatalogCache.TryRead(cache, plugin);
                if (entries is not null)
                {
                    Interlocked.Increment(ref cached);
                }
                else
                {
                    entries = Scan(plugin.Mod!);
                    CatalogCache.Write(cache, plugin, entries);
                }

                perPlugin[i] = entries;
            }
            catch (Exception ex)
            {
                errors.Add($"{plugin.Name}: {ex.Message}");
                perPlugin[i] = [];
            }

            var n = Interlocked.Increment(ref done);
            if (n % 8 == 0 || n == loaded.Count) progress?.Report($"Indexing records {n}/{loaded.Count}");
        });

        progress?.Report("Merging overrides");
        var byKey = new Dictionary<FormKey, CatalogRecord>(capacity: perPlugin.Sum(p => p.Count));

        for (var i = 0; i < loaded.Count; i++)
        {
            foreach (var entry in perPlugin[i])
            {
                if (!byKey.TryGetValue(entry.FormKey, out var record))
                {
                    record = new CatalogRecord(entry.FormKey);
                    byKey.Add(entry.FormKey, record);
                }

                record.Plugins.Add(loaded[i]);
                record.Winner = entry;
            }
        }

        return new RecordCatalog(byKey, [.. errors.Order()], stopwatch.Elapsed, cached);
    }

    /// <summary>
    /// Top-level groups through Mutagen, but cells and worldspaces only down to the CELL records:
    /// their children (refs, navmeshes, landscape) are most of Skyrim.esm and belong to the cell
    /// loader, not to the catalog.
    /// </summary>
    internal static List<CatalogEntry> Scan(ISkyrimModGetter mod)
    {
        var entries = new List<CatalogEntry>();

        foreach (var group in TopLevelGroups(mod))
        {
            foreach (var record in group.Records)
            {
                entries.Add(Entry(record, null));

                if (record is IWorldspaceGetter world) ScanWorldspace(world, entries);
            }
        }

        foreach (var block in mod.Cells.Records)
            foreach (var subBlock in block.SubBlocks)
                foreach (var cell in subBlock.Cells)
                    entries.Add(Entry(cell, new CellPlacement(null, 0, 0, true, false)));

        return entries;
    }

    private static void ScanWorldspace(IWorldspaceGetter world, List<CatalogEntry> entries)
    {
        if (world.TopCell is { } top)
            entries.Add(Entry(top, new CellPlacement(world.FormKey, 0, 0, false, true)));

        foreach (var block in world.SubCells)
            foreach (var subBlock in block.Items)
                foreach (var cell in subBlock.Items)
                {
                    var grid = cell.Grid;
                    entries.Add(Entry(cell, new CellPlacement(world.FormKey, (short)(grid?.Point.X ?? 0), (short)(grid?.Point.Y ?? 0), false, false)));
                }
    }

    private static readonly System.Reflection.PropertyInfo[] GroupProperties = typeof(ISkyrimModGetter)
        .GetProperties()
        .Where(p => typeof(IGroupGetter).IsAssignableFrom(p.PropertyType))
        .ToArray();

    private static IEnumerable<IGroupGetter> TopLevelGroups(ISkyrimModGetter mod)
    {
        foreach (var property in GroupProperties)
        {
            if (property.GetValue(mod) is IGroupGetter group && group.Count > 0)
                yield return group;
        }
    }

    private static CatalogEntry Entry(IMajorRecordGetter record, CellPlacement? cell) => new(
        record.FormKey,
        SignatureOf(record),
        (uint)record.MajorRecordFlagsRaw,
        record.EditorID,
        NameOf(record),
        cell);

    private static readonly ConcurrentDictionary<Type, string> Signatures = new();

    /// <summary>Four-letter record type. Mutagen keeps it as a static field of each *_Registration class.</summary>
    public static string SignatureOf(IMajorRecordGetter record) =>
        Signatures.GetOrAdd(record.Registration.GetType(), static type =>
            type.GetField("TriggeringRecordType")?.GetValue(null) is RecordType rt ? rt.Type : type.Name.Replace("_Registration", ""));

    public static string? NameOf(IMajorRecordGetter record)
    {
        try
        {
            return record switch
            {
                ITranslatedNamedGetter translated => translated.Name?.String,
                INamedGetter named => named.Name,
                _ => null,
            };
        }
        catch
        {
            // A broken string reference must not cost the whole plugin its index.
            return null;
        }
    }
}
