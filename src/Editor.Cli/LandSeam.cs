using System.Numerics;
using Mutagen.Bethesda.Plugins;
using StbEditor.Core;
using StbEditor.Data;
using StbEditor.Data.Catalog;

namespace StbEditor.Cli;

/// <summary>
/// landseam &lt;game&gt; &lt;worldspace&gt; &lt;x&gt; &lt;y&gt;: a visible landscape seam near a world point — the 2x2 cells around the nearest cell
/// corner, which plugins carry each cell's LAND, and along each shared edge how far heights, normals, vertex colours and
/// texture layers of the two sides disagree.
/// </summary>
internal static class LandSeam
{
    public static void Run(Workspace ws, string[] args)
    {
        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "STBEditor", "cache", "catalog");
        var catalog = RecordCatalog.Build(ws, cacheDir);
        var lookup = new RecordLookup(ws, catalog);
        var world = catalog.BySignature["WRLD"].First(w => string.Equals(w.EditorId, args[2], StringComparison.OrdinalIgnoreCase));
        var px = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
        var py = float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
        // The corner nearest to the point: cells (cx-1..cx, cy-1..cy).
        var cx = (int)MathF.Round(px / LandscapeData.CellSize);
        var cy = (int)MathF.Round(py / LandscapeData.CellSize);
        var inX = px - MathF.Floor(px / LandscapeData.CellSize) * LandscapeData.CellSize;
        var inY = py - MathF.Floor(py / LandscapeData.CellSize) * LandscapeData.CellSize;
        Console.WriteLine($"point {px:0},{py:0} is in cell {MathF.Floor(px / 4096):0},{MathF.Floor(py / 4096):0} at {inX:0},{inY:0} of 4096; corner {cx},{cy}");

        var textures = catalog.BySignature.TryGetValue("LTEX", out var ltex) ? ltex.ToDictionary(r => r.FormKey, r => r.EditorId ?? r.FormKey.ToString()) : [];
        string Water(FormKey? key) => key is null ? "-" : catalog.ByKey.TryGetValue(key.Value, out var w)
            ? $"{w.EditorId} ({ws.FormIdText(key.Value)}, in {string.Join(" > ", w.Plugins.Select(p => p.Name))})" : key.Value.ToString();
        string Tex(FormKey? key) => key is null ? "(default)" : textures.TryGetValue(key.Value, out var id) ? id : key.Value.ToString();

        var builder = new StbEditor.Render.SceneBuilder(ws, catalog);
        string Diffuse(FormKey? key) => Path.GetFileNameWithoutExtension(builder.LandTexture(key));
        var cells = new Dictionary<(int, int), LandscapeData?>();
        for (var gx = cx - 1; gx <= cx; gx++)
            for (var gy = cy - 1; gy <= cy; gy++)
            {
                var record = catalog.BySignature["CELL"].FirstOrDefault(c => c.Winner.Cell is { IsInterior: false } p && p.Worldspace == world.FormKey && p.GridX == gx && p.GridY == gy);
                if (record is null) { Console.WriteLine($"cell {gx},{gy}: none"); cells[(gx, gy)] = null; continue; }
                var versions = lookup.CellVersionsByPlugin(record.FormKey);
                var withLand = versions.Where(v => v.Cell.Landscape is not null).ToList();
                Console.WriteLine($"cell {gx},{gy} {ws.FormIdText(record.FormKey)} {record.EditorId}: LAND in {string.Join(" > ", withLand.Select(v => v.Plugin.Name + Sum(v.Cell.Landscape!)))}");
                Console.WriteLine($"    water: {string.Join(" > ", versions.Select(v => $"{v.Plugin.Name} {(v.Cell.Flags.HasFlag(Mutagen.Bethesda.Skyrim.Cell.Flag.HasWater) ? "has water" : "no water")} h {(v.Cell.WaterHeight is { } wh ? wh.ToString("0.#") : "-")} type {Water(v.Cell.Water.FormKeyNullable)}"))}");
                var land = withLand.LastOrDefault().Cell?.Landscape;
                var data = land is null ? null : LandscapeData.From(land);
                cells[(gx, gy)] = data;
                if (data is null) continue;
                for (var q = 0; q < 4; q++)
                {
                    var quadrant = data.Quadrants[q];
                    Console.WriteLine($"    quadrant {q}: base {Tex(quadrant.BaseTexture)} ({Diffuse(quadrant.BaseTexture)}); layers {string.Join(", ", quadrant.Layers.Select(l => $"{Tex(l.Texture)} ({Diffuse(l.Texture)}) max {l.Opacity.Max():0.00}"))}");
                }
            }

