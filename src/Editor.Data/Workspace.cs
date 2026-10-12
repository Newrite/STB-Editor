using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using StbEditor.Data.Raw;

namespace StbEditor.Data;

/// <summary>
/// The open load order: every plugin read-only through Mutagen overlays, plus the tables that turn
/// FormKeys into the FormIDs the game and xEdit show. Editing goes through a separate document on
/// top of this (the active file).
/// </summary>
public sealed class Workspace : IDisposable
{
    private readonly Dictionary<ModKey, PluginInfo> _byKey = new();
    private readonly List<PluginInfo> _regular = [];
    private readonly List<PluginInfo> _light = [];
    private readonly Lazy<ILinkCache<ISkyrimMod, ISkyrimModGetter>> _linkCache;

    public GameInstall Game { get; }

    /// <summary>Everything listed in the load order, including disabled and missing plugins.</summary>
    public IReadOnlyList<PluginInfo> Plugins { get; }

    /// <summary>Active plugins in load order (masters first, as the engine sorts them).</summary>
    public IReadOnlyList<PluginInfo> Active { get; }

    public LoadOrder<ISkyrimModGetter> LoadOrder { get; }

    /// <summary>Encoding for text in plugins that have none yet — the one most plugins of the build use.</summary>
    public PluginStringEncoding DefaultEncoding { get; }

    public ILinkCache<ISkyrimMod, ISkyrimModGetter> LinkCache => _linkCache.Value;

    private Workspace(GameInstall game, List<PluginInfo> plugins, List<PluginInfo> active)
    {
        Game = game;
        Plugins = plugins;
        Active = active;

        // Index prefixes follow the list of active plugins, not the ones Mutagen managed to open:
        // a broken plugin must not shift the FormIDs of everything below it.
        foreach (var plugin in active)
        {
            _byKey[plugin.ModKey] = plugin;

            if (plugin.IsLight && game.SupportsLightPlugins)
            {
                plugin.FormIdPrefix = 0xFE000000u | ((uint)_light.Count << 12);
                _light.Add(plugin);
            }
            else
            {
                plugin.FormIdPrefix = (uint)_regular.Count << 24;
                _regular.Add(plugin);
            }
        }

        foreach (var plugin in plugins)
            _byKey.TryAdd(plugin.ModKey, plugin);

        LoadOrder = new LoadOrder<ISkyrimModGetter>(active.Where(p => p.Mod is not null).Select(p => p.Mod!));
        _linkCache = new Lazy<ILinkCache<ISkyrimMod, ISkyrimModGetter>>(
            () => LoadOrder.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>(), LazyThreadSafetyMode.ExecutionAndPublication);

        DefaultEncoding = active
            .Where(p => p.StringEncoding is PluginStringEncoding.Utf8 or PluginStringEncoding.Ansi)
            .GroupBy(p => p.StringEncoding)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault(PluginStringEncoding.Utf8);
    }

