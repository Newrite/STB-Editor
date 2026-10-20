using Mutagen.Bethesda.Plugins;
using StbEditor.Core;
using StbEditor.Data;
using StbEditor.Data.Catalog;
using StbEditor.Render;

namespace StbEditor.Cli;

/// <summary>
/// landseamfix &lt;game&gt; &lt;worldspace&gt; &lt;cornerX&gt; &lt;cornerY&gt; &lt;out plugin path&gt;: a patch for a texture seam around a cell corner
/// (grid units: corner 28 8 = world 114688, 32768). Along the four edges shared by the 2x2 cells around it, every vertex where the
/// quadrants meeting there mix their textures differently gets the mix of one of them: the others' existing layer opacities are
/// re-solved (layers are composited base first, each layer over the result) so they come out the same. Only those edge vertices
/// change — heights, normals, colours and the layer lists stay — so the seam becomes a 128-unit blend. LAND copies come from the
/// current winners; the result is read back and checked.
/// </summary>
internal static class LandSeamFix
{
    private const float Tolerance = 0.03f;

    public static void Run(Workspace ws, GameInstall game, string[] args)
    {
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var builder = new SceneBuilder(ws, catalog);
        var world = catalog.BySignature["WRLD"].First(w => string.Equals(w.EditorId, args[2], StringComparison.OrdinalIgnoreCase));
        int cx = int.Parse(args[3]), cy = int.Parse(args[4]);
        var outPath = Path.GetFullPath(args[5]);
        string Diffuse(FormKey? key) => Path.GetFileNameWithoutExtension(builder.LandTexture(key));

        var cells = new Dictionary<(int X, int Y), (FormKey Cell, Mutagen.Bethesda.Skyrim.ILandscapeGetter Land, LandscapeData Data)>();
        for (var gx = cx - 1; gx <= cx; gx++)
            for (var gy = cy - 1; gy <= cy; gy++)
            {
                var record = catalog.BySignature["CELL"].First(c => c.Winner.Cell is { IsInterior: false } p && p.Worldspace == world.FormKey && p.GridX == gx && p.GridY == gy);
                var (plugin, version) = lookup.CellVersionsByPlugin(record.FormKey).Last(v => v.Cell.Landscape is not null);
                Console.WriteLine($"cell {gx},{gy}: LAND {version.Landscape!.FormKey} from {plugin.Name}");
                cells[(gx, gy)] = (record.FormKey, version.Landscape!, LandscapeData.From(version.Landscape!));
            }

        // Every vertex on the shared edges, as (cell, column, row) on each side.
        var points = new Dictionary<(int, int), List<((int, int) Cell, int Col, int Row)>>();
        void Add(int wx, int wy)
        {
            // World vertex index (wx, wy) in 128-unit steps from the corner's cell (cx - 1, cy - 1) origin.
            var list = new List<((int, int), int, int)>();
            foreach (var key in cells.Keys)
            {
                var col = wx - (key.X - (cx - 1)) * 32;
                var row = wy - (key.Y - (cy - 1)) * 32;
                if (col is >= 0 and <= 32 && row is >= 0 and <= 32) list.Add((key, col, row));
            }
            if (list.Count > 1) points[(wx, wy)] = list;
        }
        for (var i = 0; i <= 64; i++) { Add(32, i); Add(i, 32); }

        int fixedVertices = 0, skipped = 0;
        foreach (var (at, sides) in points.OrderBy(p => p.Key))
        {
            var instances = sides.SelectMany(s => QuadrantsOf(s.Col, s.Row).Select(q => (s.Cell, q, s.Col, s.Row))).ToList();
            var mixes = instances.Select(i => Mix(cells[i.Cell].Data, i.q, i.Col, i.Row)).ToList();
            var spread = mixes.SelectMany(a => mixes.Select(b => Distance(a, b))).Max();
            if (spread <= Tolerance) continue;

            // Try each instance's mix as the target; keep the one every other instance can reach with the least change.
            (int Target, float Change, List<float[]?> Solutions)? best = null;
            for (var t = 0; t < instances.Count; t++)
            {
                var solutions = new List<float[]?>();
                var change = 0f;
                var ok = true;
                for (var k = 0; k < instances.Count && ok; k++)
                {
                    if (Distance(mixes[k], mixes[t]) <= Tolerance) { solutions.Add(null); continue; }
                    var i = instances[k];
                    var solved = Solve(cells[i.Cell].Data.Quadrants[i.q], Local(i.q, i.Col, i.Row), mixes[t]);
                    if (solved is null) { ok = false; break; }
                    var quadrant = cells[i.Cell].Data.Quadrants[i.q];
                    change += solved.Select((o, n) => Math.Abs(o - quadrant.Layers[n].Opacity[Local(i.q, i.Col, i.Row)])).Sum();
                    solutions.Add(solved);
                }
                if (ok && (best is null || change < best.Value.Change)) best = (t, change, solutions);
            }
            var where = $"vertex {at.Item1},{at.Item2} (world {(cx - 1) * 4096 + at.Item1 * 128},{(cy - 1) * 4096 + at.Item2 * 128})";
            if (best is not { } b)
            {
                skipped++;
                Console.WriteLine($"  {where}: spread {spread:0.00}, no common mix reachable — left as is: {string.Join("  |  ", mixes.Select(Show))}");
                continue;
            }
            for (var k = 0; k < instances.Count; k++)
            {
                if (b.Solutions[k] is not { } solved) continue;
                var i = instances[k];
                var quadrant = cells[i.Cell].Data.Quadrants[i.q];
                for (var n = 0; n < solved.Length; n++) quadrant.Layers[n].Opacity[Local(i.q, i.Col, i.Row)] = solved[n];
            }
            fixedVertices++;
            Console.WriteLine($"  {where}: spread {spread:0.00} -> all as {instances[b.Target].Cell} q{instances[b.Target].q}: {Show(mixes[b.Target])}");
        }
        Console.WriteLine($"{fixedVertices} edge vertices evened out, {skipped} left");

        // Check: every shared vertex now mixes the same on all sides.
        var worst = points.Values.Max(sides =>
        {
            var m = sides.SelectMany(s => QuadrantsOf(s.Col, s.Row).Select(q => Mix(cells[s.Cell].Data, q, s.Col, s.Row))).ToList();
            return m.SelectMany(a => m.Select(c => Distance(a, c))).Max();
        });
        Console.WriteLine($"largest remaining difference on the edges: {worst:0.000}");

        var active = ActivePlugin.ForNew(outPath, light: true, master: false, game, ws.DefaultEncoding);
        var doc = new EditDocument();
        foreach (var ((gx, gy), c) in cells)
        {
            // Only cells whose data changed go in.
            if (Same(LandscapeData.From(c.Land), c.Data)) continue;
            doc.AddDraft("seam", new LandDraft(c.Land.FormKey, c.Cell, world.FormKey, gx, gy, c.Data, c.Land));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var writer = new PluginWriter(ws, catalog);
        var result = writer.Prepare(active, doc);
        Console.WriteLine($"prepare: success={result.Success} error={result.Error}; overridden {result.Overridden}, lossy {result.LossyRecords.Count}");
        foreach (var w in result.Warnings) Console.WriteLine("  warn " + w);
        if (!result.Success) return;
        File.Move(result.TempPath!, outPath, overwrite: true);

        // Read back: the written LAND must give the new data (layers, heights, normals of the source otherwise).
        using var check = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(outPath, game.Release);
        Console.WriteLine($"written {outPath}: masters {string.Join(", ", check.ModHeader.MasterReferences.Select(m => m.Master.FileName))}");
        var lands = check.Worldspaces.SelectMany(w => w.SubCells).SelectMany(b => b.Items).SelectMany(sb => sb.Items).Where(c => c.Landscape is not null).ToList();
        foreach (var cell in lands)
        {
            var source = cells.Values.First(c => c.Cell == cell.FormKey);
            var back = LandscapeData.From(cell.Landscape!);
            var heights = back.Heights.Zip(source.Data.Heights).Max(p => Math.Abs(p.First - p.Second));
            var normals = back.Normals.Zip(source.Data.Normals).Max(p => (p.First - p.Second).Length());
            var layers = Enumerable.Range(0, 4).Max(q => back.Quadrants[q].Layers.Zip(source.Data.Quadrants[q].Layers).SelectMany(p => p.First.Opacity.Zip(p.Second.Opacity)).Select(p => Math.Abs(p.First - p.Second)).DefaultIfEmpty(0).Max());
            var counts = Enumerable.Range(0, 4).All(q => back.Quadrants[q].Layers.Count == source.Data.Quadrants[q].Layers.Count && back.Quadrants[q].BaseTexture == source.Data.Quadrants[q].BaseTexture);
            Console.WriteLine($"  read back {cell.FormKey} grid {cell.Grid?.Point}: height diff {heights}, normal diff {normals:0.000}, opacity diff {layers:0.000} (8-bit steps), same layers {counts}, flags {cell.Landscape!.Flags}");
        }

        IEnumerable<int> QuadrantsOf(int col, int row)
        {
            foreach (var qx in new[] { 0, 1 }.Where(q => q == 0 ? col <= 16 : col >= 16))
                foreach (var qy in new[] { 0, 1 }.Where(q => q == 0 ? row <= 16 : row >= 16))
                    yield return qy * 2 + qx;
        }

        static int Local(int q, int col, int row) => (row - (q >> 1) * 16) * 17 + (col - (q & 1) * 16);

        Dictionary<string, float> Mix(LandscapeData d, int q, int col, int row)
        {
            var quadrant = d.Quadrants[q];
            var local = Local(q, col, row);
            var w = new Dictionary<string, float> { [Diffuse(quadrant.BaseTexture)] = 1 };
            foreach (var layer in quadrant.Layers)
            {
                var o = layer.Opacity[local];
                foreach (var k in w.Keys.ToList()) w[k] *= 1 - o;
                w[Diffuse(layer.Texture)] = w.GetValueOrDefault(Diffuse(layer.Texture)) + o;
            }
            return w;
        }

        // Opacities of the quadrant's layers that give the target mix: each texture of the target on one layer (the topmost of that
        // texture) or on the base, the rest 0; solved top-down with what is left. Null when a target texture is missing.
        float[]? Solve(LandQuadrant quadrant, int local, Dictionary<string, float> target)
        {
            var needed = target.Where(p => p.Value > 0.005f).ToDictionary(p => p.Key, p => p.Value);
            var baseTexture = Diffuse(quadrant.BaseTexture);
            var owner = new Dictionary<string, int>();
            for (var n = quadrant.Layers.Count - 1; n >= 0; n--)
            {
                var t = Diffuse(quadrant.Layers[n].Texture);
                if (needed.ContainsKey(t) && t != baseTexture && !owner.ContainsKey(t)) owner[t] = n;
            }
            if (needed.Keys.Any(t => t != baseTexture && !owner.ContainsKey(t))) return null;
            var opacity = new float[quadrant.Layers.Count];
            var left = 1f;
            for (var n = quadrant.Layers.Count - 1; n >= 0; n--)
            {
                var t = Diffuse(quadrant.Layers[n].Texture);
                if (owner.TryGetValue(t, out var o) && o == n)
                {
                    opacity[n] = left <= 1e-4f ? 0 : Math.Clamp(needed[t] / left, 0, 1);
                    left -= needed[t];
                }
            }
            // Whatever is left falls to the base; with no base share in the target, the lowest used layer covers it fully.
            if (!needed.ContainsKey(baseTexture) && owner.Count > 0) opacity[owner.Values.Min()] = 1;
            return opacity;
        }

        static float Distance(Dictionary<string, float> a, Dictionary<string, float> b) =>
            a.Keys.Union(b.Keys).Max(k => Math.Abs(a.GetValueOrDefault(k) - b.GetValueOrDefault(k)));

        static string Show(Dictionary<string, float> w) => string.Join(" ", w.Where(p => p.Value > 0.02f).OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value:0.00}"));

        static bool Same(LandscapeData a, LandscapeData b) => Enumerable.Range(0, 4).All(q =>
            a.Quadrants[q].Layers.Zip(b.Quadrants[q].Layers).All(p => p.First.Opacity.SequenceEqual(p.Second.Opacity)));
    }
}