        // Shared edges: right neighbour (col 32 | col 0) and upper neighbour (row 32 | row 0).
        foreach (var ((gx, gy), a) in cells)
        {
            if (a is null) continue;
            if (cells.TryGetValue((gx + 1, gy), out var right) && right is not null)
                Compare($"{gx},{gy} | {gx + 1},{gy} (east edge)", a, right, i => i * 33 + 32, i => i * 33);
            if (cells.TryGetValue((gx, gy + 1), out var up) && up is not null)
                Compare($"{gx},{gy} | {gx},{gy + 1} (north edge)", a, up, i => 32 * 33 + i, i => i);
        }

        foreach (var ((gx, gy), d) in cells) if (d is not null) Inner($"{gx},{gy}", d);
        // --dump: the north edge of the point's cell, vertex by vertex — raw layers on both sides.
        if (args.Contains("--dump") && cells.TryGetValue(((int)MathF.Floor(px / 4096), (int)MathF.Floor(py / 4096)), out var south) && south is not null
            && cells.TryGetValue(((int)MathF.Floor(px / 4096), (int)MathF.Floor(py / 4096) + 1), out var north) && north is not null)
        {
            Console.WriteLine("north edge of the point's cell, by column (south side | north side):");
            for (var col = 0; col < 33; col++)
            {
                string Side(LandscapeData d, int row) => string.Join(" / ", QuadrantsOf(col, row).Select(q =>
                    $"q{q} base {Diffuse(d.Quadrants[q].BaseTexture)}: " + string.Join(" ", d.Quadrants[q].Layers.Select((l, n) => (l, n))
                        .Where(t => t.l.Opacity[(row - (q >> 1) * 16) * 17 + (col - (q & 1) * 16)] > 0.01f)
                        .Select(t => $"{t.n}:{Diffuse(t.l.Texture)} {t.l.Opacity[(row - (q >> 1) * 16) * 17 + (col - (q & 1) * 16)]:0.00}"))));
                Console.WriteLine($"  col {col,2} (x {MathF.Floor(px / 4096) * 4096 + col * 128:0}): {Side(south, 32)}  ||  {Side(north, 0)}");
            }
        }
        // Each LAND version's normals against the winners next to it: which plugin's normals stop matching the neighbours.
        Console.WriteLine("normals of every LAND version against the neighbours' winners (max degrees on each shared edge):");
        foreach (var ((gx, gy), _) in cells)
        {
            var record = catalog.BySignature["CELL"].FirstOrDefault(c => c.Winner.Cell is { IsInterior: false } p && p.Worldspace == world.FormKey && p.GridX == gx && p.GridY == gy);
            if (record is null) continue;
            foreach (var (plugin, version) in lookup.CellVersionsByPlugin(record.FormKey).Where(v => v.Cell.Landscape is not null))
            {
                var d = LandscapeData.From(version.Landscape!);
                var parts = new List<string>();
                void Edge(string side, int nx, int ny, Func<int, int> mine, Func<int, int> theirs)
                {
                    if (!cells.TryGetValue((nx, ny), out var n) || n is null) return;
                    var worst = Enumerable.Range(0, 33).Max(i => MathF.Acos(Math.Clamp(Vector3.Dot(d.Normals[mine(i)], n.Normals[theirs(i)]), -1, 1)) * 180 / MathF.PI);
                    parts.Add($"{side} {worst:0.0}");
                }
                Edge("E", gx + 1, gy, i => i * 33 + 32, i => i * 33);
                Edge("W", gx - 1, gy, i => i * 33, i => i * 33 + 32);
                Edge("N", gx, gy + 1, i => 32 * 33 + i, i => i);
                Edge("S", gx, gy - 1, i => i, i => 32 * 33 + i);
                Console.WriteLine($"  {gx},{gy} {plugin.Name}: {string.Join(", ", parts)}");
            }
        }
        if (cells.TryGetValue(((int)MathF.Floor(px / 4096), (int)MathF.Floor(py / 4096)), out var here) && here is not null)
        {
            var fx = inX / LandscapeData.Spacing;
            var fy = inY / LandscapeData.Spacing;
            Console.WriteLine($"terrain at the point: vertex heights around {string.Join(", ", new[] { (0, 0), (1, 0), (0, 1), (1, 1) }.Select(d => here.Heights[Math.Min(32, (int)fy + d.Item2) * 33 + Math.Min(32, (int)fx + d.Item1)].ToString("0")))} (vertex {fx:0.0},{fy:0.0})");
        }
        // What stands on the ground near the point: flat meshes over the landscape show as seams too.
        var scene = builder.BuildExterior(world.FormKey, (int)MathF.Floor(px / 4096), (int)MathF.Floor(py / 4096), 1);
        var reach = args.Length > 5 ? float.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 1200;
        var point = new Vector2(px, py);
        Console.WriteLine($"objects within {reach:0} units (2D):");
        foreach (var o in scene.Objects.Where(o => Vector2.Distance(new Vector2(o.Position.X, o.Position.Y), point) < reach + o.BoundsRadius)
                     .OrderBy(o => Vector2.Distance(new Vector2(o.Position.X, o.Position.Y), point)))
            Console.WriteLine($"  {Vector2.Distance(new Vector2(o.Position.X, o.Position.Y), point),5:0} {o.BaseSignature} {o.BaseEditorId} at {o.Position.X:0},{o.Position.Y:0},{o.Position.Z:0} r {o.BoundsRadius:0} scale {o.Scale:0.##} {o.ModelPath} [{o.SourcePlugin}]");

