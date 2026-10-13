using Mutagen.Bethesda.Plugins;
using StbEditor.Core;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Render;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Services;

/// <summary>
/// Export FaceGen (CK: Ctrl+F4): NPCs' head NIF and tint DDS written next to the active file, under
/// meshes\actors\character\facegendata\facegeom\&lt;plugin&gt;\ and textures\…\facetint\&lt;plugin&gt;\ (the NPC's defining
/// plugin, as the game looks for them). The NPC is taken as it is now, unsaved edits included. The new files win in
/// the editor's asset view at once and the render window reloads them.
/// </summary>
internal static class FaceGenExports
{
    public sealed record Outcome(int Exported, IReadOnlyList<string> Problems, IReadOnlyList<string> Files);

    public static Outcome Export(EditorApp app, IReadOnlyCollection<FormKey> npcs)
    {
        if (app.Session is not { } session) return new Outcome(0, [], []);
        if (app.ActivePlugin is not { } active)
        {
            app.ShowActiveFileDialog(T("FaceGen files go next to the active file. Choose it first."));
            return new Outcome(0, [], []);
        }

        var root = Path.GetDirectoryName(Path.GetFullPath(active.Path))!;
        var lookup = new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog);
        var problems = new List<string>();
        var files = new List<string>();
        var exported = 0;
        foreach (var key in npcs)
        {
            var npc = app.Document.Override(key)?.Record as INpcGetter ?? lookup.Resolve<INpcGetter>(key);
            if (npc is null) { problems.Add($"{key}: {T("not an NPC")}"); continue; }
            var race = lookup.Resolve<IRaceGetter>(npc.Race.FormKey);
            if (FaceGenExport.Unsupported(npc, race) is not null)
            {
                problems.Add($"{npc.EditorID}: " + (race is null ? T("race not found")
                    : !race.Flags.HasFlag(Race.Flag.FaceGenHead) ? TF($"race {race.EditorID} has no FaceGen head")
                    : T("takes its face from a template (Use Traits): no FaceGen of its own")));
                continue;
            }
            try
            {
                var built = FaceGenExport.Build(npc, lookup, session.Assets.ReadAllBytes);
                foreach (var (asset, bytes) in new[] { (FaceGenExport.NifPath(key), built.Nif), (FaceGenExport.TintPath(key), built.Tint) })
                {
                    var full = Path.Combine(root, asset);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllBytes(full, bytes);
                    session.Assets.AddLoose(asset, full, T("FaceGen export"));
                    files.Add(full);
                }
                foreach (var note in built.Notes) problems.Add($"{npc.EditorID}: {note}");
                exported++;
                Log.Info($"FaceGen: {npc.EditorID} -> {FaceGenExport.NifPath(key)} ({built.Parts.Count} parts: {string.Join(", ", built.Parts)})");
            }
            catch (Exception ex)
            {
                problems.Add($"{npc.EditorID}: {ex.Message}");
                Log.Error($"FaceGen {npc.EditorID}: {ex}");
            }
        }

        // The render window shows the new heads: forget the cached meshes and tints of these NPCs.
        var paths = npcs.SelectMany(k => new[] { FaceGenExport.NifPath(k), FaceGenExport.TintPath(k) }).ToList();
        app.RenderWindow.ReloadAssets(path => paths.Any(p => path.Contains(p, StringComparison.OrdinalIgnoreCase)));
        Log.Info($"FaceGen exported for {exported} NPC(s) into {root}" + (problems.Count > 0 ? $"; {problems.Count} note(s)" : ""));
        return new Outcome(exported, problems, files);
    }
}
