using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda.Plugins;
using StbEditor.Data.Raw;

namespace StbEditor.Data;

/// <summary>
/// "Use Info" / xEdit's "Referenced By": which records point at a record. Built from the raw bytes of
/// every loaded plugin (no Mutagen, so a million placed references cost seconds, not minutes):
/// 4-byte values inside subrecords that are the FormID of an existing record count as a link. Only
/// the winning version of each source counts (a later plugin that drops the link removes the use).
/// Placed references only look at their reference subrecords (base, enable parent, linked refs,
/// owner, lock key…); other records are scanned whole and the caller verifies a hit with Mutagen.
/// Each plugin's hits are cached on disk, keyed by its file and its masters' files.
/// </summary>
public sealed class UseIndex
{
    /// <summary>One user of a record: the source, its type, its cell or topic, the subrecord the link sits in, the plugin of the winning version.</summary>
    public readonly record struct Use(FormKey Source, string Type, FormKey? Parent, string Subrecord, PluginInfo Plugin)
    {
        public bool IsPlaced => PlacedTypes.Contains(Type);
    }

    private readonly Workspace _workspace;
    private readonly uint[] _target, _source, _type, _parent, _sub;
    private readonly ushort[] _plugin;

    public int Count => _target.Length;
    public TimeSpan BuildTime { get; }
    public int CachedPlugins { get; }

    private UseIndex(Workspace workspace, List<Pair> pairs, TimeSpan time, int cached)
    {
        _workspace = workspace;
        pairs.Sort((a, b) => a.Target.CompareTo(b.Target));
        _target = new uint[pairs.Count];
        _source = new uint[pairs.Count];
        _type = new uint[pairs.Count];
        _parent = new uint[pairs.Count];
        _sub = new uint[pairs.Count];
        _plugin = new ushort[pairs.Count];
        for (var i = 0; i < pairs.Count; i++)
        {
            var p = pairs[i];
            (_target[i], _source[i], _type[i], _parent[i], _sub[i], _plugin[i]) = (p.Target, p.Source, p.Type, p.Parent, p.Sub, p.Plugin);
        }
        BuildTime = time;
        CachedPlugins = cached;
    }

    /// <summary>Records whose winning version points at <paramref name="target"/>.</summary>
    public List<Use> UsesOf(FormKey target)
    {
        var result = new List<Use>();
        if (_workspace.ToFormId(target) is not { } id) return result;
        var lo = LowerBound(id);
        for (var i = lo; i < _target.Length && _target[i] == id; i++)
        {
            if (_workspace.ToFormKey(_source[i]) is not { } source) continue;
            FormKey? parent = _parent[i] == 0 ? null : _workspace.ToFormKey(_parent[i]);
            result.Add(new Use(source, FourCC(_type[i]), parent, FourCC(_sub[i]), _workspace.Active[_plugin[i]]));
        }
        return result;
    }

    /// <summary>How many records point at <paramref name="target"/> (cheap: no allocation).</summary>
    public int CountOf(FormKey target)
    {
        if (_workspace.ToFormId(target) is not { } id) return 0;
        var lo = LowerBound(id);
        var hi = lo;
        while (hi < _target.Length && _target[hi] == id) hi++;
        return hi - lo;
    }

    private int LowerBound(uint id)
    {
        int lo = 0, hi = _target.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_target[mid] < id) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static string FourCC(uint v) => string.Create(4, v, static (chars, x) =>
    {
        for (var i = 0; i < 4; i++) chars[i] = (char)((x >> (8 * i)) & 0xFF);
    });

