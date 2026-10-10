using System.Diagnostics;
using Microsoft.Win32;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;

namespace StbEditor.Data;

/// <summary>
/// A Skyrim installation: which executable it is, what plugin features its engine supports and
/// where its INI and plugin lists live. Data files are the same for every release, so the editor
/// itself is version-agnostic; only the limits of what it may write depend on the engine.
/// </summary>
public sealed class GameInstall
{
    /// <summary>First runtime that reads header 1.71 and the full 0x000–0xFFF light-plugin range.</summary>
    private static readonly Version ExtendedEslRuntime = new(1, 6, 1130);

    public required string GameFolder { get; init; }
    public required string DataFolder { get; init; }
    public required string ExePath { get; init; }
    public required Version? ExeVersion { get; init; }
    public required SkyrimRelease Release { get; init; }
    public required Language Language { get; init; }

    /// <summary>Folder with Skyrim.ini / SkyrimPrefs.ini (MO2 profile with local INIs, or My Games).</summary>
    public required string IniFolder { get; init; }

    /// <summary>Folder with plugins.txt / loadorder.txt when the game is run without MO2.</summary>
    public required string AppDataFolder { get; init; }

    public Mo2Instance? Mo2 { get; init; }

    /// <summary>Backported Extended ESL Support present (SE/VR) — same plugin limits as 1.6.1130+.</summary>
    public required bool HasBees { get; init; }

    public GameRelease GameRelease => Release.ToGameRelease();

    public bool IsVr => Release == SkyrimRelease.SkyrimVR;

    /// <summary>Header 1.71 and light FormIDs from 0x000 are readable by the engine.</summary>
    public bool SupportsExtendedEsl => HasBees || (ExeVersion is { } v && !IsVr && v >= ExtendedEslRuntime);

    /// <summary>Light plugins exist at all (VR has none without ESL support for VR).</summary>
    public bool SupportsLightPlugins => !IsVr || HasBees;

    public float MaxHeaderVersion => SupportsExtendedEsl ? 1.71f : 1.70f;

    public string SkyrimIni => Path.Combine(IniFolder, IsVr ? "SkyrimVR.ini" : "Skyrim.ini");

    public string DisplayName
    {
        get
        {
            var name = Release switch
            {
                SkyrimRelease.SkyrimVR => "Skyrim VR",
                SkyrimRelease.SkyrimSEGog => "Skyrim SE (GOG)",
                SkyrimRelease.EnderalSE => "Enderal SE",
                _ => ExeVersion is { Major: 1, Minor: >= 6 } ? "Skyrim AE" : "Skyrim SE",
            };
            return ExeVersion is null ? name : $"{name} {ExeVersion}";
        }
    }

    /// <summary>
    /// Builds the installation description from a game folder. <paramref name="mo2Folder"/> may be
    /// null (searched next to the game), empty (plain install without MO2) or an explicit path.
    /// </summary>
    public static GameInstall Detect(string gameFolder, string? mo2Folder = null, string? profile = null)
    {
        gameFolder = Path.GetFullPath(gameFolder);

        var (exe, release) = FindExecutable(gameFolder);
        var version = ReadVersion(exe);

        Mo2Instance? mo2 = null;
        if (mo2Folder is null)
        {
            var found = Mo2Instance.FindNear(gameFolder);
            if (found is not null) mo2 = Mo2Instance.Open(found, profile);
        }
        else if (mo2Folder.Length > 0)
        {
            mo2 = Mo2Instance.Open(mo2Folder, profile);
        }

        var myGames = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games",
            MyGamesFolderName(release));

