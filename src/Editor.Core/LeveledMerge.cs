using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using StbEditor.Data;

namespace StbEditor.Core;

/// <summary>
/// Merged patch for leveled lists (xEdit: Create Merged Patch; Wrye Bash / Mator Smash: leveled lists): when several
/// plugins change one leveled item / NPC / spell list, only the last one wins in the game and the others' additions
/// are lost. The merge keeps every plugin's change against the list's original, item by item (see <see cref="Merge"/>).
/// Flags, chance none and global come from the winner. Lists only one plugin changed need nothing.
/// </summary>
public static class LeveledMerge
{
    /// <summary>A list that needs merging: its versions' plugins, what the merge adds and removes against the winner, and the merged copy.</summary>
    public sealed record Result(FormKey Key, string Signature, string? EditorId, IReadOnlyList<string> Plugins, int Added, int Removed, IMajorRecord Merged,
        IReadOnlyList<(FormKey Item, short Level, int Count)> AddedEntries, IReadOnlyList<(FormKey Item, short Level, int Count)> RemovedEntries);

    private readonly record struct EntryKey(FormKey Reference, short Level, short Count);

    /// <param name="skip">Plugins left out (the patch itself when it is already in the load order).</param>
    public static List<Result> Plan(Workspace workspace, IEnumerable<FormKey> lists, Func<FormKey, string> signatureOf, ISet<ModKey>? skip = null,
        IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var results = new List<Result>();
        var done = 0;
        foreach (var key in lists)
        {
            cancel.ThrowIfCancellationRequested();
            if (++done % 2000 == 0) progress?.Report($"{done:N0} lists");
            var versions = Versions(workspace.LinkCache, key, signatureOf(key)).Where(v => skip?.Contains(v.ModKey) != true).ToList();
            // The original and at least two plugins that change it.
            if (versions.Count < 3 || versions[^1].Record.IsDeleted) continue;
            if (Merge(versions.Select(v => v.Record).ToList()) is not { } merged) continue;
            // Against the winner, entry by entry (an item can be in a list several times).
            var winner = Entries(versions[^1].Record).GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Count());
            var now = Entries(merged).GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Count());
            var added = now.Where(kv => kv.Value > winner.GetValueOrDefault(kv.Key)).Select(kv => (kv.Key, N: kv.Value - winner.GetValueOrDefault(kv.Key))).ToList();
            var removed = winner.Where(kv => kv.Value > now.GetValueOrDefault(kv.Key)).Select(kv => (kv.Key, N: kv.Value - now.GetValueOrDefault(kv.Key))).ToList();
            results.Add(new Result(key, signatureOf(key), versions[^1].Record.EditorID, versions.Select(v => v.ModKey.FileName.String).ToList(),
                added.Sum(a => a.N), removed.Sum(r => r.N), merged,
                added.Select(a => (a.Key.Reference, a.Key.Level, a.N)).ToList(), removed.Select(r => (r.Key.Reference, r.Key.Level, r.N)).ToList()));
        }
        return results;
    }

    /// <summary>Signatures the merged patch handles.</summary>
    public static readonly string[] Leveled = ["LVLI", "LVLN", "LVSP"];
    public static readonly string[] Others = ["CONT", "NPC_", "OTFT", "FLST"];

    /// <summary>The record's versions in load order (the original first).</summary>
    private static List<(ModKey ModKey, IMajorRecordGetter Record)> Versions(ILinkCache cache, FormKey key, string signature)
    {
        IEnumerable<IModContext<IMajorRecordGetter>> contexts = signature switch
        {
            "LVLI" => cache.ResolveAllSimpleContexts<ILeveledItemGetter>(key),
            "LVLN" => cache.ResolveAllSimpleContexts<ILeveledNpcGetter>(key),
            "LVSP" => cache.ResolveAllSimpleContexts<ILeveledSpellGetter>(key),
            "CONT" => cache.ResolveAllSimpleContexts<IContainerGetter>(key),
            "NPC_" => cache.ResolveAllSimpleContexts<INpcGetter>(key),
            "OTFT" => cache.ResolveAllSimpleContexts<IOutfitGetter>(key),
            "FLST" => cache.ResolveAllSimpleContexts<IFormListGetter>(key),
            _ => [],
        };
        return contexts.Select(c => (c.ModKey, c.Record)).Reverse().ToList();
    }

    /// <summary>One list of a record the merge works on: how to read its entries and how to put merged ones into a copy.</summary>
    private sealed record ListSpec(string Name, bool ByLevel, Func<IMajorRecordGetter, List<(EntryKey Key, object Entry)>> Read, Action<IMajorRecord, List<object>> Write);

    private static (EntryKey, object) Link<T>(IFormLinkGetter<T> link) where T : class, IMajorRecordGetter => (new EntryKey(link.FormKey, 0, 1), link);

    private static ExtendedList<IFormLinkGetter<T>> Links<T>(List<object> entries) where T : class, IMajorRecordGetter =>
        new(entries.Cast<IFormLinkGetter<T>>().Select(l => (IFormLinkGetter<T>)new FormLink<T>(l.FormKey)));

    private static (EntryKey, object) Item(IContainerEntryGetter e) =>
        (new EntryKey(e.Item.Item.FormKey, 0, (short)Math.Clamp(e.Item.Count, short.MinValue, short.MaxValue)), e);

    /// <summary>For lists Mutagen only lets be set at creation: emptied and filled.</summary>
    private static void Replace<T>(ExtendedList<T> list, IEnumerable<T> items) { var copy = items.ToList(); list.Clear(); list.AddRange(copy); }

    private static ExtendedList<ContainerEntry> Items(List<object> entries) => new(entries.Cast<IContainerEntryGetter>().Select(e => e.DeepCopy()));

    private static IReadOnlyList<ListSpec> Specs(IMajorRecordGetter record) => record switch
    {
        ILeveledItemGetter => [new("Entries", true, LeveledEntries, (r, e) => ((LeveledItem)r).Entries = new(e.Cast<ILeveledItemEntryGetter>().Select(x => x.DeepCopy())))],
        ILeveledNpcGetter => [new("Entries", true, LeveledEntries, (r, e) => ((LeveledNpc)r).Entries = new(e.Cast<ILeveledNpcEntryGetter>().Select(x => x.DeepCopy())))],
        ILeveledSpellGetter => [new("Entries", true, LeveledEntries, (r, e) => ((LeveledSpell)r).Entries = new(e.Cast<ILeveledSpellEntryGetter>().Select(x => x.DeepCopy())))],
        IContainerGetter => [new("Items", false, r => [.. (((IContainerGetter)r).Items ?? []).Select(Item)], (r, e) => ((Container)r).Items = Items(e))],
        INpcGetter =>
        [
            new("Items", false, r => [.. (((INpcGetter)r).Items ?? []).Select(Item)], (r, e) => ((Npc)r).Items = Items(e)),
            new("Factions", false, r => [.. ((INpcGetter)r).Factions.Select(f => (new EntryKey(f.Faction.FormKey, f.Rank, 1), (object)f))],
                (r, e) => Replace(((Npc)r).Factions, e.Cast<IRankPlacementGetter>().Select(f => f.DeepCopy()))),
            new("Perks", false, r => [.. (((INpcGetter)r).Perks ?? []).Select(p => (new EntryKey(p.Perk.FormKey, p.Rank, 1), (object)p))],
                (r, e) => ((Npc)r).Perks = new(e.Cast<IPerkPlacementGetter>().Select(p => p.DeepCopy()))),
            new("Spells", false, r => [.. (((INpcGetter)r).ActorEffect ?? []).Select(Link)], (r, e) => ((Npc)r).ActorEffect = Links<ISpellRecordGetter>(e)),
            new("Packages", false, r => [.. ((INpcGetter)r).Packages.Select(Link)], (r, e) => Replace(((Npc)r).Packages, Links<IPackageGetter>(e))),
            new("Keywords", false, r => [.. (((INpcGetter)r).Keywords ?? []).Select(Link)], (r, e) => ((Npc)r).Keywords = Links<IKeywordGetter>(e)),
        ],
        IOutfitGetter => [new("Items", false, r => [.. (((IOutfitGetter)r).Items ?? []).Select(Link)], (r, e) => ((Outfit)r).Items = Links<IOutfitTargetGetter>(e))],
        IFormListGetter => [new("Items", false, r => [.. ((IFormListGetter)r).Items.Select(Link)], (r, e) => Replace(((FormList)r).Items, Links<ISkyrimMajorRecordGetter>(e)))],
        _ => [],
    };

    private static List<(EntryKey Key, object Entry)> LeveledEntries(IMajorRecordGetter record) => record switch
    {
        ILeveledItemGetter l => (l.Entries ?? []).Where(e => e.Data is not null).Select(e => (new EntryKey(e.Data!.Reference.FormKey, e.Data.Level, e.Data.Count), (object)e)).ToList(),
        ILeveledNpcGetter l => (l.Entries ?? []).Where(e => e.Data is not null).Select(e => (new EntryKey(e.Data!.Reference.FormKey, e.Data.Level, e.Data.Count), (object)e)).ToList(),
        ILeveledSpellGetter l => (l.Entries ?? []).Where(e => e.Data is not null).Select(e => (new EntryKey(e.Data!.Reference.FormKey, e.Data.Level, e.Data.Count), (object)e)).ToList(),
        _ => [],
    };

    /// <summary>Every list's entries together (the plan's adds / removes against the winner).</summary>
    private static List<(EntryKey Key, object Entry)> Entries(IMajorRecordGetter record) => Specs(record).SelectMany(spec => spec.Read(record)).ToList();

    /// <summary>
    /// The merged record (a copy of the winner with each list merged), or null when it would equal the winner. A list's
    /// entries are grouped by what they name: for each item (NPC, spell, faction, package, sub-list…) the entries of the last
    /// plugin that changed them against the original win — so a plugin's addition or removal survives a later plugin that
    /// did not touch that item, and two plugins changing the same item do not double it.
    /// </summary>
    public static IMajorRecord? Merge(IReadOnlyList<IMajorRecordGetter> versions)
    {
        var specs = Specs(versions[^1]);
        if (specs.Count == 0) return null;
        IMajorRecord? copy = null;
        foreach (var spec in specs)
        {
            var lists = versions.Select(spec.Read).ToList();
            if (MergeList(lists, spec.ByLevel) is not { } merged) continue;
            if (spec.ByLevel && merged.Count > 255) continue; // the format's limit: such a list needs sub-lists, left alone
            copy ??= (IMajorRecord)versions[^1].DeepCopy();
            spec.Write(copy, merged);
        }
        return copy;
    }

    /// <summary>One list merged item by item; null when the result is the winner's list.</summary>
    private static List<object>? MergeList(List<List<(EntryKey Key, object Entry)>> lists, bool byLevel)
    {
        List<(EntryKey Key, object Entry)> Of(int version, FormKey item) => lists[version].Where(e => e.Key.Reference == item).ToList();
        static bool Same(List<(EntryKey Key, object Entry)> a, List<(EntryKey Key, object Entry)> b) =>
            a.Count == b.Count && a.Select(e => e.Key).OrderBy(k => k.Level).ThenBy(k => k.Count).SequenceEqual(b.Select(e => e.Key).OrderBy(k => k.Level).ThenBy(k => k.Count));

        // Items in the winner's order first (package priority, list order), then the ones only other versions have.
        var items = lists[^1].Select(e => e.Key.Reference).Concat(lists.SelectMany(l => l.Select(e => e.Key.Reference))).Distinct().ToList();
        var merged = new List<(EntryKey Key, object Entry, int Order)>();
        var order = 0;
        foreach (var item in items)
        {
            var original = Of(0, item);
            var chosen = original;
            for (var v = 1; v < lists.Count; v++)
            {
                var mine = Of(v, item);
                if (!Same(mine, original)) chosen = mine;
            }
            foreach (var (key, entry) in chosen) merged.Add((key, entry, order++));
        }
        // Leveled lists are kept by level, as the CK sorts them; equal levels keep their first-seen order.
        if (byLevel) merged = merged.OrderBy(m => m.Key.Level).ThenBy(m => m.Order).ToList();

        var winner = lists[^1].GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Count());
        if (winner.Values.Sum() == merged.Count && merged.GroupBy(m => m.Key).All(g => winner.GetValueOrDefault(g.Key) == g.Count())) return null;
        return merged.Select(m => m.Entry).ToList();
    }
}
