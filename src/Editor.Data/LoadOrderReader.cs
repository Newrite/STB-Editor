namespace StbEditor.Data;

/// <summary>A plugin of the load order as found on disk.</summary>
/// <param name="Source">MO2 mod name, "Data", "overwrite" or "missing".</param>
/// <param name="SourceFolder">Folder that contains the file — where saves go.</param>
public sealed record PluginLocation(string Name, string Path, string Source, string SourceFolder, bool Enabled)
{
    public bool Exists => Path.Length > 0;
}

/// <summary>
/// Reads the load order either from an MO2 profile (order from loadorder.txt, activity from
/// plugins.txt, files by mod priority) or from a plain install (plugins.txt in AppData).
/// </summary>
public static class LoadOrderReader
{
    /// <summary>Masters the engine always loads; MO2 does not list them in plugins.txt.</summary>
    private static readonly string[] OfficialMasters =
    [
        "Skyrim.esm",
        "Update.esm",
        "Dawnguard.esm",
        "HearthFires.esm",
        "Dragonborn.esm",
    ];

    private static readonly string[] VrMasters = ["SkyrimVR.esm"];

    public static IReadOnlyList<PluginLocation> Read(GameInstall game)
    {
        var implicitPlugins = ImplicitPlugins(game);
        return game.Mo2 is null ? ReadPlain(game, implicitPlugins) : ReadMo2(game, game.Mo2, implicitPlugins);
    }

    /// <summary>
    /// Official masters plus Creation Club content from Skyrim.ccc that is actually present.
    /// They are active without an asterisk in plugins.txt.
    /// </summary>
    private static List<string> ImplicitPlugins(GameInstall game)
    {
        var list = new List<string>(OfficialMasters);
        if (game.IsVr) list.AddRange(VrMasters);

        var ccc = Path.Combine(game.GameFolder, game.IsVr ? "SkyrimVR.ccc" : "Skyrim.ccc");
        foreach (var line in TextFiles.ReadLines(ccc))
        {
            var name = line.Trim();
            if (name.Length > 0 && !list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
        }

        return list;
    }

    private static IReadOnlyList<PluginLocation> ReadMo2(GameInstall game, Mo2Instance mo2, List<string> implicitPlugins)
    {
        var enabled = ReadEnabled(mo2.PluginsTxt);
        var order = ReadNames(mo2.LoadOrderTxt);
        var files = IndexPluginFiles(game.DataFolder, mo2);

        // loadorder.txt normally lists everything; plugins only in plugins.txt go to the end.
        foreach (var name in ReadNames(mo2.PluginsTxt))
            if (!order.Contains(name, StringComparer.OrdinalIgnoreCase)) order.Add(name);

        return Resolve(order, files, name =>
            enabled.Contains(name) || implicitPlugins.Contains(name, StringComparer.OrdinalIgnoreCase)
            || name.StartsWith("cc", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<PluginLocation> ReadPlain(GameInstall game, List<string> implicitPlugins)
    {
        var pluginsTxt = Path.Combine(game.AppDataFolder, "plugins.txt");
        var enabled = ReadEnabled(pluginsTxt);

        var order = new List<string>();
        foreach (var name in implicitPlugins)
            if (File.Exists(Path.Combine(game.DataFolder, name))) order.Add(name);

        foreach (var name in ReadNames(pluginsTxt))
            if (!order.Contains(name, StringComparer.OrdinalIgnoreCase)) order.Add(name);

        var files = IndexPluginFiles(game.DataFolder, null);
        return Resolve(order, files, name =>
            enabled.Contains(name) || implicitPlugins.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    private static List<PluginLocation> Resolve(
        List<string> order,
        Dictionary<string, PluginLocation> files,
        Func<string, bool> isEnabled)
    {
        var result = new List<PluginLocation>(order.Count);

        foreach (var name in order)
        {
            var enabled = isEnabled(name);
            result.Add(files.TryGetValue(name, out var found)
                ? found with { Enabled = enabled }
                : new PluginLocation(name, string.Empty, "missing", string.Empty, enabled));
        }

        return result;
    }

    private static HashSet<string> ReadEnabled(string pluginsTxt)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in TextFiles.ReadLines(pluginsTxt))
        {
            var line = raw.Trim();
            if (line.StartsWith('*')) set.Add(line[1..].Trim());
        }

        return set;
    }

    private static List<string> ReadNames(string file)
    {
        var names = new List<string>();

        foreach (var raw in TextFiles.ReadLines(file))
        {
            var line = raw.Trim().TrimStart('*').Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            names.Add(line);
        }

        return names;
    }

    /// <summary>Plugin name → file. Lower-priority sources first so higher ones overwrite them.</summary>
    private static Dictionary<string, PluginLocation> IndexPluginFiles(string dataFolder, Mo2Instance? mo2)
    {
        var index = new Dictionary<string, PluginLocation>(StringComparer.OrdinalIgnoreCase);

        void Add(string folder, string source)
        {
            foreach (var file in EnumeratePlugins(folder))
            {
                var name = Path.GetFileName(file);
                index[name] = new PluginLocation(name, file, source, folder, false);
            }
        }

        Add(dataFolder, "Data");

        if (mo2 is not null)
        {
            foreach (var folder in mo2.EnabledModFolders())
            {
                var source = folder.Equals(mo2.OverwriteFolder, StringComparison.OrdinalIgnoreCase)
                    ? "overwrite"
                    : Path.GetFileName(folder);
                Add(folder, source);
            }
        }

        return index;
    }

    private static IEnumerable<string> EnumeratePlugins(string folder)
    {
        if (!Directory.Exists(folder)) return [];

        return Directory.EnumerateFiles(folder, "*.es?", SearchOption.TopDirectoryOnly)
            .Where(f => PluginFileName.IsPlugin(f));
    }
}

public static class PluginFileName
{
    public static bool IsPlugin(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".esp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".esm", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".esl", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsEsl(string name) => Path.GetExtension(name).Equals(".esl", StringComparison.OrdinalIgnoreCase);

    public static bool IsEsm(string name) => Path.GetExtension(name).Equals(".esm", StringComparison.OrdinalIgnoreCase);
}
