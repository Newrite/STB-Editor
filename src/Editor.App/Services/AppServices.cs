using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StbEditor.App.Services;

internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor");

    public static string LogFile => Path.Combine(Root, "logs", "editor.log");
    public static string CrashFolder => Path.Combine(Root, "crashes");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string ImGuiIni => Path.Combine(Root, "layout.ini");
    public static string CatalogCache => Path.Combine(Root, "cache", "catalog");
    public static string BackupFolder => Path.Combine(Root, "backups");
}

internal enum LogLevel { Info, Warning, Error }

internal sealed record LogLine(DateTime Time, LogLevel Level, string Text);

/// <summary>File log plus an in-memory tail for the Log panel. Thread-safe.</summary>
internal static class Log
{
    private const int TailSize = 2000;
    private static readonly ConcurrentQueue<LogLine> Tail = new();
    private static readonly object FileGate = new();
    private static StreamWriter? _writer;

    public static int Version { get; private set; }

    public static void Initialize(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        // Keep the previous session's log for crash investigation.
        if (File.Exists(file)) File.Copy(file, file + ".prev", overwrite: true);
        _writer = new StreamWriter(file, append: false, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public static IEnumerable<LogLine> Lines => Tail;

    public static void Info(string text) => Write(LogLevel.Info, text);

    /// <summary>"memory 1234 MB (managed 900 MB)" — process private bytes and the GC heap, for load lines.</summary>
    public static string Memory()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return $"memory {process.PrivateMemorySize64 >> 20} MB (managed {GC.GetTotalMemory(false) >> 20} MB)";
    }
    public static void Warn(string text) => Write(LogLevel.Warning, text);
    public static void Error(string text) => Write(LogLevel.Error, text);

    public static void Write(LogLevel level, string text)
    {
        var line = new LogLine(DateTime.Now, level, text);
        Tail.Enqueue(line);
        while (Tail.Count > TailSize) Tail.TryDequeue(out _);
        Version++;

        lock (FileGate)
            _writer?.WriteLine($"{line.Time:HH:mm:ss.fff} [{level}] {text}");
    }
}

internal static class CrashReport
{
    public static string Write(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CrashFolder);
            var path = Path.Combine(AppPaths.CrashFolder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, $"{DateTime.Now:O}\n{Environment.OSVersion}\n\n{ex}\n");
            Log.Error($"crash: {ex}");
            return path;
        }
        catch
        {
            return "(could not write report)";
        }
    }
}

/// <summary>User settings, JSON in %LOCALAPPDATA%\STBEditor.</summary>
internal sealed class Settings
{
    public string? GameFolder { get; set; }

    /// <summary>null = search next to the game, "" = plain install without MO2.</summary>
    public string? Mo2Folder { get; set; }

    public string? Mo2Profile { get; set; }
    public bool OpenOnStartup { get; set; } = true;

    /// <summary>Path of the active file (restored on the next start).</summary>
    public string? ActivePluginPath { get; set; }
    public List<string> RecentActivePlugins { get; set; } = [];

    /// <summary>Plugins to load (their masters come along); null = the whole load order.</summary>
    public List<string>? LoadOnly { get; set; }

    /// <summary>Mouse wheel zoom: units per notch × this (Shift ×4, Ctrl ×0.25).</summary>
    public float WheelZoomSpeed { get; set; } = 1f;

    /// <summary>RMB flying speed, units per second.</summary>
    public float FlySpeed { get; set; } = 800f;

    /// <summary>Wheel zoom glides instead of jumping.</summary>
    public bool SmoothZoom { get; set; } = true;

    /// <summary>Double-click on a teleport door asks before loading the other cell (as the Creation Kit).</summary>
    public bool ConfirmDoorTravel { get; set; } = true;

    /// <summary>Frames per second at most (0 = monitor refresh). In the background: 15.</summary>
    public int FpsLimit { get; set; } = 60;

    /// <summary>Panels the user closed (by their English name). Log and Load Order start closed: View opens them.</summary>
    public List<string> ClosedPanels { get; set; } = ["Log", "Load Order"];

    /// <summary>Tint actors in Flat / unlit shading so they stand out in dark interiors.</summary>
    public bool HighlightActors { get; set; } = true;

    /// <summary>Show record header bookkeeping (form version, version control, raw flags) in the field views.</summary>
    public bool ShowRecordHeader { get; set; }

    /// <summary>Colour theme by name (<c>Theme.Palettes</c>).</summary>
    public string Theme { get; set; } = "STB";

    /// <summary>Record comparison: hide the rows where every column has the same value.</summary>
    public bool CompareOnlyDifferences { get; set; }

    /// <summary>Interface language: "en" or "ru".</summary>
    public string Language { get; set; } = "en";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile), Options) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Warn($"settings ignored: {ex.Message}");
        }

        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"settings not saved: {ex.Message}");
        }
    }
}

/// <summary>
/// --game &lt;folder&gt;, --mo2 &lt;folder|none&gt;, --profile &lt;name&gt;, --open (load without the dialog),
/// --screenshot &lt;file.bmp&gt; (render until loaded, save the frame, exit — for automated checks),
/// --select &lt;FormID&gt;, --search &lt;text&gt;, --cell &lt;FormID|EditorID&gt; (load it into the render window).
/// </summary>
internal sealed record CommandLine(string? Game, string? Mo2, string? Profile, bool Open, string? Screenshot, string? Select, string? Search, string? Cell, string? CameraPose, string? PickAt)
{
    public static CommandLine Parse(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var mo2 = Value("--mo2");
        if (mo2 is "none") mo2 = "";

        return new CommandLine(
            Value("--game"),
            mo2,
            Value("--profile"),
            args.Contains("--open", StringComparer.OrdinalIgnoreCase),
            Value("--screenshot"),
            Value("--select"),
            Value("--search"),
            Value("--cell"),
            Value("--camera"),
            Value("--pick"));
    }
}

internal static class Screenshot
{
    /// <summary>Writes BGRA pixels as a 32-bit top-down BMP — no imaging dependency needed.</summary>
    public static void SaveBmp(string path, int width, int height, byte[] bgra)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new BinaryWriter(File.Create(path));
        var imageSize = width * height * 4;

        w.Write((ushort)0x4D42);
        w.Write(14 + 40 + imageSize);
        w.Write(0);
        w.Write(14 + 40);

        w.Write(40);
        w.Write(width);
        w.Write(-height);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(0);
        w.Write(imageSize);
        w.Write(2835);
        w.Write(2835);
        w.Write(0);
        w.Write(0);

        w.Write(bgra);
    }
}
