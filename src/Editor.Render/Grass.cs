using System.Numerics;
using Mutagen.Bethesda.Plugins;

namespace StbEditor.Render;

/// <summary>A grass type (GRAS) as the placement needs it.</summary>
/// <param name="Density">Chance in percent that a grid point of its texture gets a tuft.</param>
/// <param name="Water">How the ground must sit against the water: "above at least", "below at most"… (Mutagen's names).</param>
public sealed record GrassKind(string Model, float Density, float MinSlope, float MaxSlope, float UnitsFromWater, string Water,
    float PositionRange, float HeightRange, bool UniformScaling, bool FitToSlope);

/// <summary>Tufts of one grass model in one cell: their world matrices and the patch's bounds.</summary>
public sealed record GrassPatch(string Model, Matrix4x4[] Worlds, Vector3 Min, Vector3 Max);

/// <summary>
/// Grass on a cell's land, placed the way the game does it (an approximation — the game's random numbers are its own):
/// a grid every iMinGrassSize units; at each point the landscape texture that shows most there picks its grass types (LTEX
/// grass list), each in turn rolls its density; then the point moves by up to PositionRange, the slope must lie within
/// MinSlope..MaxSlope, the ground must keep its distance from the water, the tuft turns at random about Z, grows by up to
/// ±HeightRange (all axes with UniformScaling) and leans with the ground with FitToSlope.
/// </summary>
public static class GrassField
{
    public static List<GrassPatch> Generate(TerrainCell terrain, Func<FormKey, IReadOnlyList<GrassKind>> grassOf, float spacing, IReadOnlyList<WaterPlane> water)
    {
        var patches = new Dictionary<string, List<Matrix4x4>>(StringComparer.OrdinalIgnoreCase);
        if (terrain.Data is not { } data || spacing < 4) return [];
        var origin = terrain.Origin;
        var waterHeight = water.FirstOrDefault(w => origin.X + 1 >= w.Min.X && origin.X + 1 <= w.Max.X && origin.Y + 1 >= w.Min.Y && origin.Y + 1 <= w.Max.Y)?.Height;
        var steps = (int)(TerrainCell.CellSize / spacing);
        for (var iy = 0; iy < steps; iy++)
            for (var ix = 0; ix < steps; ix++)
            {
                var random = new Random(HashCode.Combine(terrain.X, terrain.Y, ix, iy));
                var local = new Vector2((ix + 0.5f) * spacing, (iy + 0.5f) * spacing);
                if (Dominant(data, local) is not { } texture) continue;
                var kinds = grassOf(texture);
                foreach (var kind in kinds)
                {
                    if (random.NextSingle() * 100 >= kind.Density) continue;
                    var p = local + new Vector2(random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1) * kind.PositionRange;
                    p = Vector2.Clamp(p, Vector2.Zero, new Vector2(TerrainCell.CellSize - 0.01f));
                    var (height, normal) = Ground(terrain, p);
                    var slope = MathF.Acos(Math.Clamp(normal.Z, -1, 1)) * 180 / MathF.PI;
                    if (slope < kind.MinSlope || slope > kind.MaxSlope) continue;
                    if (waterHeight is { } w && !WaterAllows(kind, height - w)) continue;
                    var grow = 1 + (random.NextSingle() * 2 - 1) * kind.HeightRange;
                    var scale = kind.UniformScaling ? new Vector3(grow) : new Vector3(1, 1, grow);
                    var world = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationZ(random.NextSingle() * MathF.Tau);
                    if (kind.FitToSlope && normal.Z < 0.9999f)
                    {
                        var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, normal));
                        world *= Matrix4x4.CreateFromAxisAngle(axis, MathF.Acos(Math.Clamp(normal.Z, -1, 1)));
                    }
                    world *= Matrix4x4.CreateTranslation(origin.X + p.X, origin.Y + p.Y, height);
                    if (!patches.TryGetValue(kind.Model, out var list)) patches[kind.Model] = list = [];
                    list.Add(world);
                    // One tuft per point: the first type that comes up takes it.
                    break;
                }
            }
        var result = new List<GrassPatch>();
        foreach (var (model, worlds) in patches)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var w in worlds) { min = Vector3.Min(min, w.Translation); max = Vector3.Max(max, w.Translation); }
            result.Add(new GrassPatch(model, worlds.ToArray(), min - new Vector3(64, 64, 0), max + new Vector3(64, 64, 128)));
        }
        return result;
    }

    /// <summary>Whether ground <paramref name="above"/> units over the water (negative: under it) may grow this grass.</summary>
    public static bool WaterAllows(GrassKind kind, float above)
    {
        var units = kind.UnitsFromWater;
        return kind.Water switch
        {
            "AboveAtLeast" => above >= units,
            "AboveAtMost" => above >= 0 && above <= units,
            "BelowAtLeast" => -above >= units,
            "BelowAtMost" => above <= 0 && -above <= units,
            "EitherAtLeast" => MathF.Abs(above) >= units,
            "EitherAtMost" => MathF.Abs(above) <= units,
            "EitherAtMostAbove" => above >= 0 ? above <= units : true,
            "EitherAtMostBelow" => above <= 0 ? -above <= units : true,
            _ => true,
        };
    }

    /// <summary>The landscape texture that shows most at a point of the cell (null: the game's default land, no grass).</summary>
    public static FormKey? Dominant(Core.LandscapeData data, Vector2 local)
    {
        // Quadrants: 0 SW, 1 SE, 2 NW, 3 NE, 17 × 17 opacity points each, 128 units apart.
        var half = TerrainCell.CellSize / 2;
        var q = (local.X >= half ? 1 : 0) + (local.Y >= half ? 2 : 0);
        var quadrant = data.Quadrants[q];
        var u = (local.X - (q & 1) * half) / (half / 16);
        var v = (local.Y - (q >> 1) * half) / (half / 16);
        FormKey? best = quadrant.BaseTexture;
        var bestWeight = 0f;
        // Layers cover what is under them: the base keeps (1 − o) of each layer above, a layer keeps its own o times the rest.
        var weights = new float[quadrant.Layers.Count];
        var baseWeight = 1f;
        for (var i = 0; i < quadrant.Layers.Count; i++)
        {
            var o = Sample(quadrant.Layers[i].Opacity, u, v);
            for (var j = 0; j < i; j++) weights[j] *= 1 - o;
            baseWeight *= 1 - o;
            weights[i] = o;
        }
        bestWeight = baseWeight;
        for (var i = 0; i < weights.Length; i++)
            if (weights[i] > bestWeight) { bestWeight = weights[i]; best = quadrant.Layers[i].Texture; }
        return best is { IsNull: false } ? best : null;
    }

    private static float Sample(float[] grid, float u, float v)
    {
        const int n = 17;
        var x0 = Math.Clamp((int)u, 0, n - 2);
        var y0 = Math.Clamp((int)v, 0, n - 2);
        var fx = Math.Clamp(u - x0, 0, 1);
        var fy = Math.Clamp(v - y0, 0, 1);
        float At(int x, int y) => grid[y * n + x];
        return (At(x0, y0) * (1 - fx) + At(x0 + 1, y0) * fx) * (1 - fy) + (At(x0, y0 + 1) * (1 - fx) + At(x0 + 1, y0 + 1) * fx) * fy;
    }

    /// <summary>Height and normal of the land at a cell-local point (the triangle it falls in).</summary>
    public static (float Height, Vector3 Normal) Ground(TerrainCell terrain, Vector2 local)
    {
        const int n = TerrainCell.Size;
        var u = local.X / TerrainCell.Spacing;
        var v = local.Y / TerrainCell.Spacing;
        var x0 = Math.Clamp((int)u, 0, n - 2);
        var y0 = Math.Clamp((int)v, 0, n - 2);
        var fx = Math.Clamp(u - x0, 0, 1);
        var fy = Math.Clamp(v - y0, 0, 1);
        float H(int x, int y) => terrain.HeightAt(x, y);
        var height = (H(x0, y0) * (1 - fx) + H(x0 + 1, y0) * fx) * (1 - fy) + (H(x0, y0 + 1) * (1 - fx) + H(x0 + 1, y0 + 1) * fx) * fy;
        var dx = ((H(x0 + 1, y0) - H(x0, y0)) * (1 - fy) + (H(x0 + 1, y0 + 1) - H(x0, y0 + 1)) * fy) / TerrainCell.Spacing;
        var dy = ((H(x0, y0 + 1) - H(x0, y0)) * (1 - fx) + (H(x0 + 1, y0 + 1) - H(x0 + 1, y0)) * fx) / TerrainCell.Spacing;
        return (height, Vector3.Normalize(new Vector3(-dx, -dy, 1)));
    }
}