        void Compare(string name, LandscapeData a, LandscapeData b, Func<int, int> ia, Func<int, int> ib)
        {
            float height = 0, normal = 0, colour = 0;
            var worstColour = (A: 0u, B: 0u, At: 0);
            for (var i = 0; i < 33; i++)
            {
                height = Math.Max(height, Math.Abs(a.Heights[ia(i)] - b.Heights[ib(i)]));
                normal = Math.Max(normal, MathF.Acos(Math.Clamp(Vector3.Dot(a.Normals[ia(i)], b.Normals[ib(i)]), -1, 1)) * 180 / MathF.PI);
                var ca = a.Colors?[ia(i)] ?? 0xFFFFFFu;
                var cb = b.Colors?[ib(i)] ?? 0xFFFFFFu;
                var d = Enumerable.Range(0, 3).Max(k => Math.Abs((int)((ca >> (8 * k)) & 255) - (int)((cb >> (8 * k)) & 255)));
                if (d > colour) { colour = d; worstColour = (ca, cb, i); }
            }
            // Texture weights: each quadrant is drawn with its own layers, so a layer that one side has at a shared vertex
            // and the other has not ends in a hard line right on the edge.
            var tex = (Diff: 0f, At: 0, A: "", B: "");
            for (var i = 0; i < 33; i++)
            {
                var (ca, ra) = (ia(i) % 33, ia(i) / 33);
                var (cb, rb) = (ib(i) % 33, ib(i) / 33);
                foreach (var qa in QuadrantsOf(ca, ra))
                    foreach (var qb in QuadrantsOf(cb, rb))
                    {
                        var wa = Weights(a, qa, ca, ra);
                        var wb = Weights(b, qb, cb, rb);
                        var d = wa.Keys.Union(wb.Keys).Max(k => Math.Abs(wa.GetValueOrDefault(k) - wb.GetValueOrDefault(k)));
                        if (d > tex.Diff) tex = (d, i, Show(wa), Show(wb));
                    }
            }
            if (tex.Diff > 0.05f) Console.WriteLine($"  {name}: TEXTURE weights differ by {tex.Diff:0.00} at vertex {tex.At}: {tex.A}  |  {tex.B}");
            Console.WriteLine($"  {name}: height diff {height:0.#}, normal diff {normal:0.#} deg, vertex colour diff {colour} (at {worstColour.At}: {Rgb(worstColour.A)} vs {Rgb(worstColour.B)}){(a.Colors is null || b.Colors is null ? $" [no VCLR: {(a.Colors is null ? "first" : "")} {(b.Colors is null ? "second" : "")}]" : "")}");
        }

