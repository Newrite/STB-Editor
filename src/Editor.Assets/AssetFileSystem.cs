using System.Collections.Concurrent;
using Mutagen.Bethesda.Archives;
using StbEditor.Data;

namespace StbEditor.Assets;

/// <summary>Where an asset path resolves to.</summary>
/// <param name="Container">MO2 mod name, "Data" or "overwrite" for loose files; archive file name otherwise.</param>
public sealed record AssetSource(string Path, string Container, string? ArchivePath, string? LoosePath)
{
    public bool InArchive => ArchivePath is not null;
    public override string ToString() => InArchive ? $"{Container} (BSA)" : Container;
}

/// <summary>
/// The game's view of Data as the engine would see it under MO2: loose files by mod priority
/// override every archive; archives load in INI order, then by plugin in load order, and a later
/// archive overrides an earlier one.
/// </summary>
public sealed class AssetFileSystem
{
    private sealed record ArchiveEntry(IArchiveFile File, string ArchivePath, string ArchiveName, object Gate);

    private Dictionary<string, (string FullPath, string Container)> _loose;
    private readonly Dictionary<string, ArchiveEntry> _archived;

    public IReadOnlyList<string> LoadedArchives { get; }
    public IReadOnlyList<string> Errors { get; }
    public int LooseCount => _loose.Count;
    public int ArchivedCount => _archived.Count;

    private AssetFileSystem(
        Dictionary<string, (string, string)> loose,
        Dictionary<string, ArchiveEntry> archived,
        List<string> archives,
        List<string> errors)
    {
        _loose = loose;
        _archived = archived;
        LoadedArchives = archives;
        Errors = errors;
    }

    public static AssetFileSystem Build(Workspace workspace, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var game = workspace.Game;
        var errors = new ConcurrentBag<string>();

        progress?.Report("Scanning loose files");
        var sources = new List<(string Folder, string Name)> { (game.DataFolder, "Data") };
        if (game.Mo2 is { } mo2)
        {
            foreach (var folder in mo2.EnabledModFolders())
            {
                var name = folder.Equals(mo2.OverwriteFolder, StringComparison.OrdinalIgnoreCase) ? "overwrite" : Path.GetFileName(folder);
                sources.Add((folder, name));
            }
        }

        // Scan in parallel, merge in priority order (lowest first, so higher priority overwrites).
        var scanned = new List<(string Relative, string Full)>[sources.Count];
        Parallel.For(0, sources.Count, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            scanned[i] = ScanFolder(sources[i].Folder, errors);
        });