        var iniFolder = mo2 is not null && mo2.HasLocalInis(release == SkyrimRelease.SkyrimVR ? "SkyrimVR.ini" : "Skyrim.ini")
            ? mo2.ProfileFolder
            : myGames;

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            MyGamesFolderName(release));

        var dataFolder = Path.Combine(gameFolder, "Data");

        var iniName = release == SkyrimRelease.SkyrimVR ? "SkyrimVR.ini" : "Skyrim.ini";

        return new GameInstall
        {
            GameFolder = gameFolder,
            DataFolder = dataFolder,
            ExePath = exe,
            ExeVersion = version,
            Release = release,
            Language = DetectLanguage(Path.Combine(iniFolder, iniName)),
            IniFolder = iniFolder,
            AppDataFolder = appData,
            Mo2 = mo2,
            HasBees = FindBees(dataFolder, mo2),
        };
    }

    /// <summary>
    /// The game the editor sits in (e.g. C:\Skyrim\STB Editor\STBEditor.exe, the usual place for
    /// tools launched from MO2), else the Steam/GOG registry keys, else null.
    /// </summary>
    public static string? FindInstalledGame()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SkyrimSE.exe")) || File.Exists(Path.Combine(dir.FullName, "SkyrimVR.exe")))
                return dir.FullName;
        }


        string[] keys =
        [
            @"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition",
            @"SOFTWARE\Bethesda Softworks\Skyrim Special Edition",
            @"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim VR",
            @"SOFTWARE\WOW6432Node\GOG.com\Games\1711230643",
        ];

        foreach (var key in keys)
        {
            try
            {
                using var reg = Registry.LocalMachine.OpenSubKey(key);
                var path = reg?.GetValue("Installed Path") as string ?? reg?.GetValue("path") as string;
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
            }
            catch
            {
                // Registry access is best effort.
            }
        }

        return Directory.Exists(@"C:\Skyrim") ? @"C:\Skyrim" : null;
    }

    private static (string Exe, SkyrimRelease Release) FindExecutable(string gameFolder)
    {
        var vr = Path.Combine(gameFolder, "SkyrimVR.exe");
        if (File.Exists(vr)) return (vr, SkyrimRelease.SkyrimVR);

        var se = Path.Combine(gameFolder, "SkyrimSE.exe");
        var gog = File.Exists(Path.Combine(gameFolder, "Galaxy64.dll"));
        if (File.Exists(se)) return (se, gog ? SkyrimRelease.SkyrimSEGog : SkyrimRelease.SkyrimSE);

        var le = Path.Combine(gameFolder, "TESV.exe");
        if (File.Exists(le)) return (le, SkyrimRelease.SkyrimLE);

        throw new FileNotFoundException($"No SkyrimSE.exe / SkyrimVR.exe in {gameFolder}");
    }

    private static Version? ReadVersion(string exe)
    {
        try
        {
            // SkyrimSE.exe 1.5.97 carries 1.0.0.0 in the numeric fields; only the string is right.
            var info = FileVersionInfo.GetVersionInfo(exe);
            foreach (var text in new[] { info.FileVersion, info.ProductVersion })
            {
                var token = text?.Split(' ', ',')[0].Trim();
                if (Version.TryParse(token, out var parsed) && parsed > new Version(1, 0, 0, 0)) return parsed;
            }

            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        }
        catch
        {
            return null;
        }
    }

    private static string MyGamesFolderName(SkyrimRelease release) => release switch
    {
        SkyrimRelease.SkyrimVR => "Skyrim VR",
        SkyrimRelease.SkyrimSEGog => "Skyrim Special Edition GOG",
        SkyrimRelease.SkyrimLE => "Skyrim",
        SkyrimRelease.EnderalSE => "Enderal Special Edition",
        _ => "Skyrim Special Edition",
    };

    private static bool FindBees(string dataFolder, Mo2Instance? mo2)
    {
        const string dll = "BackportedESLSupport.dll";

        if (File.Exists(Path.Combine(dataFolder, "SKSE", "Plugins", dll))) return true;
        if (mo2 is null) return false;

        return mo2.EnabledModFolders().Any(folder => File.Exists(Path.Combine(folder, "SKSE", "Plugins", dll)));
    }

    /// <summary>Skyrim writes the full language name: <c>sLanguage=RUSSIAN</c>.</summary>
    private static Language DetectLanguage(string skyrimIni)
    {
        var value = TextFiles.ReadIniValue(skyrimIni, "sLanguage")?.ToLowerInvariant();

        return value switch
        {
            "ru" or "russian" => Language.Russian,
            "de" or "german" => Language.German,
            "fr" or "french" => Language.French,
            "es" or "spanish" => Language.Spanish,
            "it" or "italian" => Language.Italian,
            "pl" or "polish" => Language.Polish,
            "cz" or "czech" => Language.Czech,
            "ja" or "japanese" => Language.Japanese,
            "zhhans" or "chinese" => Language.Chinese,
            _ => Language.English,
        };
    }
}
