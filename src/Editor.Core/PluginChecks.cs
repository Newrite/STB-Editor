using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Data;
using StbEditor.Data.Catalog;

namespace StbEditor.Core;

/// <summary>An override identical to the version it overrides from the plugin's masters (xEdit: ITM).</summary>
public readonly record struct ItmRecord(FormKey Key, string Signature, string? EditorId, string Master, FormKey? Cell);

/// <summary>A reference the plugin deletes (xEdit: UDR — the game crashes when something still points at it).</summary>
public readonly record struct DeletedReference(FormKey Key, string Signature, FormKey Cell, bool Override, bool Persistent);

/// <summary>A navmesh the plugin deletes (crashes the game when an actor paths over it): its cell and whether it is another plugin's.</summary>
public readonly record struct DeletedNavmesh(FormKey Key, FormKey Cell, FormKey? World, bool Override);

/// <summary>A problem Check for Errors found: what, where, how bad.</summary>
/// <param name="Fix">A fix the check window offers for this kind of issue ("BodyTint"), or null.</param>
public readonly record struct CheckIssue(bool Error, FormKey Key, string Signature, string? EditorId, string Message, string? Fix = null);

public sealed class CleanReport
{
    public List<ItmRecord> Itm { get; } = [];
    public List<DeletedReference> DeletedReferences { get; } = [];
    public List<DeletedNavmesh> DeletedNavmeshes { get; } = [];
    public int Compared { get; set; }
}

/// <summary>
/// xEdit's Quick Auto Clean, read-only: overrides identical to their master's version (top-level
/// records, cell headers, references) and deleted references. The fixes (remove the ITM override,
/// undelete and disable the reference) are edits in the editor.
/// </summary>
public static class PluginChecks
{
    private static readonly HashSet<string> Containers = ["CELL", "WRLD", "DIAL"];
    private static readonly HashSet<string> NotCompared = ["NAVI", "NAVM", "LAND", "TES4"];

    private static readonly Cell.TranslationMask CellHeader = new(defaultOn: true) { Persistent = false, Temporary = false, Landscape = false, NavigationMeshes = false };
    private static readonly Worldspace.TranslationMask WorldHeader = new(defaultOn: true) { TopCell = false, SubCells = false };
    private static readonly DialogTopic.TranslationMask TopicHeader = new(defaultOn: true) { Responses = false };

    /// <summary>A copy for comparing: containers without their contents, bookkeeping fields levelled.</summary>
    private static IMajorRecord Comparable(IMajorRecordGetter record)
    {
        IMajorRecord copy = record switch
        {
            ICellGetter cell => cell.DeepCopy(CellHeader),
            IWorldspaceGetter world => world.DeepCopy(WorldHeader),
            IDialogTopicGetter topic => topic.DeepCopy(TopicHeader),
            _ => (IMajorRecord)record.DeepCopy(),
        };
        if (copy is ISkyrimMajorRecord skyrim)
        {
            skyrim.VersionControl = 0;
            skyrim.FormVersion = 0;
            skyrim.Version2 = 0;
        }
        copy.IsCompressed = false;
        return copy;
    }