        var loose = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < sources.Count; i++)
            foreach (var (relative, full) in scanned[i])
                loose[Normalize(relative)] = (full, sources[i].Name);

        progress?.Report("Reading archives");
        var archiveNames = ArchiveOrder(workspace);
        var archived = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var loaded = new List<string>();

        var readers = new (string Name, string Path, IArchiveFile[]? Files)[archiveNames.Count];
        Parallel.For(0, archiveNames.Count, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            var name = archiveNames[i];
            if (!loose.TryGetValue(name, out var found))
            {
                readers[i] = (name, string.Empty, null);
                return;
            }

            try
            {
                var reader = Archive.CreateReader(game.GameRelease, found.Item1);
                readers[i] = (name, found.Item1, reader.Files.ToArray());
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {ex.Message}");
                readers[i] = (name, found.Item1, null);
            }
        });

        foreach (var (name, path, files) in readers)
        {
            if (files is null) continue;

            loaded.Add(name);
            var gate = new object();
            foreach (var file in files)
                archived[Normalize(file.Path)] = new ArchiveEntry(file, path, name, gate);
        }

        return new AssetFileSystem(loose, archived, loaded, [.. errors.Order()]);
    }

    private static List<(string, string)> ScanFolder(string folder, ConcurrentBag<string> errors)
    {
        var result = new List<(string, string)>();
        if (!Directory.Exists(folder)) return result;

        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
            foreach (var file in Directory.EnumerateFiles(folder, "*", options))
            {
                var relative = Path.GetRelativePath(folder, file);
                // MO2 bookkeeping and plugins are not assets.
                if (relative.Equals("meta.ini", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add((relative, file));
            }
        }
        catch (Exception ex)
        {
            errors.Add($"{folder}: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Archive names in load order. INI lists first (profile INI, falling back to
    /// Skyrim_Default.ini), then "&lt;plugin&gt;.bsa" and "&lt;plugin&gt; - Textures.bsa" per plugin.
    /// </summary>
    private static List<string> ArchiveOrder(Workspace workspace)
    {
        var game = workspace.Game;
        var names = new List<string>();

        void Add(string name)
        {
            name = name.Trim();
            if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }

        var defaultIni = Path.Combine(game.GameFolder, game.IsVr ? "SkyrimVR_Default.ini" : "Skyrim_Default.ini");
        foreach (var key in new[] { "sResourceArchiveList", "sResourceArchiveList2", "sResourceArchiveListBeta" })
        {
            var value = TextFiles.ReadIniValue(game.SkyrimIni, key) ?? TextFiles.ReadIniValue(defaultIni, key);
            if (value is null) continue;
            foreach (var name in value.Split(',')) Add(name);
        }

        foreach (var plugin in workspace.Active)
        {
            var stem = Path.GetFileNameWithoutExtension(plugin.Name);
            Add(stem + ".bsa");
            Add(stem + " - Textures.bsa");
        }

        return names;
    }

    /// <summary>"Meshes/Foo.NIF" → "meshes\foo.nif"; strips a leading "Data\".</summary>
    public static string Normalize(string path)
    {
        var p = path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
        if (p.StartsWith("data\\", StringComparison.Ordinal)) p = p[5..];
        return p;
    }

    /// <summary>Model paths in records are relative to Meshes\.</summary>
    public static string MeshPath(string recordPath) => Rooted(recordPath, "meshes\\");

    /// <summary>
    /// Texture paths in NIFs and TXST are relative to Data\ or to Textures\ — or carry the mod
    /// author's build path ("skyrimhd\build\pc\data\textures\plants\x.dds"), which the engine
    /// cuts at "data\" / "textures\". Do the same.
    /// </summary>
    public static string TexturePath(string nifPath) => Rooted(nifPath, "textures\\");

    private static string Rooted(string path, string root)
    {
        var p = Normalize(path);
        if (p.StartsWith(root, StringComparison.Ordinal)) return p;

        var data = p.LastIndexOf("data\\" + root, StringComparison.Ordinal);
        if (data >= 0) return p[(data + 5)..];

        var inner = p.IndexOf("\\" + root, StringComparison.Ordinal);
        if (inner >= 0) return p[(inner + 1)..];

        // Absolute paths ("c:\…") with no recognisable root cannot resolve; keep them distinct.
        return root + p;
    }

    /// <summary>Files that exist only in memory (previews: a face being edited); they win over everything.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _memory = new(StringComparer.OrdinalIgnoreCase);

    public void AddMemory(string path, byte[] bytes) => _memory[Normalize(path)] = bytes;

    public void RemoveMemory(string path) => _memory.TryRemove(Normalize(path), out _);

    public AssetSource? Find(string path)
    {
        var key = Normalize(path);
        if (_memory.ContainsKey(key)) return new AssetSource(key, "memory", null, null);

        if (_loose.TryGetValue(key, out var loose))
            return new AssetSource(key, loose.Container, null, loose.FullPath);

        if (_archived.TryGetValue(key, out var entry))
            return new AssetSource(key, entry.ArchiveName, entry.ArchivePath, null);

        return null;
    }

    /// <summary>
    /// A loose file made during the session (a compiled script): it wins over what was there. The map is
    /// replaced, not changed, so lookups on worker threads never see it half-updated.
    /// </summary>
    public void AddLoose(string path, string fullPath, string container)
    {
        var copy = new Dictionary<string, (string FullPath, string Container)>(_loose, _loose.Comparer) { [Normalize(path)] = (fullPath, container) };
        _loose = copy;
    }

    public bool Exists(string path)
    {
        var key = Normalize(path);
        return _memory.ContainsKey(key) || _loose.ContainsKey(key) || _archived.ContainsKey(key);
    }

    public byte[]? ReadAllBytes(string path)
    {
        var key = Normalize(path);
        if (_memory.TryGetValue(key, out var held)) return held;

        if (_loose.TryGetValue(key, out var loose))
            return File.ReadAllBytes(loose.FullPath);

        if (_archived.TryGetValue(key, out var entry))
        {
            // Archive readers share one file stream; reads from worker threads must not interleave.
            lock (entry.Gate) return entry.File.GetBytes();
        }

        return null;
    }

    /// <summary>The archived copy of an asset, ignoring loose files that replace it (null when no archive has it).</summary>
    public byte[]? ReadArchived(string path)
    {
        if (!_archived.TryGetValue(Normalize(path), out var entry)) return null;
        lock (entry.Gate) return entry.File.GetBytes();
    }

    /// <summary>All asset paths under a folder prefix with an extension (e.g. "meshes\", ".nif").</summary>
    public IEnumerable<string> Enumerate(string prefix, string extension)
    {
        prefix = Normalize(prefix);
        return _loose.Keys.Concat(_archived.Keys)
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
