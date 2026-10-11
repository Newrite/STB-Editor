using System.Numerics;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using Noggog;
using StbEditor.Data;
using StbEditor.Data.Catalog;
using StbEditor.Data.Raw;

namespace StbEditor.Core;

/// <summary>Outcome of preparing a save: a verified temp file next to the target, or the reason it failed.</summary>
public sealed class SaveResult
{
    public required string TargetPath { get; init; }
    public string? TempPath { get; init; }
    public bool Success => TempPath is not null && Error is null;
    public string? Error { get; init; }
    public int Overridden { get; init; }
    public int Created { get; init; }
    public int Disabled { get; init; }
    public int Removed { get; init; }

    /// <summary>Untouched records the writer would not reproduce byte for byte (existing plugins only).</summary>
    public List<string> LossyRecords { get; init; } = [];

    public List<string> Warnings { get; init; } = [];
}

/// <summary>
/// Writes the edit document into the active plugin. The plugin is read in full, changed in
/// memory, written to a temp file beside it and read back for verification; the caller swaps the
/// file in after closing its own handles (the workspace keeps plugins open).
///
/// Reference changes become overrides the way the Creation Kit makes them: the CELL (and
/// WRLD) records are copied without their children, the winning REFR is copied with the new
/// placement. Deleting a reference of another plugin disables it safely instead of deleting
/// (a deleted master reference crashes the game when something points at it).
/// </summary>
public sealed class PluginWriter(Workspace workspace, RecordCatalog catalog)
{
    private const uint InitiallyDisabledFlag = 0x800;
    private const uint PersistentFlag = 0x400;
    private static readonly FormKey PlayerRef = FormKey.Factory("000014:Skyrim.esm");

    private readonly RecordLookup _lookup = new(workspace, catalog);