    private static uint Code(string fourCC) => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(fourCC));

    // ------------------------------------------------------------------ build

    private record struct Pair(uint Target, uint Source, uint Type, uint Parent, uint Sub, ushort Plugin);

    /// <summary>A hit in a plugin's own FormID space (high byte = its master index, or its master count for its own records).</summary>
    private record struct LocalHit(uint Target, uint Source, uint Type, uint Parent, uint Sub);

    private static readonly HashSet<string> PlacedTypes = ["REFR", "ACHR", "PGRE", "PMIS", "PHZD", "PARW", "PBAR", "PBEA", "PCON", "PFLA"];

    /// <summary>
    /// Link fields of a placed reference: subrecord → (first offset, stride; stride 0 = that offset only).
    /// Exact offsets matter here: a lock level or a faction rank is a small number, and small numbers
    /// are FormIDs of Skyrim.esm's hardcoded forms (15 = gold).
    /// </summary>
    private static readonly Dictionary<uint, (int Start, int Stride)> PlacedLinks = new (string, int, int)[]
    {
        ("NAME", 0, 0), ("XESP", 0, 0), ("XLKR", 0, 4), ("XOWN", 0, 0), ("XLOC", 4, 0), ("XTEL", 0, 0), ("XEZN", 0, 0),
        ("XLCN", 0, 0), ("XLRT", 0, 4), ("XAPR", 0, 8), ("XLIB", 0, 0), ("XLRL", 0, 0), ("XNDP", 0, 0), ("XMBR", 0, 0),
        ("XPOD", 0, 4), ("XLTW", 0, 0), ("XPWR", 0, 8), ("XLRM", 0, 4), ("XEMI", 0, 0), ("XCZC", 0, 0), ("XATR", 0, 0),
        ("INAM", 0, 0), ("XTNM", 0, 0), ("XSPC", 0, 0), ("LNAM", 0, 0), ("XHOR", 0, 0), ("XMRC", 0, 0), ("XCZR", 0, 0),
        ("XORD", 0, 4), ("PDTO", 4, 0), ("VMAD", 0, 1),
    }.ToDictionary(x => Code(x.Item1), x => (x.Item2, x.Item3));

    /// <summary>Text, paths and string IDs: never links, and full of bytes that look like FormIDs.</summary>
    private static readonly HashSet<uint> TextSubrecords = new[]
    {
        "EDID", "FULL", "DESC", "MODL", "MOD2", "MOD3", "MOD4", "MOD5", "MODT", "MO2T", "MO3T", "MO4T", "MO5T", "ICON", "MICO",
        "ICO2", "MIC2", "TX00", "TX01", "TX02", "TX03", "TX04", "TX05", "TX06", "TX07", "DMDL", "DMDT", "SHRT", "ITXT",
        "SCTX", "XXXX", "OFST", "VHGT", "VNML", "VCLR", "VTXT", "NVNM", "TVDT",
        "MHDT", "XCLW", "RDMP",
    }.Select(Code).ToHashSet();

    private static readonly uint Vmad = Code("VMAD"), Xxxx = Code("XXXX"), Pdto = Code("PDTO");

    /// <summary>Alternate texture lists: {name, TXST, 3D index} entries after names of any length.</summary>
    private static readonly HashSet<uint> AlternateTextureSubrecords = new[] { "MODS", "MO2S", "MO3S", "MO4S", "MO5S", "DMDS" }.Select(Code).ToHashSet();

    /// <summary>A dialogue line's text and prompt: string IDs in localized plugins, which look like Skyrim.esm FormIDs.</summary>
    private static readonly HashSet<uint> InfoText = new[] { "NAM1", "RNAM" }.Select(Code).ToHashSet();
    private static readonly HashSet<uint> LandLinks = new[] { "ATXT", "BTXT" }.Select(Code).ToHashSet();

    public static UseIndex Build(Workspace workspace, string cacheFolder, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var clock = Stopwatch.StartNew();
        Directory.CreateDirectory(cacheFolder);
        var plugins = workspace.Active.Select((p, i) => (Plugin: p, Index: i)).Where(x => x.Plugin.IsLoaded && x.Plugin.Header is not null).ToList();
        // Taken once: the workspace may be closed while this runs (save, reload); maps taken later would lose every master.
        var maps = plugins.ToDictionary(x => x.Index, x => GlobalMap(workspace, x.Plugin));

        // Pass 1: every record that exists (global FormID) and which plugin's version wins; deleted winners give no uses.
        progress?.Report("Use Info: listing records");
        var winner = new Dictionary<uint, int>(2_000_000);
        var deleted = new HashSet<uint>();
        var headers = new Dictionary<int, List<(uint Local, bool Deleted)>>();
        foreach (var (plugin, index) in plugins)
        {
            cancel.ThrowIfCancellationRequested();
            var map = maps[index];
            var list = ReadHeadersCached(cacheFolder, plugin);
            headers[index] = list;
            foreach (var (local, isDeleted) in list)
            {
                if (Global(map, local) is not { } id) continue;
                winner[id] = index;
                if (isDeleted) deleted.Add(id);
                else deleted.Remove(id);
            }
        }

        // Pass 2: links, per plugin in parallel (cached per plugin and its masters).
        var all = new List<Pair>();
        var cached = 0;
        var done = 0;
        var gate = new object();
        Parallel.ForEach(plugins, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, item =>
        {
            var (plugin, index) = item;
            var map = maps[index];
            // The stamp is taken before the scan: a plugin replaced meanwhile is not cached under its new stamp.
            var stamp = Stamp(workspace, plugin);
            var hits = TryReadHits(cacheFolder, plugin, stamp, out var fromCache)
                ?? ScanPlugin(plugin, map, winner, cancel);
            cancel.ThrowIfCancellationRequested();
            // Links into a master that is not loaded are dropped, so such a scan is not cached for when it is.
            var complete = Enumerable.Range(0, Math.Min(plugin.Header!.Masters.Count, 255)).All(i => map[i] is not null);
            if (!fromCache && complete) WriteHits(cacheFolder, plugin, stamp, hits);

            var pairs = new List<Pair>(hits.Count);
            foreach (var hit in hits)
            {
                if (Global(map, hit.Source) is not { } source || Global(map, hit.Target) is not { } target) continue;
                // Only the winning, not deleted version of the source counts; the target must exist.
                if (!winner.TryGetValue(source, out var win) || win != index || deleted.Contains(source)) continue;
                if (!winner.ContainsKey(target)) continue;
                var parent = hit.Parent == 0 ? 0u : Global(map, hit.Parent) ?? 0u;
                pairs.Add(new Pair(target, source, hit.Type, parent, hit.Sub, (ushort)index));
            }
            lock (gate)
            {
                all.AddRange(pairs);
                if (fromCache) cached++;
                done++;
                if (done % 16 == 0) progress?.Report($"Use Info: {done}/{plugins.Count} plugins");
            }
        });

        return new UseIndex(workspace, all, clock.Elapsed, cached);
    }

    /// <summary>File high byte → (load-order prefix, light) of the plugin it means; null where the master is not loaded.</summary>
    private static (uint Prefix, bool Light)?[] GlobalMap(Workspace workspace, PluginInfo plugin)
    {
        var masters = plugin.Header!.Masters;
        var map = new (uint, bool)?[256];
        for (var i = 0; i < masters.Count && i < 255; i++)
        {
            var master = workspace.Find(ModKey.FromFileName(masters[i]));
            if (master is { LoadIndex: >= 0, IsLoaded: true }) map[i] = (master.FormIdPrefix, master.IsLight && workspace.Game.SupportsLightPlugins);
        }
        map[Math.Min(masters.Count, 255)] = (plugin.FormIdPrefix, plugin.IsLight && workspace.Game.SupportsLightPlugins);
        return map;
    }

    private static uint? Global((uint Prefix, bool Light)?[] map, uint local)
    {
        if (map[local >> 24] is not { } m) return null;
        return m.Light ? m.Prefix | (local & 0xFFF) : m.Prefix | (local & 0xFFFFFF);
    }

    private static List<LocalHit> ScanPlugin(PluginInfo plugin, (uint Prefix, bool Light)?[] map, Dictionary<uint, int> exists, CancellationToken cancel)
    {
        var hits = new List<LocalHit>();
        var file = File.ReadAllBytes(plugin.Location.Path);
        var masters = plugin.Header!.Masters.Count;
        var placed = PlacedTypes.Select(Code).ToHashSet();
        uint navm = Code("NAVM"), navi = Code("NAVI"), tes4 = Code("TES4"), land = Code("LAND"), grup = Code("GRUP"), info = Code("INFO");
        var records = 0;

        // A value is a link when its high byte is a master (or the plugin itself) and the record exists.
        bool IsLink(uint value)
        {
            var high = (int)(value >> 24);
            if (high > masters || (value & 0xFFFFFF) == 0) return false;
            return Global(map, value) is { } id && exists.ContainsKey(id);
        }

        var groups = new Stack<(long End, int Type, uint Label)>();
        long pos = 0;
        while (pos + RawFormat.RecordHeaderSize <= file.Length)
        {
            while (groups.Count > 0 && pos >= groups.Peek().End) groups.Pop();
            var span = file.AsSpan((int)pos);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(span);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
            if (type == grup)
            {
                groups.Push((pos + size, BinaryPrimitives.ReadInt32LittleEndian(span[12..]), BinaryPrimitives.ReadUInt32LittleEndian(span[8..])));
                pos += RawFormat.GroupHeaderSize;
                continue;
            }

            if ((++records & 0xFFF) == 0) cancel.ThrowIfCancellationRequested();
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
            var formId = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
            var bodyStart = pos + RawFormat.RecordHeaderSize;
            if (bodyStart + size > file.Length) break;
            pos = bodyStart + size;
            if (type == navm || type == navi || type == tes4) continue;

            // Parent: the cell (children groups 6, 8, 9, 10) or topic (7) the record sits in.
            uint parent = 0;
            foreach (var g in groups)
            {
                if (g.Type is 6 or 7 or 8 or 9 or 10) { parent = g.Label; break; }
            }

            ReadOnlyMemory<byte> body = file.AsMemory((int)bodyStart, (int)size);
            if ((flags & RawFormat.CompressedFlag) != 0)
            {
                try { body = RawFormat.Inflate(body); }
                catch { continue; }
            }

            var isPlaced = placed.Contains(type);
            var isLand = type == land;
            var isInfo = type == info;
            var bytes = body.Span;
            var at = 0;
            var bigSize = -1;
            while (at + 6 <= bytes.Length)
            {
                var code = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
                var length = (int)BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at + 4)..]);
                at += 6;
                if (bigSize >= 0) { length = bigSize; bigSize = -1; }
                if (at + length > bytes.Length) break;
                if (code == Xxxx && length == 4)
                {
                    bigSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
                    at += 4;
                    continue;
                }
                var data = bytes.Slice(at, length);
                at += length;
                int start = 0, step;
                if (isPlaced && code == Vmad)
                {
                    // A reference's scripts: the object properties, read by the format (no fragments on references).
                    foreach (var value in VmadObjects(data))
                        if (value != formId && IsLink(value)) hits.Add(new LocalHit(value, formId, type, parent, code));
                    continue;
                }
                if (AlternateTextureSubrecords.Contains(code))
                {
                    foreach (var value in AlternateTextures(data))
                        if (value != formId && IsLink(value)) hits.Add(new LocalHit(value, formId, type, parent, code));
                    continue;
                }
                if (isPlaced)
                {
                    if (!PlacedLinks.TryGetValue(code, out var rule)) continue;
                    // Patrol idle topic: a topic FormID only when its type is 0 (else a 4-letter subtype).
                    if (code == Pdto && (data.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0)) continue;
                    (start, step) = rule.Stride == 0 ? (rule.Start, int.MaxValue / 2) : rule;
                }
                // Texture layers: the LTEX first, then quadrant and layer numbers.
                else if (isLand) { if (!LandLinks.Contains(code)) continue; step = int.MaxValue / 2; }
                else if (TextSubrecords.Contains(code) || (isInfo && InfoText.Contains(code))) continue;
                // Script properties sit after names of any length: every offset. Elsewhere links are 4-aligned.
                else step = code == Vmad ? 1 : 4;
                for (var o = start; o + 4 <= data.Length; o += step)
                {
                    var value = BinaryPrimitives.ReadUInt32LittleEndian(data[o..]);
                    if (value == formId || !IsLink(value)) continue;
                    hits.Add(new LocalHit(value, formId, type, parent, code));
                }
            }
        }
        return hits;
    }

    /// <summary>
    /// FormIDs of the object properties (single and arrays) of a VMAD's scripts — the part every
    /// record type shares; fragments that follow on quests, topics etc. are not read.
    /// </summary>
    private static List<uint> VmadObjects(ReadOnlySpan<byte> span)
    {
        var result = new List<uint>();
        var data = span.ToArray(); // small; local functions cannot hold a span
        try
        {
            var at = 0;
            int U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)); at += 2; return v; }
            int U8() => data[at++];
            int I32() { var v = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at)); at += 4; return v; }
            void Text() => at += U16();
            uint Object(int format)
            {
                // Format 2: unused int16, alias int16, FormID; format 1: FormID, alias, unused.
                var id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(format == 1 ? at : at + 4));
                at += 8;
                return id;
            }

            var version = U16();
            var objectFormat = U16();
            var scripts = U16();
            for (var s = 0; s < scripts; s++)
            {
                Text(); // script name
                if (version >= 4) U8(); // status
                var properties = U16();
                for (var p = 0; p < properties; p++)
                {
                    Text(); // property name
                    var kind = U8();
                    if (version >= 4) U8(); // status
                    switch (kind)
                    {
                        case 1: result.Add(Object(objectFormat)); break;
                        case 2: Text(); break;
                        case 3: case 4: at += 4; break;
                        case 5: at += 1; break;
                        case 11: { var n = I32(); for (var i = 0; i < n; i++) result.Add(Object(objectFormat)); break; }
                        case 12: { var n = I32(); for (var i = 0; i < n; i++) Text(); break; }
                        case 13: case 14: at += 4 * I32(); break;
                        case 15: at += I32(); break;
                        default: return result; // unknown kind: stop rather than misread
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // Truncated data: keep what was read.
        }
        return result;
    }

    /// <summary>TXST FormIDs of an alternate texture list: count, then {uint32 length, name, TXST, int32 3D index} each.</summary>
    private static List<uint> AlternateTextures(ReadOnlySpan<byte> data)
    {
        var result = new List<uint>();
        if (data.Length < 4) return result;
        var count = BinaryPrimitives.ReadInt32LittleEndian(data);
        var at = 4;
        for (var i = 0; i < count && at + 4 <= data.Length; i++)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
            if (length < 0 || at + 4 + length + 8 > data.Length) break;
            at += 4 + length;
            result.Add(BinaryPrimitives.ReadUInt32LittleEndian(data[at..]));
            at += 8;
        }
        return result;
    }

    // ------------------------------------------------------------------ cache

    private const uint Magic = 0x49555453; // "STUI"
    private const int Version = 4; // 2: exact link offsets in placed references; 3: their VMAD read by the format; 4: more reference links, alternate textures

    private static string CachePath(string folder, PluginInfo plugin)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(plugin.Location.Path.ToLowerInvariant())))[..12];
        return Path.Combine(folder, $"{plugin.Name}.{hash}");
    }

    /// <summary>Size and write time of the plugin and of each master (hits depend on which records the masters have).</summary>
    private static List<long> Stamp(Workspace workspace, PluginInfo plugin)
    {
        var stamp = new List<long>();
        void Add(string path)
        {
            var info = new FileInfo(path);
            stamp.Add(info.Exists ? info.Length : -1);
            stamp.Add(info.Exists ? info.LastWriteTimeUtc.Ticks : -1);
        }
        Add(plugin.Location.Path);
        foreach (var master in plugin.Header!.Masters)
        {
            if (workspace.Find(ModKey.FromFileName(master)) is { } m) Add(m.Location.Path);
            else { stamp.Add(-2); stamp.Add(-2); }
        }
        return stamp;
    }

    private static List<(uint, bool)> ReadHeadersCached(string folder, PluginInfo plugin)
    {
        var path = CachePath(folder, plugin) + ".hdr";
        var info = new FileInfo(plugin.Location.Path);
        try
        {
            if (File.Exists(path))
            {
                using var reader = new BinaryReader(File.OpenRead(path));
                if (reader.ReadUInt32() == Magic && reader.ReadInt32() == Version && reader.ReadInt64() == info.Length && reader.ReadInt64() == info.LastWriteTimeUtc.Ticks)
                {
                    var count = reader.ReadInt32();
                    var list = new List<(uint, bool)>(count);
                    for (var i = 0; i < count; i++)
                    {
                        var v = reader.ReadUInt32();
                        list.Add((v, reader.ReadBoolean()));
                    }
                    return list;
                }
            }
        }
        catch (IOException) { }


        var result = new List<(uint, bool)>();
        var file = File.ReadAllBytes(plugin.Location.Path);
        foreach (var (type, flags, formId, _, _) in RawFormat.RecordHeaders(file))
            if (type != "TES4") result.Add((formId, (flags & 0x20) != 0));
        try
        {
            using var writer = new BinaryWriter(File.Create(path));
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(info.Length);
            writer.Write(info.LastWriteTimeUtc.Ticks);
            writer.Write(result.Count);
            foreach (var (id, isDeleted) in result)
            {
                writer.Write(id);
                writer.Write(isDeleted);
            }
        }
        catch (IOException) { }
        return result;
    }

    private static List<LocalHit>? TryReadHits(string folder, PluginInfo plugin, List<long> stamp, out bool fromCache)
    {
        fromCache = false;
        var path = CachePath(folder, plugin) + ".use";
        if (!File.Exists(path)) return null;
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version) return null;
            var count = reader.ReadInt32();
            if (count != stamp.Count) return null;
            for (var i = 0; i < count; i++)
                if (reader.ReadInt64() != stamp[i]) return null;
            var hits = new List<LocalHit>(reader.ReadInt32());
            for (var i = hits.Capacity; i > 0; i--)
                hits.Add(new LocalHit(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32()));
            fromCache = true;
            return hits;
        }
        catch (IOException) { return null; }

    }

    private static void WriteHits(string folder, PluginInfo plugin, List<long> stamp, List<LocalHit> hits)
    {
        try
        {
            using var writer = new BinaryWriter(File.Create(CachePath(folder, plugin) + ".use"));
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(stamp.Count);
            foreach (var s in stamp) writer.Write(s);
            writer.Write(hits.Count);
            foreach (var h in hits)
            {
                writer.Write(h.Target);
                writer.Write(h.Source);
                writer.Write(h.Type);
                writer.Write(h.Parent);
                writer.Write(h.Sub);
            }
        }
        catch (IOException) { }
    }
}
