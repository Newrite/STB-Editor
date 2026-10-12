using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Data;

namespace StbEditor.Core;

/// <summary>What compacting a plugin would do.</summary>
public sealed record CompactPlan(int Own, int Moved, bool Fits, IReadOnlyDictionary<FormKey, FormKey> Map, IReadOnlyList<string> Dependents);

/// <summary>
/// Compact FormIDs for ESL and Convert to light (xEdit / CK): the plugin's own records get object IDs
/// 0x800–0xFFF (those already there keep theirs), every link inside the plugin follows, the light flag
/// can be set. FaceGen meshes / tints and voice files next to the plugin are renamed to the new IDs.
/// Plugins that have this one as a master point at the old IDs and must be fixed (they are listed).
/// </summary>
public static class FormIdCompactor
{
    public const uint First = 0x800, Last = 0xFFF;
    public const int Capacity = (int)(Last - First + 1);

    public static CompactPlan Plan(ISkyrimModGetter mod, ModKey self, IEnumerable<PluginInfo> loadOrder)
    {
        var own = mod.EnumerateMajorRecords().Select(r => r.FormKey).Where(k => k.ModKey == self).Distinct().OrderBy(k => k.ID).ToList();
        var kept = own.Where(k => k.ID is >= First and <= Last).Select(k => k.ID).ToHashSet();
        var map = new Dictionary<FormKey, FormKey>();
        var next = First;
        foreach (var key in own.Where(k => k.ID is < First or > Last))
        {
            while (kept.Contains(next)) next++;
            map[key] = new FormKey(self, next);
            kept.Add(next++);
        }
        var dependents = loadOrder.Where(p => p.Header?.Masters.Any(m => string.Equals(m, self.FileName, StringComparison.OrdinalIgnoreCase)) == true)
            .Select(p => p.Name).ToList();
        return new CompactPlan(own.Count, map.Count, own.Count <= Capacity, map, dependents);
    }

    /// <summary>Gives the mapped records their new keys (wherever they sit) and points every link at them.</summary>
    public static void Apply(SkyrimMod mod, IReadOnlyDictionary<FormKey, FormKey> map)
    {
        T Renew<T>(T record) where T : class, IMajorRecordGetter =>
            map.TryGetValue(record.FormKey, out var to) ? (T)(object)record.Duplicate(to) : record;

        void Children(Cell cell)
        {
            for (var i = 0; i < cell.Persistent.Count; i++) cell.Persistent[i] = (IPlaced)Renew((IMajorRecordGetter)cell.Persistent[i]);
            for (var i = 0; i < cell.Temporary.Count; i++) cell.Temporary[i] = (IPlaced)Renew((IMajorRecordGetter)cell.Temporary[i]);
            for (var i = 0; i < cell.NavigationMeshes.Count; i++) cell.NavigationMeshes[i] = Renew(cell.NavigationMeshes[i]);
            if (cell.Landscape is { } land) cell.Landscape = Renew(land);
        }

        foreach (var block in mod.Cells.Records)
            foreach (var sub in block.SubBlocks)
                for (var i = 0; i < sub.Cells.Count; i++)
                {
                    sub.Cells[i] = Renew(sub.Cells[i]);
                    Children(sub.Cells[i]);
                }

        var worlds = mod.Worldspaces.ToList();
        foreach (var original in worlds)
        {
            var world = Renew(original);
            if (world.TopCell is { } top)
            {
                world.TopCell = Renew(top);
                Children(world.TopCell);
            }
            foreach (var block in world.SubCells)
                foreach (var sub in block.Items)
                    for (var i = 0; i < sub.Items.Count; i++)
                    {
                        sub.Items[i] = Renew(sub.Items[i]);
                        Children(sub.Items[i]);
                    }
            if (!ReferenceEquals(world, original))
            {
                mod.Worldspaces.Remove(original.FormKey);
                mod.Worldspaces.Add(world);
            }
        }

        foreach (var original in mod.DialogTopics.ToList())
        {
            var topic = Renew(original);
            for (var i = 0; i < topic.Responses.Count; i++) topic.Responses[i] = Renew(topic.Responses[i]);
            if (!ReferenceEquals(topic, original))
            {
                mod.DialogTopics.Remove(original.FormKey);
                mod.DialogTopics.Add(topic);
            }
        }

        // Every other top-level group.
        foreach (var property in typeof(SkyrimMod).GetProperties().Where(p => typeof(IGroup).IsAssignableFrom(p.PropertyType)))
        {
            if (property.Name is nameof(SkyrimMod.Worldspaces) or nameof(SkyrimMod.DialogTopics) or nameof(SkyrimMod.Cells)) continue;
            if (property.GetValue(mod) is not IGroup group) continue;
            var moved = group.Records.Where(r => map.ContainsKey(r.FormKey)).ToList();
            foreach (var record in moved)
            {
                var renewed = (IMajorRecord)record.Duplicate(map[record.FormKey]);
                var remove = group.GetType().GetMethods().First(m => m.Name == "Remove" && m.GetParameters() is [{ ParameterType: var t }] && t == typeof(FormKey));
                remove.Invoke(group, [record.FormKey]);
                group.AddUntyped(renewed);
            }
        }

        mod.RemapLinks(map);
        var highest = mod.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == mod.ModKey).Select(r => r.FormKey.ID).DefaultIfEmpty(First - 1).Max();
        mod.ModHeader.Stats.NextFormID = Math.Max(highest + 1, First);
    }

    /// <summary>
    /// Loose FaceGen and voice files next to the plugin renamed to the new IDs (FaceGen: the NPC's ID; voice:
    /// "…_&lt;line ID&gt;_&lt;n&gt;.fuz/.wav/.xwm/.lip"). Returns how many; files inside archives cannot be renamed.
    /// </summary>
    public static int RenameAssets(string pluginPath, IReadOnlyDictionary<FormKey, FormKey> map)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(pluginPath))!;
        var name = Path.GetFileName(pluginPath);
        var ids = map.ToDictionary(kv => kv.Key.ID.ToString("X8"), kv => kv.Value.ID.ToString("X8"), StringComparer.OrdinalIgnoreCase);
        var renamed = 0;
        foreach (var folder in new[] { Path.Combine(root, "meshes", "actors", "character", "facegendata", "facegeom", name),
                     Path.Combine(root, "textures", "actors", "character", "facegendata", "facetint", name) })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.GetFiles(folder))
            {
                if (!ids.TryGetValue(Path.GetFileNameWithoutExtension(file), out var to)) continue;
                File.Move(file, Path.Combine(folder, to + Path.GetExtension(file)));
                renamed++;
            }
        }
        var voice = Path.Combine(root, "sound", "voice", name);
        if (Directory.Exists(voice))
        {
            var pattern = new Regex(@"_([0-9A-Fa-f]{8})_(\d+)$");
            foreach (var file in Directory.GetFiles(voice, "*", SearchOption.AllDirectories))
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                var m = pattern.Match(stem);
                if (!m.Success || !ids.TryGetValue(m.Groups[1].Value, out var to)) continue;
                var target = Path.Combine(Path.GetDirectoryName(file)!, stem[..m.Index] + "_" + to + "_" + m.Groups[2].Value + Path.GetExtension(file));
                File.Move(file, target);
                renamed++;
            }
        }
        return renamed;
    }
}
