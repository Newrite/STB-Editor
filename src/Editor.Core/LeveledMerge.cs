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

    /// <summary>The list's versions in load order (the original first).</summary>
    private static List<(ModKey ModKey, IMajorRecordGetter Record)> Versions(ILinkCache cache, FormKey key, string signature)
    {
        IEnumerable<IModContext<IMajorRecordGetter>> contexts = signature switch
        {
            "LVLI" => cache.ResolveAllSimpleContexts<ILeveledItemGetter>(key),
            "LVLN" => cache.ResolveAllSimpleContexts<ILeveledNpcGetter>(key),
            "LVSP" => cache.ResolveAllSimpleContexts<ILeveledSpellGetter>(key),
            _ => [],
        };
        return contexts.Select(c => (c.ModKey, c.Record)).Reverse().ToList();
    }

    private static List<(EntryKey Key, object Entry)> Entries(IMajorRecordGetter record) => record switch
    {
        ILeveledItemGetter l => (l.Entries ?? []).Where(e => e.Data is not null).Select(e => (new EntryKey(e.Data!.Reference.FormKey, e.Data.Level, e.Data.Count), (object)e)).ToList(),
        ILeveledNpcGetter l => (l.Entries ?? []).Where(e => e.Data is not null).Select(e => (new EntryKey(e.Data!.Reference.FormKey, e.Data.Level, e.Data.Count), (object)e)).ToList(),
        ILeveledSpellGetter l => (l.Entries ?? []).Where(e => e.Data is not null).Select(e => (new EntryKey(e.Data!.Reference.FormKey, e.Data.Level, e.Data.Count), (object)e)).ToList(),
        _ => [],
    };

    /// <summary>
    /// The merged list (a copy of the winner with merged entries), or null when it would equal the winner. Entries are
    /// grouped by what they spawn: for each item (NPC, spell, sub-list) the entries of the last plugin that changed them
    /// against the original win — so a plugin's addition or removal survives a later plugin that did not touch that
    /// item, and two plugins re-levelling the same item do not double it.
    /// </summary>
    public static IMajorRecord? Merge(IReadOnlyList<IMajorRecordGetter> versions)
    {
        var lists = versions.Select(Entries).ToList();
        List<(EntryKey Key, object Entry)> Of(int version, FormKey item) => lists[version].Where(e => e.Key.Reference == item).ToList();
        static bool Same(List<(EntryKey Key, object Entry)> a, List<(EntryKey Key, object Entry)> b) =>
            a.Count == b.Count && a.Select(e => e.Key).OrderBy(k => k.Level).ThenBy(k => k.Count).SequenceEqual(b.Select(e => e.Key).OrderBy(k => k.Level).ThenBy(k => k.Count));

        var items = lists.SelectMany(l => l.Select(e => e.Key.Reference)).Distinct().ToList();
        var merged = new List<(EntryKey Key, object Entry, int Order)>();
        var order = 0;
        foreach (var item in items)
        {
            var original = Of(0, item);
            var chosen = original;
            for (var v = 1; v < versions.Count; v++)
            {
                var mine = Of(v, item);
                if (!Same(mine, original)) chosen = mine;
            }
            foreach (var (key, entry) in chosen) merged.Add((key, entry, order++));
        }
        // Lists are kept by level, as the CK sorts them; equal levels keep their first-seen order.
        merged = merged.OrderBy(m => m.Key.Level).ThenBy(m => m.Order).ToList();

        var winner = lists[^1].GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Count());
        if (winner.Values.Sum() == merged.Count && merged.GroupBy(m => m.Key).All(g => winner.GetValueOrDefault(g.Key) == g.Count())) return null;
        if (merged.Count > 255) return null; // the format's limit: such a list needs sub-lists, left alone

        switch (versions[^1])
        {
            case ILeveledItemGetter li:
            {
                var copy = li.DeepCopy();
                copy.Entries = new ExtendedList<LeveledItemEntry>(merged.Select(m => ((ILeveledItemEntryGetter)m.Entry).DeepCopy()));
                return copy;
            }
            case ILeveledNpcGetter ln:
            {
                var copy = ln.DeepCopy();
                copy.Entries = new ExtendedList<LeveledNpcEntry>(merged.Select(m => ((ILeveledNpcEntryGetter)m.Entry).DeepCopy()));
                return copy;
            }
            case ILeveledSpellGetter ls:
            {
                var copy = ls.DeepCopy();
                copy.Entries = new ExtendedList<LeveledSpellEntry>(merged.Select(m => ((ILeveledSpellEntryGetter)m.Entry).DeepCopy()));
                return copy;
            }
            default:
                return null;
        }
    }
}
