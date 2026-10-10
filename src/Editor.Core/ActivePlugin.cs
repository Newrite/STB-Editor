using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using StbEditor.Data;
using StbEditor.Data.Raw;

namespace StbEditor.Core;

/// <summary>
/// The plugin edits are saved into (the Creation Kit's "active file"). Either an existing plugin
/// of the load order or a new one that is created on the first save.
/// </summary>
public sealed class ActivePlugin
{
    public required ModKey ModKey { get; init; }

    /// <summary>Where the file is (or will be written).</summary>
    public required string Path { get; init; }

    public required bool IsNew { get; init; }
    public bool Light { get; init; }
    public bool Master { get; init; }

    /// <summary>Header version to write (1.70, or 1.71 where the game reads it).</summary>
    public float HeaderVersion { get; init; } = 1.70f;

    public PluginStringEncoding Encoding { get; init; } = PluginStringEncoding.Utf8;

    private uint _next;
    private readonly uint _limit;
    private readonly uint _floor;
    private readonly HashSet<uint> _used;

    private ActivePlugin(uint first, uint limit, uint floor = 0x800, HashSet<uint>? used = null)
    {
        _next = first;
        _limit = limit;
        _floor = floor;
        _used = used ?? [];
    }

    public string Name => ModKey.FileName.String;

    /// <summary>Next free object ID (what the header's NextObjectID will say).</summary>
    public uint NextObjectId => _next;

    public int Remaining => (int)Math.Max(0, (long)_limit - _next + 1);

    /// <summary>
    /// A FormKey for a new record of this plugin. Full plugins count up; light plugins (2048 or
    /// 4096 IDs) also reuse gaps left by deleted records.
    /// </summary>
    public FormKey Allocate()
    {
        while (_next <= _limit && _used.Contains(_next)) _next++;

        if (_next > _limit && Light)
        {
            // Wrap around once and look for a gap from the bottom of the range.
            for (var id = _floor; id <= _limit; id++)
            {
                if (_used.Contains(id)) continue;
                _used.Add(id);
                return new FormKey(ModKey, id);
            }
        }

        if (_next > _limit)
            throw new InvalidOperationException(Light
                ? $"{Name} is a light plugin and every FormID 0x{_floor:X3}–0x{_limit:X3} is taken; save into a full plugin"
                : $"{Name} has no free FormIDs left");

        _used.Add(_next);
        return new FormKey(ModKey, _next++);
    }

    /// <summary>Same plugin identity and FormID counter, written to another file (tests, "save a copy").</summary>
    public ActivePlugin RetargetedTo(string path) => new(_next, _limit, _floor, [.. _used])
    {
        ModKey = ModKey,
        Path = path,
        IsNew = IsNew,
        Light = Light,
        Master = Master,
        HeaderVersion = HeaderVersion,
        Encoding = Encoding,
    };

    /// <summary>Existing plugin: continues after the highest ID the file uses.</summary>
    public static ActivePlugin ForExisting(PluginInfo plugin, GameInstall game)
    {
        if (plugin.Header is not { } header) throw new InvalidOperationException($"{plugin.Name} has no readable header");
        if (header.IsLocalized) throw new InvalidOperationException($"{plugin.Name} is localized (.STRINGS); editing localized plugins is not supported yet");

        var light = plugin.IsLight;
        var used = RawFormat.OwnObjectIds(plugin.Location.Path, header.Masters.Count);
        var own = used.Count == 0 ? 0u : used.Max();

        // From 0x800 like the Creation Kit. A light plugin with header 1.71 on a runtime that
        // reads it (1.6.1130+ or BEES) may also use 0x001–0x7FF.
        var floor = light && header.Version >= 1.705f && game.SupportsExtendedEsl ? 0x001u : 0x800u;
        var limit = light ? 0xFFFu : 0xFFFFFFu;
        var first = Math.Max(Math.Max(Math.Min(header.NextObjectId, limit + 1), own + 1), floor);

        return new ActivePlugin(first, limit, floor, used)
        {
            ModKey = plugin.ModKey,
            Path = plugin.Location.Path,
            IsNew = false,
            Light = light,
            Master = header.IsMasterFlagged,
            HeaderVersion = header.Version,
            Encoding = plugin.StringEncoding is PluginStringEncoding.Ansi ? PluginStringEncoding.Ansi : PluginStringEncoding.Utf8,
        };
    }

    /// <summary>New plugin: CK-style IDs from 0x800, header 1.70 unless the game needs 1.71.</summary>
    public static ActivePlugin ForNew(string path, bool light, bool master, GameInstall game, PluginStringEncoding encoding)
    {
        var key = ModKey.FromFileName(System.IO.Path.GetFileName(path));
        return new ActivePlugin(0x800, light ? 0xFFFu : 0xFFFFFFu)
        {
            ModKey = key,
            Path = path,
            IsNew = true,
            Light = light,
            Master = master,
            HeaderVersion = 1.70f,
            Encoding = encoding == PluginStringEncoding.Ansi ? PluginStringEncoding.Ansi : PluginStringEncoding.Utf8,
        };
    }
}

/// <summary>Where a reference lives and what it is, captured when it is first edited.</summary>
/// <param name="Cell">Owning CELL (a worldspace's persistent cell for persistent exterior refs).</param>
/// <param name="Record">Winning REFR/ACHR, null for references created in this session.</param>
public sealed record RefContext(
    FormKey Cell,
    FormKey? Worldspace,
    bool Persistent,
    bool Interior,
    IMajorRecordGetter? Record,
    FormKey Base,
    string BaseSignature);