    /// <summary>
    /// Whether two versions of a record hold the same data (bookkeeping and compression aside). Mutagen's
    /// equality compares male/female pairs (armor models, race data) by reference, so records that have
    /// them are compared field by field when it says no.
    /// </summary>
    public static bool Identical(IMajorRecordGetter a, IMajorRecordGetter b)
    {
        var x = Comparable(a);
        var y = Comparable(b);
        if (x.Equals(y)) return true;
        if (!HasGendered(x.GetType())) return false;
        var left = RecordFields.Leaves(x, maxDepth: 16, maxListItems: int.MaxValue).Select(l => (l.PathText, RecordFields.Repr(l.Value)));
        var right = RecordFields.Leaves(y, maxDepth: 16, maxListItems: int.MaxValue).Select(l => (l.PathText, RecordFields.Repr(l.Value)));
        return left.SequenceEqual(right);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> Gendered = new();

    private static bool HasGendered(Type type) => Gendered.GetOrAdd(type, static t => Walk(t, 0, []));

    private static bool Walk(Type type, int depth, HashSet<Type> seen)
    {
        if (depth > 6 || !seen.Add(type)) return false;
        foreach (var property in type.GetProperties())
        {
            var p = property.PropertyType;
            if (p.IsGenericType && p.GetGenericTypeDefinition().Name.Contains("GenderedItem", StringComparison.Ordinal)) return true;
            if (p.IsGenericType && p.GetGenericArguments() is [var element] && element.Namespace?.StartsWith("Mutagen", StringComparison.Ordinal) == true && Walk(element, depth + 1, seen)) return true;
            if (p.Namespace?.StartsWith("Mutagen.Bethesda.Skyrim", StringComparison.Ordinal) == true && p.IsClass && Walk(p, depth + 1, seen)) return true;
        }
        return false;
    }

    private static readonly HashSet<string> LeveledLists = ["LVLI", "LVLN", "LVSP"];

    /// <summary>
    /// xEdit's Check for Errors over one plugin's records: links to records that are not in the load order,
    /// deleted base records, leveled lists that contain themselves, the plugin's new NPCs without
    /// a FaceGen head (dark face in game), records that cannot be read.
    /// </summary>
    public static List<CheckIssue> Errors(Workspace workspace, RecordCatalog catalog, PluginInfo plugin, Func<string, bool> assetExists,
        IProgress<string>? progress = null, CancellationToken cancel = default, Func<string, string?>? assetOrigin = null)
    {
        var pluginFolder = Path.GetDirectoryName(Path.GetFullPath(plugin.Location.Path))!;
        var pluginStem = Path.GetFileNameWithoutExtension(plugin.Name);
        var issues = new List<CheckIssue>();
        if (plugin.Mod is not { } mod) return issues;
        var lookup = new RecordLookup(workspace, catalog);
        var count = 0;
        foreach (var record in mod.EnumerateMajorRecords())
        {
            cancel.ThrowIfCancellationRequested();
            if (++count % 2000 == 0) progress?.Report($"checking: {count:N0} records");
            string signature;
            string? editorId;
            try
            {
                signature = catalog.Get(record.FormKey)?.Signature ?? record.Registration.ClassType.Name.ToUpperInvariant() switch
                {
                    "PLACEDOBJECT" => "REFR",
                    "PLACEDNPC" => "ACHR",
                    "DIALOGRESPONSES" => "INFO",
                    "NAVIGATIONMESH" => "NAVM",
                    var other => other.Length > 4 ? other[..4] : other,
                };
                editorId = record.EditorID;
            }
            catch (Exception ex)
            {
                issues.Add(new CheckIssue(true, record.FormKey, "?", null, $"cannot be read: {ex.Message}"));
                continue;
            }

            if (record.IsDeleted)
            {
                if (signature is not ("REFR" or "ACHR" or "PGRE" or "PHZD" or "PMIS" or "PARW" or "PBAR" or "PBEA" or "PCON" or "PFLA" or "NAVM" or "INFO"))
                    issues.Add(new CheckIssue(false, record.FormKey, signature, editorId, "deleted record: the game crashes if anything still uses it (remove it from the plugin instead)"));
                continue;
            }

            try
            {
                foreach (var link in record.EnumerateFormLinks())
                {
                    if (link.FormKey.IsNull) continue;
                    var target = catalog.Get(link.FormKey);
                    // Forms the engine itself defines (Skyrim.esm's first IDs; xEdit keeps them in Hardcoded.dat).
                    if (link.FormKey.ID < 0x800 && link.FormKey.ModKey.FileName.String.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase)) continue;
                    if (target is null)
                    {
                        // References, navmeshes, dialogue lines and land are not catalogued: only links that must be catalogued count.
                        if (IsCatalogued(link.Type)) issues.Add(new CheckIssue(true, record.FormKey, signature, editorId, $"points at {link.FormKey}, which is not in the load order"));
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                issues.Add(new CheckIssue(true, record.FormKey, signature, editorId, $"cannot be read: {ex.InnerException?.Message ?? ex.Message}"));
            }

            // New NPCs need their FaceGen head, unless they take their traits from a template.
            if (record is INpcGetter npc && npc.FormKey.ModKey == plugin.ModKey
                && !(npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) && !npc.Template.IsNull)
                && lookup.Resolve<IRaceGetter>(npc.Race.FormKey) is { } race && race.Flags.HasFlag(Race.Flag.FaceGenHead)
                && !assetExists($@"meshes\actors\character\facegendata\facegeom\{npc.FormKey.ModKey.FileName}\{npc.FormKey.ID:X8}.nif"))
                issues.Add(new CheckIssue(false, record.FormKey, "NPC_", editorId, "no FaceGen head (meshes\\actors\\character\\facegendata): dark face in game", "FaceGen"));

            // The body's tint must follow the skin tone, or the neck shows a seam.
            if (record is INpcGetter tinted && tinted.TintLayers.Count > 0
                && !(tinted.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) && !tinted.Template.IsNull)
                && lookup.Resolve<IRaceGetter>(tinted.Race.FormKey) is { } tintRace && BodyTint.Stale(tinted, tintRace, out var wanted))
                issues.Add(new CheckIssue(false, record.FormKey, "NPC_", editorId,
                    $"body tint {(tinted.TextureLighting is { } q ? $"{q.R},{q.G},{q.B}" : "none")} is not the skin tone's ({wanted.R},{wanted.G},{wanted.B}): a seam at the neck (CK: Update NPC Body Tint Colors)", "BodyTint"));

            // Another plugin's NPC whose looks this plugin changes needs the head re-exported with this plugin.
            if (record is INpcGetter changed && changed.FormKey.ModKey != plugin.ModKey && assetOrigin is not null
                && !(changed.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) && !changed.Template.IsNull)
                && lookup.Resolve<IRaceGetter>(changed.Race.FormKey) is { } changedRace && changedRace.Flags.HasFlag(Race.Flag.FaceGenHead)
                && LooksChanged(workspace, plugin, changed) is { } master)
            {
                var path = $@"meshes\actors\character\facegendata\facegeom\{changed.FormKey.ModKey.FileName}\{changed.FormKey.ID:X8}.nif";
                var origin = assetOrigin(path);
                var own = origin is not null && (origin.StartsWith(pluginFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(origin).StartsWith(pluginStem, StringComparison.OrdinalIgnoreCase));
                if (!own)
                    issues.Add(new CheckIssue(false, record.FormKey, "NPC_", editorId, $"changes the face, hair or skin set in {master}, but its FaceGen head comes from another plugin: the face may look dark or wrong (export FaceGen, Ctrl+F4)", "FaceGen"));
            }
        }

        // Leveled lists that contain themselves (through other lists): the game hangs resolving them.
        foreach (var list in catalog.BySignature.Where(kv => LeveledLists.Contains(kv.Key)).SelectMany(kv => kv.Value).Where(r => r.Plugins.Contains(plugin)))
        {
            cancel.ThrowIfCancellationRequested();
            if (LeveledCycle(lookup, list.FormKey) is { } cycle)
                issues.Add(new CheckIssue(true, list.FormKey, list.Signature, list.EditorId, $"contains itself: {string.Join(" > ", cycle.Select(k => catalog.Get(k)?.EditorId ?? k.ToString()))}"));
        }

        // Rooms and portals of the interiors the plugin touches (CK: room / portal validation).
        foreach (var cell in catalog.BySignature.GetValueOrDefault("CELL")?.Where(c => c.Plugins.Contains(plugin) && c.Winner.Cell?.IsInterior == true) ?? [])
        {
            cancel.ThrowIfCancellationRequested();
            issues.AddRange(RoomIssues(lookup, cell.FormKey));
        }
        return issues;
    }

