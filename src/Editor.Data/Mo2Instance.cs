namespace StbEditor.Data;

/// <summary>
/// An MO2 instance and one of its profiles. The editor never runs under MO2's virtual file
/// system: it reads the profile lists itself and resolves files by mod priority.
/// </summary>
public sealed class Mo2Instance
{
    public required string Folder { get; init; }
    public required string ProfileName { get; init; }
    public required IReadOnlyList<string> Profiles { get; init; }

    public string ModsFolder => Path.Combine(Folder, "mods");
    public string OverwriteFolder => Path.Combine(Folder, "overwrite");
    public string ProfileFolder => Path.Combine(Folder, "profiles", ProfileName);
    public string PluginsTxt => Path.Combine(ProfileFolder, "plugins.txt");
    public string LoadOrderTxt => Path.Combine(ProfileFolder, "loadorder.txt");

    /// <summary>modlist.txt lives in the profile; some MO2 versions keep it in the instance root.</summary>
    public string ModlistTxt
    {
        get
        {
            var inProfile = Path.Combine(ProfileFolder, "modlist.txt");
            return File.Exists(inProfile) ? inProfile : Path.Combine(Folder, "modlist.txt");
        }
    }

    /// <summary>Profile uses its own INI files (MO2 "local game settings").</summary>
    public bool HasLocalInis(string iniName)
    {
        var settings = Path.Combine(ProfileFolder, "settings.ini");
        var local = TextFiles.ReadIniValue(settings, "LocalSettings");
        if (local is not null && !local.Equals("true", StringComparison.OrdinalIgnoreCase)) return false;
        return File.Exists(Path.Combine(ProfileFolder, iniName));
    }

    public static Mo2Instance Open(string folder, string? profile)
    {
        var profilesRoot = Path.Combine(folder, "profiles");
        var profiles = Directory.Exists(profilesRoot)
            ? Directory.EnumerateDirectories(profilesRoot).Select(p => Path.GetFileName(p)!).Order().ToList()
            : [];

        profile ??= SelectedProfile(folder, profiles) ?? profiles.FirstOrDefault() ?? "Default";

        return new Mo2Instance { Folder = Path.GetFullPath(folder), ProfileName = profile, Profiles = profiles };
    }

    /// <summary>The MO2 folder need not sit inside the game: here it is "C:\Skyrim\[STB] Mod Organizer".</summary>
    public static string? FindNear(string gameFolder)
    {
        foreach (var root in new[] { gameFolder, Path.GetDirectoryName(gameFolder) })
        {
            if (root is null || !Directory.Exists(root)) continue;

            foreach (var candidate in Directory.EnumerateDirectories(root))
            {
                if (Directory.Exists(Path.Combine(candidate, "mods"))
                    && Directory.Exists(Path.Combine(candidate, "profiles")))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>The profile MO2 had selected last (ModOrganizer.ini, <c>selected_profile=@ByteArray(STB)</c>).</summary>
    private static string? SelectedProfile(string folder, List<string> profiles)
    {
        var value = TextFiles.ReadIniValue(Path.Combine(folder, "ModOrganizer.ini"), "selected_profile");
        if (value is null) return null;

        if (value.StartsWith("@ByteArray(", StringComparison.Ordinal) && value.EndsWith(')'))
            value = value["@ByteArray(".Length..^1];

        return profiles.FirstOrDefault(p => p.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Enabled mods in MO2 priority order: first line has the highest priority.</summary>
    public List<string> ReadModPriority()
    {
        var mods = new List<string>();

        foreach (var raw in TextFiles.ReadLines(ModlistTxt))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '+') continue;

            var name = line[1..].Trim();
            if (name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)) continue;
            mods.Add(name);
        }

        return mods;
    }

    /// <summary>Folders of enabled mods from the lowest to the highest priority, overwrite last.</summary>
    public IEnumerable<string> EnabledModFolders()
    {
        foreach (var mod in Enumerable.Reverse(ReadModPriority()))
        {
            var folder = Path.Combine(ModsFolder, mod);
            if (Directory.Exists(folder)) yield return folder;
        }

        if (Directory.Exists(OverwriteFolder)) yield return OverwriteFolder;
    }
}