        void Inner(string name, LandscapeData d)
        {
            // Quadrant edges inside a cell: column 16 (0|1, 2|3) and row 16 (0|2, 1|3).
            var worst = (Diff: 0f, Where: "", A: "", B: "");
            for (var i = 0; i < 33; i++)
                foreach (var (qa, qb, col, row) in new[] { (i <= 16 ? 0 : 2, i <= 16 ? 1 : 3, 16, i), (i <= 16 ? 0 : 1, i <= 16 ? 2 : 3, i, 16) })
                {
                    var wa = Weights(d, qa, col, row);
                    var wb = Weights(d, qb, col, row);
                    var diff = wa.Keys.Union(wb.Keys).Max(k => Math.Abs(wa.GetValueOrDefault(k) - wb.GetValueOrDefault(k)));
                    if (diff > worst.Diff) worst = (diff, $"q{qa}|q{qb} vertex {col},{row}", Show(wa), Show(wb));
                }
            if (worst.Diff > 0.05f) Console.WriteLine($"  {name} inside: TEXTURE weights differ by {worst.Diff:0.00} at {worst.Where}: {worst.A}  |  {worst.B}");
        }

        static IEnumerable<int> QuadrantsOf(int col, int row)
        {
            foreach (var qx in new[] { 0, 1 }.Where(q => q == 0 ? col <= 16 : col >= 16))
                foreach (var qy in new[] { 0, 1 }.Where(q => q == 0 ? row <= 16 : row >= 16))
                    yield return qy * 2 + qx;
        }

        Dictionary<string, float> Weights(LandscapeData d, int q, int col, int row)
        {
            var quadrant = d.Quadrants[q];
            var local = (row - (q >> 1) * 16) * 17 + (col - (q & 1) * 16);
            var w = new Dictionary<string, float> { [Diffuse(quadrant.BaseTexture)] = 1 };
            foreach (var layer in quadrant.Layers)
            {
                var o = layer.Opacity[local];
                foreach (var k in w.Keys.ToList()) w[k] *= 1 - o;
                w[Diffuse(layer.Texture)] = w.GetValueOrDefault(Diffuse(layer.Texture)) + o;
            }
            return w;
        }

        static string Show(Dictionary<string, float> w) => string.Join(" ", w.Where(p => p.Value > 0.02f).OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value:0.00}"));

        static string Rgb(uint c) => $"{c & 255},{(c >> 8) & 255},{(c >> 16) & 255}";
        static string Sum(Mutagen.Bethesda.Skyrim.ILandscapeGetter land)
        {
            var d = LandscapeData.From(land);
            return $"[h {Hash(d.Heights.Select(h => (int)MathF.Round(h)))} c {(d.Colors is null ? "-" : Hash(d.Colors.Select(c => (int)c)))} n {Hash(d.Normals.Select(n => (int)(n.X * 100) * 7 + (int)(n.Y * 100) * 3 + (int)(n.Z * 100)))} l {d.Quadrants.Sum(q => q.Layers.Count)}]";
        }
        static string Hash(IEnumerable<int> values) => (values.Aggregate(17u, (h, b) => h * 31 + (uint)b) % 10000).ToString("0000");
    }
}