    public SaveResult Prepare(ActivePlugin active, EditDocument document)
    {
        var game = workspace.Game;
        var temp = active.Path + ".stbsave";
        var warnings = new List<string>();
        int overridden = 0, created = 0, disabled = 0, removed = 0;

        try
        {
            var mod = active.IsNew ? NewMod(active) : ReadMod(active);
            var originalMasters = mod.ModHeader.MasterReferences.Select(m => m.Master).ToList();
            var touched = new HashSet<FormKey>();
            _document = document;
            _active = active;
            _autoCells.Clear();

            // New records first, so references can go into them: worldspaces, then cells.
            foreach (var world in document.Drafts.Values.OfType<WorldDraft>())
            {
                mod.Worldspaces.Add(world.World.DeepCopy());
                touched.Add(world.Key);
                touched.Add(world.PersistentCell);
                created++;
            }
            foreach (var cell in document.Drafts.Values.OfType<CellDraft>())
            {
                var copy = cell.Cell.DeepCopy();
                if (cell.Interior) mod.Cells.AddInteriorCell(copy);
                else if (cell.World is { } worldKey && WorldFor(mod, worldKey) is { } world) InsertExterior(world, copy);
                else
                {
                    warnings.Add($"{cell.EditorId}: worldspace {cell.World} not found, cell skipped");
                    continue;
                }
                touched.Add(cell.Key);
                created++;
            }

            // Edited and new land, under its cell (a header copy of the cell when it is someone else's).
            foreach (var land in document.Drafts.Values.OfType<LandDraft>())
            {
                var cell = ExteriorCell(mod, new CellTarget(land.Cell, land.World, false, false));
                if (cell is null)
                {
                    warnings.Add($"{land.EditorId}: cell {land.Cell} not found, land skipped");
                    continue;
                }
                var record = cell.Landscape ?? land.Original?.DeepCopy() ?? new Landscape(land.Key, workspace.Game.Release);
                land.Data.Clone().WriteTo(record);
                cell.Landscape = record;
                touched.Add(land.Key);
                if (land.IsNew) created++;
                else overridden++;
            }

            // Edited top-level records (field editors, scripts on base objects).
            foreach (var edit in document.Drafts.Values.OfType<OverrideDraft>().Where(d => d.Place == RecordPlace.TopLevel))
            {
                if (GroupFor(mod, edit.Record.GetType()) is not { } group)
                {
                    warnings.Add($"{edit.EditorId}: no group for {edit.Record.GetType().Name}, skipped");
                    continue;
                }
                SetInGroup(group, (IMajorRecord)edit.Record.DeepCopy());
                touched.Add(edit.Key);
                overridden++;
            }

            // Navmeshes: the edited copy replaces the cell's NAVM (cell header copied if needed).
            foreach (var edit in document.Drafts.Values.OfType<OverrideDraft>().Where(d => d.Place == RecordPlace.Navmesh))
            {
                if (edit.Cell is not { } cellKey)
                {
                    warnings.Add($"{edit.EditorId}: navmesh without a cell, skipped");
                    continue;
                }
                var target = new CellTarget(cellKey, edit.World, false, edit.World is null);
                var cell = target.Interior ? InteriorCell(mod, cellKey) : ExteriorCell(mod, target);
                if (cell is null)
                {
                    warnings.Add($"{edit.EditorId}: cell {cellKey} not found, navmesh skipped");
                    continue;
                }
                var index = cell.NavigationMeshes.FindIndex(n => n.FormKey == edit.Key);
                var copy = (NavigationMesh)edit.Record.DeepCopy();
                if (index >= 0) cell.NavigationMeshes[index] = copy;
                else cell.NavigationMeshes.Add(copy);
                touched.Add(edit.Key);
                overridden++;
            }

            // References: moved/created/deleted ones, and ones whose properties were edited.
            var referenceEdits = document.Drafts.Values.OfType<OverrideDraft>().Where(d => d.Place == RecordPlace.Reference).ToDictionary(d => d.Key);
            var referenceKeys = document.Changes.Keys.Concat(referenceEdits.Keys.Where(k => !document.Changes.ContainsKey(k))).ToList();

            foreach (var key in referenceKeys)
            {
                referenceEdits.TryGetValue(key, out var edit);
                var state = document.Changes.TryGetValue(key, out var changed) ? changed : StateOf((IPlaced)edit!.Record);
                var context = document.Context(key) ?? edit?.Reference;
                if (context is null)
                {
                    warnings.Add($"{key}: no context recorded, skipped");
                    continue;
                }

                if (document.IsNew(key) || (context.Record is null && edit is not null))
                {
                    if (state.Deleted) continue;
                    var record = edit?.Record.DeepCopy() as IPlaced ?? CreateReference(key, context, state);
                    ApplyPlacement(record, state);
                    PlaceInCell(mod, key, record, TargetCell(context, state), warnings);
                    touched.Add(key);
                    created++;
                    continue;
                }

                // Existing reference: this plugin's own record, or an override of someone else's.
                var own = key.ModKey == active.ModKey;
                var existing = FindReference(mod, key);

                if (state.Deleted && own)
                {
                    if (existing is not null && RemoveReference(mod, key)) removed++;
                    touched.Add(key);
                    continue;
                }

                var copy = edit?.Record.DeepCopy() as IPlaced ?? existing?.Record ?? CopyWinning(context);
                if (edit is not null && existing is not null)
                {
                    // The edited copy replaces the plugin's own version of the reference.
                    RemoveReference(mod, key);
                    existing = null;
                }
                if (copy is null)
                {
                    warnings.Add($"{key}: winning record unavailable, skipped");
                    continue;
                }

                if (state.Deleted)
                {
                    SafeDisable(copy, state);
                    disabled++;
                }
                else
                {
                    ApplyPlacement(copy, state);
                    overridden++;
                }

                // A reference dragged into another exterior cell moves to that cell, as in the CK.
                var target = TargetCell(context, state);
                if (existing is not null && existing.Value.Cell.FormKey != target.Cell) RemoveReference(mod, key);
                if (existing is null || existing.Value.Cell.FormKey != target.Cell) PlaceInCell(mod, key, copy, target, warnings);
                touched.Add(key);
            }

            touched.UnionWith(_autoCells.Values);
            mod.ModHeader.Stats.NextFormID = active.NextObjectId;
            mod.ModHeader.Stats.Version = active.HeaderVersion;

            Write(mod, temp, active, originalMasters);

            var writtenMasters = PluginHeader.Read(temp).Masters;
            var mastersKept = !active.IsNew && writtenMasters.Count == originalMasters.Count
                && writtenMasters.Select(m => m.ToLowerInvariant()).SequenceEqual(originalMasters.Select(m => m.FileName.String.ToLowerInvariant()));

            if (!active.IsNew && !mastersKept)
            {
                var added = writtenMasters.Except(originalMasters.Select(m => m.FileName.String), StringComparer.OrdinalIgnoreCase).ToList();
                warnings.Add($"new master(s) needed: {string.Join(", ", added)} — the plugin's own FormIDs shift, so the whole file is rewritten (no byte-for-byte splice)");
            }

            if (mastersKept)
            {
                // Put back the original bytes of everything that was not edited.
                var editedIds = touched
                    .Select(k => (Key: k, Index: k.ModKey == active.ModKey ? originalMasters.Count : originalMasters.IndexOf(k.ModKey)))
                    .Where(x => x.Index >= 0)
                    .Select(x => ((uint)x.Index << 24) | (x.Key.ID & 0xFFFFFF))
                    .ToHashSet();
                var spliced = RawSplice.Splice(File.ReadAllBytes(active.Path), File.ReadAllBytes(temp), editedIds, out var restored);
                File.WriteAllBytes(temp, spliced);
                warnings.Add($"{restored} untouched record(s) kept byte for byte");
            }

            var lossy = active.IsNew ? [] : CompareUntouched(active.Path, temp, touched, originalMasters.Count, writtenMasters.Count);
            Verify(temp, active, document);

            return new SaveResult
            {
                TargetPath = active.Path,
                TempPath = temp,
                Overridden = overridden,
                Created = created,
                Disabled = disabled,
                Removed = removed,
                LossyRecords = lossy,
                Warnings = warnings,
            };
        }
        catch (Exception ex)
        {
            try { File.Delete(temp); } catch { }
            return new SaveResult { TargetPath = active.Path, Error = ex.Message, Warnings = warnings };
        }
    }