    /// <summary>The room marker and portal marker bases (Skyrim.esm).</summary>
    public static readonly FormKey RoomMarker = FormKey.Factory("00001F:Skyrim.esm"), PortalMarker = FormKey.Factory("000020:Skyrim.esm");

    /// <summary>
    /// A cell's rooms and portals as the game wants them: every room has its bounds and the rooms it links to (XLRM) are rooms
    /// of the cell; every portal has bounds and says which rooms it joins (XPOD), and those are rooms of the cell. (Rooms a
    /// portal joins do not list each other, nor do linked rooms always link back — vanilla does neither.)
    /// </summary>
    public static List<CheckIssue> RoomIssues(RecordLookup lookup, FormKey cellKey)
    {
        var issues = new List<CheckIssue>();
        // The references as the game sees them: every plugin's version of the cell, the last version of each reference.
        var refs = new Dictionary<FormKey, IPlacedObjectGetter>();
        foreach (var version in lookup.CellVersions(cellKey))
            foreach (var placed in version.Persistent.Concat(version.Temporary).OfType<IPlacedObjectGetter>())
                refs[placed.FormKey] = placed;
        var rooms = refs.Values.Where(r => !r.IsDeleted && r.Base.FormKey == RoomMarker).ToDictionary(r => r.FormKey);
        var portals = refs.Values.Where(r => !r.IsDeleted && r.Base.FormKey == PortalMarker).ToList();
        if (rooms.Count == 0 && portals.Count == 0) return issues;
        foreach (var room in rooms.Values)
        {
            if (room.Primitive is null) issues.Add(new CheckIssue(true, room.FormKey, "REFR", room.EditorID, "room marker without bounds (primitive)"));
            foreach (var link in room.LinkedRooms ?? [])
            {
                if (link.IsNull) continue;
                if (!rooms.TryGetValue(link.FormKey, out var other)) issues.Add(new CheckIssue(true, room.FormKey, "REFR", room.EditorID, $"links to {link.FormKey}, which is not a room of this cell"));
            }
        }
        foreach (var portal in portals)
        {
            if (portal.Primitive is null) issues.Add(new CheckIssue(false, portal.FormKey, "REFR", portal.EditorID, "portal without bounds (primitive)"));
            if ((portal.Portals?.Count ?? 0) == 0) { issues.Add(new CheckIssue(false, portal.FormKey, "REFR", portal.EditorID, "portal that joins no rooms (no portal data)")); continue; }
            foreach (var joint in portal.Portals!)
            {
                var ends = new[] { joint.Origin, joint.Destination }.Where(l => !l.IsNull).ToList();
                foreach (var end in ends)
                    if (!rooms.ContainsKey(end.FormKey)) issues.Add(new CheckIssue(true, portal.FormKey, "REFR", portal.EditorID, $"joins {end.FormKey}, which is not a room of this cell"));
            }
        }
        return issues;
    }