    /// <param name="extraPlugin">
    /// The active file when it is not in the profile's load order yet (a plugin the editor just
    /// created in MO2's overwrite): loaded last and active, so its edits are visible.
    /// </param>
    /// <param name="only">
    /// Load just these plugins (and their masters, and the extra plugin) — as the Creation Kit's Data
    /// dialog. The others stay in the load order (FormID indices as in the game) but are not opened.
    /// Null: everything.
    /// </param>
    public static Workspace Open(GameInstall game, IProgress<string>? progress = null, CancellationToken cancel = default, string? extraPlugin = null,
        IReadOnlyCollection<string>? only = null)
    {
        progress?.Report("Reading load order");
        var locations = LoadOrderReader.Read(game).ToList();

        if (extraPlugin is not null && File.Exists(extraPlugin))
        {
            var name = Path.GetFileName(extraPlugin);
            var index = locations.FindIndex(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var location = new PluginLocation(name, extraPlugin, "active file", Path.GetDirectoryName(extraPlugin)!, true);
            if (index < 0) locations.Add(location);
            else if (!locations[index].Enabled || !locations[index].Exists) locations[index] = location;
        }

        var plugins = locations
            .Select(l => new PluginInfo { Location = l, ModKey = ModKey.FromFileName(l.Name) })
            .ToList();

        progress?.Report("Reading plugin headers");
        Parallel.ForEach(plugins.Where(p => p.Location.Exists), new ParallelOptions { CancellationToken = cancel }, plugin =>
        {
            try
            {
                plugin.Header = PluginHeader.Read(plugin.Location.Path);
            }
            catch (Exception ex)
            {
                plugin.Error = ex.Message;
            }
        });

        // The engine loads every master-flagged file before regular plugins regardless of the
        // list order; MO2 keeps loadorder.txt sorted that way, but a hand-edited list may not be.
        var active = plugins.Where(p => p.IsActive && p.Header is not null).ToList();
        active = [.. active.Where(p => p.IsMaster), .. active.Where(p => !p.IsMaster)];
        for (var i = 0; i < active.Count; i++) active[i].LoadIndex = i;

        if (only is { Count: > 0 })
        {
            var wanted = new HashSet<string>(only, StringComparer.OrdinalIgnoreCase);
            if (extraPlugin is not null) wanted.Add(Path.GetFileName(extraPlugin));
            var byName = active.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>(wanted);
            while (queue.TryDequeue(out var name))
                if (byName.TryGetValue(name, out var plugin))
                    foreach (var master in plugin.Header!.Masters)
                        if (wanted.Add(master)) queue.Enqueue(master);
            foreach (var plugin in active) plugin.Skipped = !wanted.Contains(plugin.Name);
        }

        var done = 0;
        Parallel.ForEach(active.Where(p => !p.Skipped), new ParallelOptions { CancellationToken = cancel }, plugin =>
        {
            try
            {
                (plugin.StringEncoding, plugin.MixedEncoding) = StringEncodingDetector.Detect(plugin.Location.Path, plugin.Header!);
                plugin.Mod = OpenOverlay(game, plugin);
            }
            catch (Exception ex)
            {
                plugin.Error = ex.Message;
            }

            var n = Interlocked.Increment(ref done);
            if (n % 16 == 0) progress?.Report($"Opening plugins {n}/{active.Count}");
        });

        return new Workspace(game, plugins, active);
    }

    private static ISkyrimModGetter OpenOverlay(GameInstall game, PluginInfo plugin)
    {
        var text = StringEncodingDetector.ReadEncoding(game.Language);
        var (folder, bsas) = plugin.Header?.IsLocalized == true ? StringSources(game, plugin) : (null, null);
        var strings = new StringsReadParameters
        {
            TargetLanguage = game.Language,
            NonLocalizedEncodingOverride = text,
            NonTranslatedEncodingOverride = text,
            StringsFolderOverride = folder is null ? (Noggog.DirectoryPath?)null : new Noggog.DirectoryPath(folder),
            BsaFolderOverride = bsas is null ? (Noggog.DirectoryPath?)null : new Noggog.DirectoryPath(bsas),
        };

        var param = new BinaryReadParameters { StringsParam = strings };
        return SkyrimMod.CreateFromBinaryOverlay(new ModPath(plugin.ModKey, plugin.Location.Path), game.Release, param);
    }

    /// <summary>
    /// Where a localized plugin's .strings files are, as the game sees them through MO2: Mutagen looks
    /// only next to the plugin, but a plugin from a mod folder (cleaned Update / DLC masters) keeps its
    /// strings in the game's own archives. Loose strings in the highest-priority mod win, then Data\Strings,
    /// then the archives of Data. (null, null): the default lookup next to the plugin is right.
    /// </summary>
    internal static (string? StringsFolder, string? BsaFolder) StringSources(GameInstall game, PluginInfo plugin)
    {
        var folder = Path.GetDirectoryName(plugin.Location.Path)!;
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.DataFolder)), StringComparison.OrdinalIgnoreCase))
            return (null, null);

        var name = Path.GetFileNameWithoutExtension(plugin.Name);
        bool HasStrings(string strings) => Directory.Exists(strings) && Directory.EnumerateFiles(strings, name + "_*.*strings").Any();
        if (HasStrings(Path.Combine(folder, "Strings"))) return (null, null);

        if (game.Mo2 is { } mo2)
        {
            // EnabledModFolders runs from low to high priority: the winner is searched first.
            foreach (var mod in mo2.EnabledModFolders().Reverse())
            {
                var strings = Path.Combine(mod, "Strings");
                if (HasStrings(strings)) return (strings, game.DataFolder);
            }
        }
        var data = Path.Combine(game.DataFolder, "Strings");
        return (HasStrings(data) ? data : null, game.DataFolder);
    }

    public PluginInfo? Find(ModKey key) => _byKey.GetValueOrDefault(key);

    /// <summary>FormID as the game/xEdit shows it (load-order prefixed), or null for an unloaded master.</summary>
    public uint? ToFormId(FormKey key)
    {
        if (!_byKey.TryGetValue(key.ModKey, out var plugin) || plugin.LoadIndex < 0) return null;

        return plugin.IsLight && Game.SupportsLightPlugins
            ? plugin.FormIdPrefix | (key.ID & 0xFFF)
            : plugin.FormIdPrefix | (key.ID & 0x00FFFFFF);
    }

    public string FormIdText(FormKey key) => ToFormId(key) is { } id ? id.ToString("X8") : $"[{key.ModKey.FileName}] {key.ID:X6}";

    public FormKey? ToFormKey(uint formId)
    {
        var high = formId >> 24;

        if (high == 0xFE && Game.SupportsLightPlugins)
        {
            var index = (int)((formId >> 12) & 0xFFF);
            return index < _light.Count ? new FormKey(_light[index].ModKey, formId & 0xFFF) : null;
        }

        return high < _regular.Count ? new FormKey(_regular[(int)high].ModKey, formId & 0x00FFFFFF) : null;
    }

    /// <summary>Accepts "0E01A319", "01A319:STB.esp" or "STB.esp:01A319".</summary>
    public FormKey? ParseFormReference(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;

        var colon = text.IndexOf(':');
        if (colon > 0)
        {
            var left = text[..colon].Trim();
            var right = text[(colon + 1)..].Trim();
            var (idText, plugin) = PluginFileName.IsPlugin(right) ? (left, right) : (right, left);

            if (uint.TryParse(idText, System.Globalization.NumberStyles.HexNumber, null, out var local))
                return new FormKey(ModKey.FromFileName(plugin), local & 0x00FFFFFF);
            return null;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var id) ? ToFormKey(id) : null;
    }

    /// <summary>
    /// Closes the plugin files. Overlays keep esp/esm open; without this the files stay locked
    /// for xEdit and MO2 until garbage collection.
    /// </summary>
    public void Dispose()
    {
        foreach (var plugin in Plugins)
        {
            (plugin.Mod as IDisposable)?.Dispose();
            plugin.Mod = null;
        }
    }
}