    // ------------------------------------------------------------------ mod I/O

    private SkyrimMod NewMod(ActivePlugin active)
    {
        var mod = new SkyrimMod(active.ModKey, workspace.Game.Release);
        if (active.Light) mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
        if (active.Master) mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        mod.ModHeader.Author = "STB Editor";
        return mod;
    }

    private SkyrimMod ReadMod(ActivePlugin active)
    {
        var text = StringEncodingDetector.ReadEncoding(workspace.Game.Language);
        var param = new BinaryReadParameters
        {
            StringsParam = new StringsReadParameters
            {
                TargetLanguage = workspace.Game.Language,
                NonLocalizedEncodingOverride = text,
                NonTranslatedEncodingOverride = text,
            },
        };
        return SkyrimMod.CreateFromBinary(new ModPath(active.ModKey, active.Path), workspace.Game.Release, param);
    }

    private void Write(SkyrimMod mod, string path, ActivePlugin active, List<ModKey> originalMasters)
    {
        // Masters: every original master stays where it is — Mutagen would drop unused ones and
        // renumber the plugin's own FormIDs — and masters the edits need are appended in load order.
        var needed = new HashSet<ModKey>();
        foreach (var record in mod.EnumerateMajorRecords())
        {
            if (record.FormKey.ModKey != active.ModKey) needed.Add(record.FormKey.ModKey);
            foreach (var link in record.EnumerateFormLinks())
                if (!link.FormKey.IsNull && link.FormKey.ModKey != active.ModKey) needed.Add(link.FormKey.ModKey);
        }

        var loadOrder = workspace.Active.Select(p => p.ModKey).ToList();
        var added = needed.Where(k => !originalMasters.Contains(k))
            .OrderBy(k => loadOrder.IndexOf(k) is var i && i < 0 ? int.MaxValue : i)
            .ToList();

        mod.ModHeader.MasterReferences.Clear();
        foreach (var master in originalMasters.Concat(added))
            mod.ModHeader.MasterReferences.Add(new MasterReference { Master = master });

        var encoding = new MutagenEncodingWrapper(StringEncodingDetector.WriteEncoding(active.Encoding, workspace.Game.Language));
        var param = new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            Encodings = new EncodingBundle(encoding, encoding),
        };