    private static readonly string[] Looks = ["Race", "HeadParts", "HairColor", "HeadTexture", "FaceMorph", "FaceParts", "TintLayers", "Weight", "TextureLighting"];

    /// <summary>The master whose version of the NPC looks different from the plugin's (face, hair, tints, race, weight, sex), or null.</summary>
    private static string? LooksChanged(Workspace workspace, PluginInfo plugin, INpcGetter npc)
    {
        List<Mutagen.Bethesda.Plugins.Cache.IModContext<INpcGetter>> versions;
        try { versions = workspace.LinkCache.ResolveAllSimpleContexts<INpcGetter>(npc.FormKey).Reverse().ToList(); }
        catch { return null; }
        var at = versions.FindIndex(v => v.ModKey == plugin.ModKey);
        var masters = (plugin.Header?.Masters ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previous = at > 0 ? versions.Take(at).LastOrDefault(v => masters.Contains(v.ModKey.FileName.String)) : null;
        if (previous is null) return null;
        string Print(INpcGetter n) => string.Join("|", Looks.Select(name =>
        {
            var property = RecordFields.PropertiesOf(n).FirstOrDefault(p => p.Name == name);
            var value = property?.GetValue(n);
            string Text(object item) => RecordFields.IsComposite(item)
                ? string.Join(",", RecordFields.Leaves(item, 8, int.MaxValue).Where(l => !l.PathText.EndsWith("FormKeyNullable", StringComparison.Ordinal)).Select(l => l.PathText + "=" + Value(l.Value)))
                : Value(item);
            // Face sliders re-saved by the CK differ in the last float digits (-0.39999998 / -0.4): same face.
            static string Value(object? v) => v is float f ? (MathF.Round(f, 3) + 0f).ToString(System.Globalization.CultureInfo.InvariantCulture) : RecordFields.Repr(v);
            // Lists (head parts, tint layers) as sets: their order does not change the face.
            return value switch
            {
                null => "",
                System.Collections.IEnumerable list and not string when !RecordFields.IsGendered(value) =>
                    string.Join(";", list.Cast<object?>().Select(i => i is null ? "" : Text(i)).Order(StringComparer.Ordinal)),
                _ => Text(value),
            };
        })) + "|" + n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
        return Print(npc) != Print(previous.Record) ? previous.ModKey.FileName.String : null;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> CataloguedTypes = new();

    /// <summary>Whether every record a link can point at is in the catalog (not a reference, navmesh, line, land or anything).</summary>
    private static bool IsCatalogued(Type linkType) => CataloguedTypes.GetOrAdd(linkType, static t =>
        t != typeof(IMajorRecordGetter) && t != typeof(ISkyrimMajorRecordGetter)
        && !t.IsAssignableFrom(typeof(IPlacedObjectGetter)) && !t.IsAssignableFrom(typeof(IPlacedNpcGetter)) && !typeof(IPlacedGetter).IsAssignableFrom(t)
        && !t.IsAssignableFrom(typeof(INavigationMeshGetter)) && !t.IsAssignableFrom(typeof(IDialogResponsesGetter)) && !t.IsAssignableFrom(typeof(ILandscapeGetter))
        && !t.IsAssignableFrom(typeof(IAPlacedTrapGetter)) && !t.IsAssignableFrom(typeof(IPlacedHazardGetter)));

    /// <summary>A path from a leveled list back to itself through its entries (winning versions), or null.</summary>
    private static List<FormKey>? LeveledCycle(RecordLookup lookup, FormKey start)
    {
        var path = new List<FormKey>();
        var onPath = new HashSet<FormKey>();
        var done = new HashSet<FormKey>();
        IEnumerable<FormKey> Entries(FormKey key) => lookup.Resolve(key) switch
        {
            ILeveledItemGetter li => li.Entries?.Select(e => e.Data?.Reference.FormKey ?? FormKey.Null) ?? [],
            ILeveledNpcGetter ln => ln.Entries?.Select(e => e.Data?.Reference.FormKey ?? FormKey.Null) ?? [],
            ILeveledSpellGetter ls => ls.Entries?.Select(e => e.Data?.Reference.FormKey ?? FormKey.Null) ?? [],
            _ => [],
        };
        bool Visit(FormKey key, int depth)
        {
            if (depth > 64) return false;
            if (onPath.Contains(key)) { path.Add(key); return key == start; }
            if (!done.Add(key)) return false;
            onPath.Add(key);
            path.Add(key);
            foreach (var next in Entries(key).Where(k => !k.IsNull))
                if (Visit(next, depth + 1)) return true;
            path.RemoveAt(path.Count - 1);
            onPath.Remove(key);
            return false;
        }
        return Visit(start, 0) ? path : null;
    }

    public static CleanReport Clean(Workspace workspace, RecordCatalog catalog, PluginInfo plugin, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var report = new CleanReport();
        var masters = (plugin.Header?.Masters ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool IsMaster(PluginInfo p) => masters.Contains(p.Name);
        var lookup = new RecordLookup(workspace, catalog);

        // Top-level records and cell / worldspace / topic headers the plugin overrides.
        var overrides = catalog.ByKey.Values.Where(r => r.FormKey.ModKey != plugin.ModKey && r.Plugins.Contains(plugin)).ToList();
        var done = 0;
        foreach (var record in overrides)
        {
            cancel.ThrowIfCancellationRequested();
            if (++done % 500 == 0) progress?.Report($"ITM: {done}/{overrides.Count}");
            if (NotCompared.Contains(record.Signature) || RecordLookup.GetterType(record.Signature) is not { } type) continue;
            IMajorRecordGetter? mine = null, previous = null;
            string? from = null;
            if (record.Signature == "CELL")
            {
                var versions = lookup.CellVersionsByPlugin(record.FormKey);
                mine = versions.FirstOrDefault(v => v.Plugin == plugin).Cell;
                var before = versions.TakeWhile(v => v.Plugin != plugin).LastOrDefault(v => IsMaster(v.Plugin));
                previous = before.Cell;
                from = before.Plugin?.Name;
            }
            else
            {
                try
                {
                    var versions = workspace.LinkCache.ResolveAllSimpleContexts(record.FormKey, type).Reverse().ToList();
                    var at = versions.FindIndex(v => v.ModKey == plugin.ModKey);
                    if (at < 0) continue;
                    mine = versions[at].Record;
                    var before = versions.Take(at).LastOrDefault(v => masters.Contains(v.ModKey.FileName.String));
                    previous = before?.Record;
                    from = before?.ModKey.FileName.String;
                }
                catch
                {
                    continue;
                }
            }
            if (mine is null || previous is null) continue;
            report.Compared++;
            try
            {
                if (Identical(mine, previous)) report.Itm.Add(new ItmRecord(record.FormKey, record.Signature, record.EditorId, from!, null));
            }
            catch
            {
                // A record Mutagen cannot copy is not called identical.
            }
        }

        // References in the plugin's cells: identical overrides and deleted ones.
        var cells = catalog.BySignature.GetValueOrDefault("CELL")?.Where(c => c.Plugins.Contains(plugin)).ToList() ?? [];
        done = 0;
        foreach (var cellRecord in cells)
        {
            cancel.ThrowIfCancellationRequested();
            if (++done % 200 == 0) progress?.Report($"references: {done}/{cells.Count} cells");
            var versions = lookup.CellVersionsByPlugin(cellRecord.FormKey);
            var mineAt = versions.FindIndex(v => v.Plugin == plugin);
            if (mineAt < 0) continue;
            var mine = versions[mineAt].Cell;
            var masterVersions = versions.Take(mineAt).Where(v => IsMaster(v.Plugin)).Reverse().ToList();
            var persistent = mine.Persistent.Select(p => p.FormKey).ToHashSet();
            foreach (var navmesh in mine.NavigationMeshes.Where(n => n.IsDeleted))
                report.DeletedNavmeshes.Add(new DeletedNavmesh(navmesh.FormKey, cellRecord.FormKey, cellRecord.Winner.Cell?.Worldspace, navmesh.FormKey.ModKey != plugin.ModKey));
            foreach (var placed in mine.Persistent.Concat(mine.Temporary))
            {
                var signature = placed switch { IPlacedNpcGetter => "ACHR", IPlacedObjectGetter => "REFR", _ => placed.GetType().Name.Replace("BinaryOverlay", "") };
                var isOverride = placed.FormKey.ModKey != plugin.ModKey;
                if (placed.IsDeleted)
                {
                    report.DeletedReferences.Add(new DeletedReference(placed.FormKey, signature, cellRecord.FormKey, isOverride, persistent.Contains(placed.FormKey)));
                    continue;
                }
                if (!isOverride) continue;
                var previous = masterVersions.Select(v => v.Cell.Persistent.Concat(v.Cell.Temporary).FirstOrDefault(r => r.FormKey == placed.FormKey) is { } r ? (v.Plugin, r) : default)
                    .FirstOrDefault(x => x.r is not null);
                if (previous.r is null) continue;
                report.Compared++;
                try
                {
                    if (Identical(placed, previous.r)) report.Itm.Add(new ItmRecord(placed.FormKey, signature, placed.EditorID, previous.Plugin!.Name, cellRecord.FormKey));
                }
                catch
                {
                }
            }
        }
        return report;
    }
}
