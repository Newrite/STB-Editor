using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Records;
using StbEditor.Assets;
using StbEditor.Data;
using StbEditor.Nif;

namespace StbEditor.App.Services;

/// <summary>
/// The files a plugin uses (xEdit: Assets; CK: the list Create Archive packs): every path its records name — models,
/// textures, sounds, scripts, … — and the textures inside the meshes it names, with where the build has each one (a loose
/// file of a mod, an archive) or that it is missing.
/// </summary>
internal static class PluginAssets
{
    public sealed record Asset(string Path, string Kind, AssetSource? Source, IReadOnlyList<string> UsedBy)
    {
        public bool Missing => Source is null;
    }

    public static List<Asset> List(PluginInfo plugin, AssetFileSystem assets, IProgress<string>? progress, CancellationToken cancel)
    {
        if (plugin.Mod is not { } mod) return [];
        var used = new Dictionary<string, (string Kind, List<string> By)>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string kind, string by)
        {
            if (!used.TryGetValue(path, out var entry)) used[path] = entry = (kind, []);
            if (entry.By.Count < 20 && !entry.By.Contains(by)) entry.By.Add(by);
        }
        var count = 0;
        foreach (var record in mod.EnumerateMajorRecords())
        {
            cancel.ThrowIfCancellationRequested();
            if (++count % 2000 == 0) progress?.Report($"{count:N0} records…");
            if (record is not IAssetLinkContainerGetter container) continue;
            var by = record.EditorID ?? record.FormKey.ToString();
            IEnumerable<IAssetLinkGetter> links;
            try { links = container.EnumerateListedAssetLinks().ToList(); }
            catch { continue; }
            foreach (var link in links)
            {
                if (link.IsNull) continue;
                var path = link.DataRelativePath.Path;
                if (string.IsNullOrWhiteSpace(path)) continue;
                Add(AssetFileSystem.Normalize(path), link.Type.GetType().Name.Replace("Skyrim", "").Replace("AssetType", ""), by);
            }
        }

        // Textures the meshes ask for (the game loads them with the mesh).
        var meshes = used.Where(u => u.Key.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)).Select(u => (u.Key, u.Value.By.FirstOrDefault() ?? "")).ToList();
        var done = 0;
        foreach (var (mesh, by) in meshes)
        {
            cancel.ThrowIfCancellationRequested();
            if (++done % 50 == 0) progress?.Report($"textures of {done:N0} / {meshes.Count:N0} meshes…");
            byte[]? bytes;
            try { bytes = assets.ReadAllBytes(mesh); } catch { continue; }
            if (bytes is null) continue;
            NifModel model;
            try { model = NifFile.Load(bytes, mesh, raw: true); } catch { continue; }
            foreach (var shape in model.Shapes)
                foreach (var texture in shape.Material.Textures)
                    if (!string.IsNullOrWhiteSpace(texture))
                        Add(AssetFileSystem.Normalize(AssetFileSystem.TexturePath(texture)), "Texture (in mesh)", $"{System.IO.Path.GetFileName(mesh)} ({by})");
        }

        return used.Select(u => new Asset(u.Key, u.Value.Kind, assets.Find(u.Key), u.Value.By))
            .OrderBy(a => a.Missing ? 0 : 1).ThenBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
