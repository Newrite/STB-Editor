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
                    Console.WriteLine($"    quadrant {q}: base {Tex(quadrant.BaseTexture)}; layers {string.Join(", ", quadrant.Layers.Select(l => $"{Tex(l.Texture)} max {l.Opacity.Max():0.00}"))}");
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

        if (cells.TryGetValue(((int)MathF.Floor(px / 4096), (int)MathF.Floor(py / 4096)), out var here) && here is not null)
        {
            var fx = inX / LandscapeData.Spacing;
            var fy = inY / LandscapeData.Spacing;
            Console.WriteLine($"terrain at the point: vertex heights around {string.Join(", ", new[] { (0, 0), (1, 0), (0, 1), (1, 1) }.Select(d => here.Heights[Math.Min(32, (int)fy + d.Item2) * 33 + Math.Min(32, (int)fx + d.Item1)].ToString("0")))} (vertex {fx:0.0},{fy:0.0})");
        }
        // What stands on the ground near the point: flat meshes over the landscape show as seams too.
        var scene = new StbEditor.Render.SceneBuilder(ws, catalog).BuildExterior(world.FormKey, (int)MathF.Floor(px / 4096), (int)MathF.Floor(py / 4096), 1);
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
            Console.WriteLine($"  {name}: height diff {height:0.#}, normal diff {normal:0.#} deg, vertex colour diff {colour} (at {worstColour.At}: {Rgb(worstColour.A)} vs {Rgb(worstColour.B)}){(a.Colors is null || b.Colors is null ? $" [no VCLR: {(a.Colors is null ? "first" : "")} {(b.Colors is null ? "second" : "")}]" : "")}");
        }

        static string Rgb(uint c) => $"{c & 255},{(c >> 8) & 255},{(c >> 16) & 255}";
        static string Sum(Mutagen.Bethesda.Skyrim.ILandscapeGetter land)
        {
            var d = LandscapeData.From(land);
            return $"[h {Hash(d.Heights.Select(h => (int)MathF.Round(h)))} c {(d.Colors is null ? "-" : Hash(d.Colors.Select(c => (int)c)))} n {Hash(d.Normals.Select(n => (int)(n.X * 100) * 7 + (int)(n.Y * 100) * 3 + (int)(n.Z * 100)))} l {d.Quadrants.Sum(q => q.Layers.Count)}]";
        }
        static string Hash(IEnumerable<int> values) => (values.Aggregate(17u, (h, b) => h * 31 + (uint)b) % 10000).ToString("0000");
    }
}
