using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace StbEditor.Data;

/// <summary>One compiler message: file(line,column): text.</summary>
public readonly record struct PapyrusError(string File, int Line, int Column, string Message);

public sealed record PapyrusResult(bool Success, string Output, IReadOnlyList<PapyrusError> Errors, string? PexPath);

/// <summary>
/// The Papyrus compiler as the Creation Kit runs it: PapyrusCompiler.exe with the flags file, every
/// script source folder of the load order as an import (MO2: the winning mod first, as files resolve
/// in game), and the output next to the plugin being edited (its mod's Scripts folder).
/// </summary>
public sealed class PapyrusSetup
{
    private static readonly string[] SourceLayouts = [@"Scripts\Source", @"Source\Scripts"];
    private const string FlagsName = "TESV_Papyrus_Flags.flg";

    /// <summary>PapyrusCompiler.exe, or null when none was found.</summary>
    public string? Compiler { get; }

    /// <summary>Folders with .psc files, highest priority first.</summary>
    public IReadOnlyList<string> SourceFolders { get; }

    /// <summary>The folder holding TESV_Papyrus_Flags.flg (the compiler looks for it among the imports).</summary>
    public string? FlagsFolder { get; }

    private readonly GameInstall _game;

    private PapyrusSetup(GameInstall game, string? compiler, List<string> sources)
    {
        _game = game;
        Compiler = compiler;
        SourceFolders = sources;
        FlagsFolder = sources.FirstOrDefault(f => File.Exists(Path.Combine(f, FlagsName)));
    }

    public static PapyrusSetup Find(GameInstall game, string? compilerOverride = null)
    {
        var compiler = new[]
        {
            compilerOverride,
            Path.Combine(game.GameFolder, "Papyrus Compiler", "PapyrusCompiler.exe"),
            Path.Combine(game.DataFolder, "..", "Papyrus Compiler", "PapyrusCompiler.exe"),
        }.FirstOrDefault(p => p is not null && File.Exists(p));

        // Lowest priority first, then reversed: Data, MO2 mods by priority, overwrite last. Disabled mods
        // come after all of them: a mod holding only the game's script sources is usually kept disabled.
        var roots = new List<string> { game.DataFolder };
        if (game.Mo2 is { } mo2) roots.AddRange(mo2.EnabledModFolders());
        roots.Reverse();
        if (game.Mo2 is { } m && Directory.Exists(m.ModsFolder))
            roots.AddRange(Directory.EnumerateDirectories(m.ModsFolder).Where(d => !roots.Contains(d, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<string>();
        foreach (var root in roots)
        {
            foreach (var layout in SourceLayouts)
            {
                var folder = Path.GetFullPath(Path.Combine(root, layout));
                if (!Directory.Exists(folder) || !seen.Add(folder)) continue;
                if (!Directory.EnumerateFiles(folder, "*.psc").Any() && !File.Exists(Path.Combine(folder, FlagsName))) continue;
                sources.Add(folder);
            }
        }
        return new PapyrusSetup(game, compiler is null ? null : Path.GetFullPath(compiler), sources);
    }

    /// <summary>The .psc of a script as the compiler would import it (first folder that has it).</summary>
    public string? SourcePath(string script) =>
        SourceFolders.Select(f => Path.Combine(f, script + ".psc")).FirstOrDefault(File.Exists);

    /// <summary>
    /// Where scripts of a plugin go: its own folder (the mod folder under MO2) — Scripts for .pex, and the
    /// source layout that folder already has (else the one most of the load order uses).
    /// </summary>
    public (string Scripts, string Sources) OutputFor(string pluginPath)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(pluginPath))!;
        var sources = SourceLayouts.Select(l => Path.Combine(root, l)).FirstOrDefault(Directory.Exists);
        if (sources is null)
        {
            var common = SourceFolders.GroupBy(f => f.EndsWith(@"Scripts\Source", StringComparison.OrdinalIgnoreCase) ? SourceLayouts[0] : SourceLayouts[1])
                .OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? SourceLayouts[1];
            sources = Path.Combine(root, common);
        }
        return (Path.Combine(root, "Scripts"), sources);
    }

    /// <summary>What is missing to compile, or null when everything is there.</summary>
    public string? Problem =>
        Compiler is null ? $"PapyrusCompiler.exe not found (looked in {Path.Combine(_game.GameFolder, "Papyrus Compiler")}); set it in Settings"
        : FlagsFolder is null ? $"{FlagsName} not found: the game's script sources (Scripts.zip / Source\\Scripts) are not in the load order"
        : null;

    private static readonly Regex ErrorLine = new(@"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<msg>.+)$", RegexOptions.Compiled);

    /// <summary>
    /// Compiles one .psc into <paramref name="outputFolder"/>; its own folder is imported first, then
    /// <paramref name="extraImports"/>, then the load order's sources.
    /// </summary>
    public PapyrusResult Compile(string pscPath, string outputFolder, IEnumerable<string>? extraImports = null, CancellationToken cancel = default)
    {
        if (Problem is { } problem) return new PapyrusResult(false, problem, [], null);
        Directory.CreateDirectory(outputFolder);
        var imports = new List<string> { Path.GetDirectoryName(Path.GetFullPath(pscPath))! };
        imports.AddRange(extraImports ?? []);
        imports.AddRange(SourceFolders);
        imports = imports.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var pex = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(pscPath) + ".pex");
        var before = File.Exists(pex) ? File.GetLastWriteTimeUtc(pex) : DateTime.MinValue;
        var start = new ProcessStartInfo(Compiler!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Compiler)!,
        };
        start.ArgumentList.Add(Path.GetFullPath(pscPath));
        start.ArgumentList.Add($"-f={FlagsName}");
        start.ArgumentList.Add($"-i={string.Join(';', imports)}");
        start.ArgumentList.Add($"-o={Path.GetFullPath(outputFolder)}");

        var output = new StringBuilder();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        while (!process.WaitForExit(100))
        {
            if (!cancel.IsCancellationRequested) continue;
            try { process.Kill(); } catch (InvalidOperationException) { }
            return new PapyrusResult(false, "cancelled", [], null);
        }
        process.WaitForExit();

        var text = output.ToString();
        var errors = text.Split('\n')
            .Select(l => ErrorLine.Match(l.TrimEnd('\r')))
            .Where(m => m.Success)
            .Select(m => new PapyrusError(m.Groups["file"].Value, int.Parse(m.Groups["line"].Value), int.Parse(m.Groups["col"].Value), m.Groups["msg"].Value))
            .ToList();
        var written = File.Exists(pex) && File.GetLastWriteTimeUtc(pex) > before;
        return new PapyrusResult(process.ExitCode == 0 && written && errors.Count == 0, text, errors, written ? pex : null);
    }
}
