using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Data.Raw;

namespace StbEditor.Core;

/// <summary>
/// xEdit's «Compare to…»: a plugin against another version of the same file (a backup, a colleague's copy, last
/// release). Records are matched by FormKey through each file's own master list and compared byte for byte (bodies
/// decompressed, so recompression is not a change); changed records then get the fields that differ.
/// </summary>
public static class PluginDiff
{
    public enum Kind { Added, Removed, Changed }

    /// <summary>A difference: the record, what happened to it, and for a changed record the differing fields.</summary>
    public sealed record Entry(FormKey Key, string Signature, string? EditorId, Kind Kind, IReadOnlyList<string> Fields);

    private const uint CompressedFlag = 0x00040000;

    /// <param name="current">The plugin (its file name is the records' ModKey in both files).</param>
    /// <param name="other">The other version, wherever it lies and whatever it is called.</param>
    public static List<Entry> Compare(string current, string other, SkyrimRelease release, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var self = ModKey.FromFileName(Path.GetFileName(current));
        progress?.Report("reading");
        var mine = Index(current, self);
        var theirs = Index(other, self);
        cancel.ThrowIfCancellationRequested();

        var entries = new List<Entry>();
        var changed = new HashSet<FormKey>();
        foreach (var (key, record) in mine)
        {
            if (!theirs.TryGetValue(key, out var old)) entries.Add(new Entry(key, record.Type, null, Kind.Added, []));
            else if (old.Type != record.Type || (old.Flags & ~CompressedFlag) != (record.Flags & ~CompressedFlag) || !old.Data.Span.SequenceEqual(record.Data.Span)) changed.Add(key);
        }
        foreach (var (key, record) in theirs)
            if (!mine.ContainsKey(key)) entries.Add(new Entry(key, record.Type, null, Kind.Removed, []));

        // Names and fields from Mutagen: only the records that differ are looked at.
        progress?.Report($"{changed.Count:N0} changed record(s): fields");
        var wanted = changed.Concat(entries.Select(e => e.Key)).ToHashSet();
        var now = Records(current, self, release, wanted);
        var before = Records(other, self, release, wanted);
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var record = e.Kind == Kind.Added ? now.GetValueOrDefault(e.Key) : before.GetValueOrDefault(e.Key);
            entries[i] = e with { EditorId = record?.EditorID };
        }
        foreach (var key in changed)
        {
            cancel.ThrowIfCancellationRequested();
            now.TryGetValue(key, out var a);
            before.TryGetValue(key, out var b);
            entries.Add(new Entry(key, mine[key].Type, a?.EditorID ?? b?.EditorID, Kind.Changed, a is null || b is null ? [] : Fields(a, b)));
        }
        return entries.OrderBy(e => e.Kind).ThenBy(e => e.Signature, StringComparer.Ordinal).ThenBy(e => e.EditorId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Every record of a file by FormKey (FormIDs resolved through the file's own masters).</summary>
    private static Dictionary<FormKey, (string Type, uint Flags, ReadOnlyMemory<byte> Data)> Index(string path, ModKey self)
    {
        var masters = PluginHeader.Read(path).Masters.Select(m => ModKey.FromFileName(m)).ToList();
        var records = new Dictionary<FormKey, (string, uint, ReadOnlyMemory<byte>)>();
        foreach (var record in RawFormat.Records(File.ReadAllBytes(path)))
        {
            if (record.Type == "TES4") continue;
            var index = (int)(record.FormId >> 24);
            var mod = index < masters.Count ? masters[index] : self;
            records[new FormKey(mod, record.FormId & 0xFFFFFF)] = (record.Type, record.Flags, record.Data);
        }
        return records;
    }

    private static Dictionary<FormKey, IMajorRecordGetter> Records(string path, ModKey self, SkyrimRelease release, HashSet<FormKey> wanted)
    {
        var found = new Dictionary<FormKey, IMajorRecordGetter>();
        if (wanted.Count == 0) return found;
        var mod = SkyrimMod.CreateFromBinaryOverlay(new ModPath(self, path), release);
        foreach (var record in mod.EnumerateMajorRecords())
            if (wanted.Contains(record.FormKey)) found[record.FormKey] = record;
        return found;
    }

    /// <summary>Paths of the fields whose values differ (at most 12).</summary>
    public static IReadOnlyList<string> Fields(IMajorRecordGetter a, IMajorRecordGetter b)
    {
        Dictionary<string, string> Leaves(IMajorRecordGetter r)
        {
            var map = new Dictionary<string, string>();
            try
            {
                foreach (var leaf in RecordFields.Leaves(r, maxDepth: 12, maxListItems: 2000))
                    map[leaf.PathText] = RecordFields.Repr(leaf.Value);
            }
            catch { }
            return map;
        }
        var left = Leaves(a);
        var right = Leaves(b);
        return left.Keys.Union(right.Keys)
            .Where(k => !RecordFields.IsRecordHeader(k.Split('/')[0]) && left.GetValueOrDefault(k) != right.GetValueOrDefault(k))
            .Take(12).ToList();
    }
}
