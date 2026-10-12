using Hexa.NET.ImGui;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Assets;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// File → Pack into BSA (CK: Create Archive): the loose files next to the active file (its mod folder) —
/// meshes, textures, compiled scripts, sounds, interface, strings… — into "&lt;plugin&gt;.bsa", textures into
/// "&lt;plugin&gt; - Textures.bsa" (the game loads both with the plugin). Sources, configs and plugins stay out.
/// An archive that is already there is backed up first.
/// </summary>
internal sealed class ArchiveWindow
{
    /// <summary>Folders of Data whose files belong in an archive.</summary>
    private static readonly string[] AssetFolders = ["meshes", "textures", "scripts", "sound", "music", "interface", "strings", "lodsettings", "grass",
        "materials", "shadersfx", "facegen", "seq", "dialogueviews", "vis", "lsdata", "trees", "fonts"];

    private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".psc", ".esp", ".esm", ".esl", ".ini", ".txt", ".md", ".bak", ".tmp", ".7z", ".zip" };

    private bool _open;
    private bool _separateTextures = true;
    private Task<string>? _task;
    private volatile string _progress = "";
    private string? _last;

    public bool IsBusy => _task is not null;
    public string? LastResult => _last;

    public void Show()
    {
        _open = true;
        _listed = null;
    }

    /// <summary>The folder's files as last listed (listing a big mod folder every frame would be slow).</summary>
    private (string Plugin, DateTime At, string Root, Dictionary<string, string> Files)? _listed;

    /// <summary>The files of the plugin's folder that go into archives (archive path → file), and the folder.</summary>
    public static (string Root, Dictionary<string, string> Files) Collect(string pluginPath)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(pluginPath))!;
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in AssetFolders)
        {
            var dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).ToLowerInvariant();
                // Script sources (Scripts\Source, Source\Scripts) are for the compiler, not the game.
                if (SkippedExtensions.Contains(Path.GetExtension(file)) || relative.Contains(@"\source\", StringComparison.Ordinal)) continue;
                // SEQ files are read loose by the game.
                if (relative.StartsWith(@"seq\", StringComparison.Ordinal)) continue;
                files[relative] = file;
            }
        }
        return (root, files);
    }

    /// <summary>Packs; returns a summary. <paramref name="separateTextures"/>: textures into "&lt;plugin&gt; - Textures.bsa".</summary>
    public static string Pack(string pluginPath, bool separateTextures, IProgress<string>? progress = null)
    {
        var (root, files) = Collect(pluginPath);
        var name = Path.GetFileNameWithoutExtension(pluginPath);
        var textures = separateTextures ? files.Where(f => f.Key.StartsWith(@"textures\", StringComparison.Ordinal)).ToDictionary() : [];
        var main = files.Where(f => !textures.ContainsKey(f.Key)).ToDictionary();
        var parts = new List<string>();
        void One(string archive, Dictionary<string, string> content)
        {
            if (content.Count == 0) return;
            var path = Path.Combine(root, archive);
            if (File.Exists(path))
            {
                var backups = Path.Combine(AppPaths.BackupFolder, archive);
                Directory.CreateDirectory(backups);
                File.Copy(path, Path.Combine(backups, $"{DateTime.Now:yyyyMMdd-HHmmss}_{archive}"), overwrite: true);
            }
            var temp = path + ".tmp";
            var count = BsaWriter.Write(temp, content, progress);
            File.Move(temp, path, overwrite: true);
            parts.Add(TF($"{archive}: {count} file(s), {new FileInfo(path).Length / 1048576.0:0.0} MB"));
        }
        One(name + ".bsa", main);
        One(name + " - Textures.bsa", textures);
        return parts.Count == 0 ? T("Nothing to pack: no meshes, textures, scripts or sounds next to the plugin.") : string.Join("; ", parts);
    }

    public void Draw(EditorApp app)
    {
        if (_task is { IsCompleted: true } done)
        {
            _task = null;
            _last = done.IsCompletedSuccessfully ? done.Result : TF($"Packing failed: {done.Exception?.GetBaseException().Message}");
            Log.Info($"archive: {_last}");
        }
        if (!_open) return;
        ImGui.SetNextWindowSize(app.Scaled(620, 420), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Pack into BSA") + "###archive", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            if (app.ActivePlugin is not { } active)
            {
                ImGui.TextColored(Theme.Warning, T("Choose the active file first: its folder is packed."));
            }
            else
            {
                if (_listed is not { } listed || listed.Plugin != active.Path || (DateTime.UtcNow - listed.At).TotalSeconds > 3)
                {
                    var (r, f) = Collect(active.Path);
                    _listed = listed = (active.Path, DateTime.UtcNow, r, f);
                }
                var (root, files) = (listed.Root, listed.Files);
                ImGui.TextWrapped(TF($"Loose files next to {active.Name} ({root}):"));
                foreach (var group in files.GroupBy(f => f.Key.Split('\\')[0]).OrderBy(g => g.Key))
                    ImGui.BulletText($"{group.Key}: {group.Count()} ({group.Sum(f => new FileInfo(f.Value).Length) / 1048576.0:0.0} MB)");
                if (files.Count == 0) ImGui.TextColored(Theme.TextDim, T("No meshes, textures, compiled scripts, sounds or interface files there."));
                ImGui.Checkbox(T("Textures in their own archive (<plugin> - Textures.bsa)"), ref _separateTextures);
                ImGui.BeginDisabled(files.Count == 0 || _task is not null);
                if (ImGui.Button(_task is not null ? T("packing…") : T("Pack")))
                {
                    var path = active.Path;
                    var separate = _separateTextures;
                    var progress = new Progress<string>(m => _progress = m);
                    _task = Task.Run(() => Pack(path, separate, progress));
                }
                ImGui.EndDisabled();
                if (_task is not null) { ImGui.SameLine(); ImGui.TextColored(Theme.Warning, _progress); }
                if (_last is not null) ImGui.TextWrapped(_last);
                ImGui.TextColored(Theme.TextDim, T("Loose files stay where they are and win over the archive: move them away once the archive is checked. Script sources (.psc) and SEQ files are not packed."));
            }
        }
        ImGui.End();
    }
}