        mod.WriteToBinary(path, param);
    }

    /// <summary>Reads the written file back: it must parse and contain every new/changed reference.</summary>
    private void Verify(string path, ActivePlugin active, EditDocument document)
    {
        using var check = SkyrimMod.CreateFromBinaryOverlay(new ModPath(active.ModKey, path), workspace.Game.Release);
        var present = new HashSet<FormKey>(check.EnumerateMajorRecords().Select(r => r.FormKey));

        foreach (var (key, state) in document.Changes)
        {
            var expected = !(state.Deleted && (key.ModKey == active.ModKey || document.IsNew(key)));
            if (expected && !present.Contains(key))
                throw new InvalidDataException($"verification failed: {key} missing from the written plugin");
        }

        foreach (var key in document.Drafts.Keys)
            if (!present.Contains(key))
                throw new InvalidDataException($"verification failed: new record {key} missing from the written plugin");
    }

    /// <summary>
    /// Untouched records must come out byte-identical (decompressed body + flags). Anything else
    /// means the writer normalised or lost data the user did not ask to change.
    /// </summary>
    private static List<string> CompareUntouched(string originalPath, string writtenPath, HashSet<FormKey> touched, int oldOwnIndex, int newOwnIndex)
    {
        var before = Digest(originalPath);
        var after = Digest(writtenPath);

        // Own records move to the new self index when masters were added.
        if (oldOwnIndex != newOwnIndex)
        {
            before = before.ToDictionary(
                kv => (kv.Key.Type, (kv.Key.FormId >> 24) == oldOwnIndex ? ((uint)newOwnIndex << 24) | (kv.Key.FormId & 0xFFFFFF) : kv.Key.FormId),
                kv => kv.Value);
        }
        var touchedIds = touched.Select(k => k.ID).ToHashSet();
        var lossy = new List<string>();

        foreach (var (key, hash) in before)
        {
            if (key.Type == "TES4") continue;
            if (touchedIds.Contains(key.FormId & 0xFFFFFF)) continue;
            // Cells and worldspaces that gained or lost children change only in their groups.
            if (!after.TryGetValue(key, out var newHash))
                lossy.Add($"{key.Type} {key.FormId:X8} missing");
            else if (newHash != hash && key.Type is not ("CELL" or "WRLD"))
                lossy.Add($"{key.Type} {key.FormId:X8} changed");
            if (lossy.Count >= 50) break;
        }

        return lossy;
    }

    private static Dictionary<(string Type, uint FormId), string> Digest(string path)
    {
        var map = new Dictionary<(string, uint), string>();
        var file = File.ReadAllBytes(path);
        foreach (var record in RawFormat.Records(file))
        {
            var flags = record.Flags & ~RawFormat.CompressedFlag;
            var hash = Convert.ToHexString(SHA1.HashData(record.Data.Span)) + flags.ToString("X");
            map[(record.Type, record.FormId)] = hash;
        }
        return map;
    }

    // ------------------------------------------------------------------ references

    private readonly record struct Found(Cell Cell, IPlaced Record, bool Persistent);

    private static IEnumerable<(Cell Cell, Worldspace? World)> AllCells(SkyrimMod mod)
    {
        foreach (var block in mod.Cells.Records)
            foreach (var sub in block.SubBlocks)
                foreach (var cell in sub.Cells)
                    yield return (cell, null);

        foreach (var world in mod.Worldspaces)
        {
            if (world.TopCell is { } top) yield return (top, world);
            foreach (var block in world.SubCells)
                foreach (var sub in block.Items)
                    foreach (var cell in sub.Items)
                        yield return (cell, world);
        }
    }

    private static Found? FindReference(SkyrimMod mod, FormKey key)
    {
        foreach (var (cell, _) in AllCells(mod))
        {
            foreach (var placed in cell.Persistent) if (placed.FormKey == key) return new Found(cell, placed, true);
            foreach (var placed in cell.Temporary) if (placed.FormKey == key) return new Found(cell, placed, false);
        }
        return null;
    }

    private static bool RemoveReference(SkyrimMod mod, FormKey key)
    {
        foreach (var (cell, _) in AllCells(mod))
        {
            var p = cell.Persistent.FindIndex(r => r.FormKey == key);
            if (p >= 0) { cell.Persistent.RemoveAt(p); return true; }
            var t = cell.Temporary.FindIndex(r => r.FormKey == key);
            if (t >= 0) { cell.Temporary.RemoveAt(t); return true; }
        }
        return false;
    }

    private static IPlaced? CopyWinning(RefContext context) => context.Record switch
    {
        IPlacedObjectGetter obj => obj.DeepCopy(),
        IPlacedNpcGetter npc => npc.DeepCopy(),
        IPlacedGetter other => other.DeepCopy() as IPlaced,
        _ => null,
    };

    /// <summary>The placement a reference record carries (for references edited without moving).</summary>
    private static RefState StateOf(IPlaced record)
    {
        var placement = record switch
        {
            IPlacedObjectGetter o => o.Placement,
            IPlacedNpcGetter n => n.Placement,
            IAPlacedTrapGetter t => t.Placement,
            _ => null,
        };
        var scale = record switch
        {
            IPlacedObjectGetter o => o.Scale,
            IPlacedNpcGetter n => n.Scale,
            IAPlacedTrapGetter t => t.Scale,
            _ => null,
        } ?? 1f;
        var position = placement is null ? Vector3.Zero : new Vector3(placement.Position.X, placement.Position.Y, placement.Position.Z);
        var rotation = placement is null ? Vector3.Zero : new Vector3(placement.Rotation.X, placement.Rotation.Y, placement.Rotation.Z);
        return new RefState(position, rotation, scale, false);
    }

    /// <summary>Adds or replaces (the plugin's own record, or an earlier override in it).</summary>
    /// <summary>Puts a record into its top-level group of a mod (tests write records one by one with it).</summary>
    public static bool AddTopLevel(SkyrimMod mod, IMajorRecord record)
    {
        if (GroupFor(mod, record.GetType()) is not { } group) return false;
        SetInGroup(group, record);
        return true;
    }

    private static void SetInGroup(IGroup group, IMajorRecord record)
    {
        var set = group.GetType().GetMethods().FirstOrDefault(m => m.Name == "Set" && m.GetParameters() is [{ ParameterType: var t }] && t.IsInstanceOfType(record));
        if (set is not null) set.Invoke(group, [record]);
        else group.AddUntyped(record);
    }

    private static readonly Dictionary<Type, System.Reflection.PropertyInfo?> Groups = new();

    /// <summary>The mod's top-level group holding records of <paramref name="type"/> (SkyrimGroup&lt;T&gt;).</summary>
    private static IGroup? GroupFor(SkyrimMod mod, Type type)
    {
        if (!Groups.TryGetValue(type, out var property))
        {
            // Subclasses live in their base's group: GlobalShort / GlobalFloat in Globals, GameSettingFloat in GameSettings.
            for (var t = type; t is not null && property is null; t = t.BaseType)
                property = typeof(SkyrimMod).GetProperties().FirstOrDefault(p =>
                    p.PropertyType.IsGenericType && p.PropertyType.GetGenericArguments()[0] == t && typeof(IGroup).IsAssignableFrom(p.PropertyType));
            Groups[type] = property;
        }
        return property?.GetValue(mod) as IGroup;
    }

    private static IPlaced CreateReference(FormKey key, RefContext context, RefState state)
    {
        IPlaced record;
        if (context.BaseSignature is "NPC_" or "LVLN")
        {
            var npc = new PlacedNpc(key, SkyrimRelease.SkyrimSE);
            npc.Base.SetTo(context.Base);
            npc.MajorRecordFlagsRaw |= (int)PersistentFlag;
            record = npc;
        }
        else
        {
            var obj = new PlacedObject(key, SkyrimRelease.SkyrimSE);
            obj.Base.SetTo(context.Base);
            record = obj;
        }

        ApplyPlacement(record, state);
        return record;
    }

    private static void ApplyPlacement(IPlaced record, RefState state)
    {
        var placement = new Placement
        {
            Position = new P3Float(state.Position.X, state.Position.Y, state.Position.Z),
            Rotation = new P3Float(state.Rotation.X, state.Rotation.Y, state.Rotation.Z),
        };

        switch (record)
        {
            case PlacedObject obj:
                obj.Placement = placement;
                obj.Scale = Math.Abs(state.Scale - 1f) < 1e-5f ? null : state.Scale;
                break;
            case PlacedNpc npc:
                npc.Placement = placement;
                npc.Scale = Math.Abs(state.Scale - 1f) < 1e-5f ? null : state.Scale;
                break;
            case APlacedTrap trap:
                trap.Placement = placement;
                trap.Scale = Math.Abs(state.Scale - 1f) < 1e-5f ? null : state.Scale;
                break;
        }
    }

    /// <summary>xEdit's "undelete and disable": initially disabled, enable parent = player (opposite), sunk below the world.</summary>
    private static void SafeDisable(IPlaced record, RefState state)
    {
        record.MajorRecordFlagsRaw |= (int)InitiallyDisabledFlag;
        var sunk = state with { Position = new Vector3(state.Position.X, state.Position.Y, -30000f), Deleted = false };
        ApplyPlacement(record, sunk);

        var parent = new EnableParent { Flags = EnableParent.Flag.SetEnableStateToOppositeOfParent };
        parent.Reference.SetTo(PlayerRef);
        switch (record)
        {
            case PlacedObject obj: obj.EnableParent = parent; break;
            case PlacedNpc npc: npc.EnableParent = parent; break;
            case APlacedTrap trap: trap.EnableParent = parent; break;
        }
    }

    private readonly record struct CellTarget(FormKey Cell, FormKey? World, bool Persistent, bool Interior);

    private EditDocument? _document;
    private ActivePlugin? _active;

    /// <summary>Exterior cells made during this save for references that landed where no cell was.</summary>
    private readonly Dictionary<(FormKey World, int X, int Y), FormKey> _autoCells = new();

    /// <summary>The cell a reference belongs in now: its own, or the exterior cell under its new position.</summary>
    private CellTarget TargetCell(RefContext context, RefState state)
    {
        if (context.Interior || context.Persistent || context.Worldspace is not { } world)
            return new CellTarget(context.Cell, context.Worldspace, context.Persistent, context.Interior);

        var gx = (int)MathF.Floor(state.Position.X / 4096f);
        var gy = (int)MathF.Floor(state.Position.Y / 4096f);
        FormKey? cell = (catalog.BySignature.GetValueOrDefault("CELL") ?? [])
            .FirstOrDefault(c => c.Winner.Cell is { IsInterior: false, IsPersistentCell: false } p && p.Worldspace == world && p.GridX == gx && p.GridY == gy)
            ?.FormKey
            ?? _document?.CellDrafts(world).FirstOrDefault(d => d.Cell.Grid is { } g && g.Point.X == gx && g.Point.Y == gy)?.Key;
        if (cell is null && _autoCells.TryGetValue((world, gx, gy), out var made)) cell = made;

        if (cell is null && _active is not null)
        {
            // Nothing there yet (a new worldspace, the wilderness edge): make the cell, as the CK does.
            cell = _active.Allocate();
            _autoCells[(world, gx, gy)] = cell.Value;
        }

        return new CellTarget(cell ?? context.Cell, world, false, false);
    }

    private void PlaceInCell(SkyrimMod mod, FormKey key, IPlaced record, CellTarget target, List<string> warnings)
    {
        var cell = target.Interior ? InteriorCell(mod, target.Cell) : ExteriorCell(mod, target);
        if (cell is null)
        {
            warnings.Add($"{key}: cell {target.Cell} not found, reference skipped");
            return;
        }

        var persistent = target.Persistent || (record.MajorRecordFlagsRaw & (int)PersistentFlag) != 0;
        if (persistent) cell.Persistent.Add(record);
        else cell.Temporary.Add(record);
    }

    private static readonly Cell.TranslationMask CellHeaderOnly = new(defaultOn: true)
    {
        Persistent = false,
        Temporary = false,
        Landscape = false,
        NavigationMeshes = false,
    };

    private static readonly Worldspace.TranslationMask WorldHeaderOnly = new(defaultOn: true)
    {
        SubCells = false,
        TopCell = false,
    };

    private Cell? InteriorCell(SkyrimMod mod, FormKey cellKey)
    {
        foreach (var block in mod.Cells.Records)
            foreach (var sub in block.SubBlocks)
                foreach (var cell in sub.Cells)
                    if (cell.FormKey == cellKey) return cell;

        var winning = _lookup.CellVersions(cellKey).LastOrDefault();
        if (winning is null) return null;

        var copy = winning.DeepCopy(CellHeaderOnly);
        mod.Cells.AddInteriorCell(copy);
        return copy;
    }

    /// <summary>The worldspace in the plugin: its own, or a header-only override of the winner.</summary>
    private Worldspace? WorldFor(SkyrimMod mod, FormKey worldKey)
    {
        var world = mod.Worldspaces.FirstOrDefault(w => w.FormKey == worldKey);
        if (world is not null) return world;

        var winningWorld = _lookup.Resolve<IWorldspaceGetter>(worldKey);
        if (winningWorld is null) return null;
        world = winningWorld.DeepCopy(WorldHeaderOnly);
        mod.Worldspaces.Add(world);
        return world;
    }

    private Cell? ExteriorCell(SkyrimMod mod, CellTarget target)
    {
        if (target.World is not { } worldKey) return null;

        var world = WorldFor(mod, worldKey);
        if (world is null) return null;

        if (target.Persistent)
        {
            if (world.TopCell is null)
            {
                var top = _lookup.PersistentCellVersions(worldKey).LastOrDefault();
                if (top is null) return null;
                world.TopCell = top.DeepCopy(CellHeaderOnly);
            }
            return world.TopCell;
        }

        foreach (var block in world.SubCells)
            foreach (var sub in block.Items)
                foreach (var cell in sub.Items)
                    if (cell.FormKey == target.Cell) return cell;

        Cell copy;
        if (_autoCells.FirstOrDefault(kv => kv.Value == target.Cell) is { Value: var autoKey, Key: var at } && autoKey == target.Cell)
        {
            copy = new Cell(autoKey, workspace.Game.Release) { Grid = new CellGrid { Point = new P2Int(at.X, at.Y) } };
        }
        else
        {
            var winning = _lookup.CellVersions(target.Cell).LastOrDefault();
            if (winning?.Grid is null) return null;
            copy = winning.DeepCopy(CellHeaderOnly);
        }

        InsertExterior(world, copy);
        return copy;
    }

    /// <summary>Files an exterior cell under its block and sub-block (32×32 and 8×8 cells).</summary>
    private static void InsertExterior(Worldspace world, Cell copy)
    {
        var x = copy.Grid!.Point.X;
        var y = copy.Grid.Point.Y;
        var blockX = (short)Math.Floor(x / 32d);
        var blockY = (short)Math.Floor(y / 32d);
        var subX = (short)Math.Floor(x / 8d);
        var subY = (short)Math.Floor(y / 8d);

        var worldBlock = world.SubCells.FirstOrDefault(b => b.BlockNumberX == blockX && b.BlockNumberY == blockY);
        if (worldBlock is null)
        {
            worldBlock = new WorldspaceBlock { BlockNumberX = blockX, BlockNumberY = blockY, GroupType = GroupTypeEnum.ExteriorCellBlock };
            world.SubCells.Add(worldBlock);
        }

        var subBlock = worldBlock.Items.FirstOrDefault(b => b.BlockNumberX == subX && b.BlockNumberY == subY);
        if (subBlock is null)
        {
            subBlock = new WorldspaceSubBlock { BlockNumberX = subX, BlockNumberY = subY, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
            worldBlock.Items.Add(subBlock);
        }

        subBlock.Items.Add(copy);
    }
}
