using System.Numerics;
using DotRecast.Core.Numerics;
using DotRecast.Recast;
using DotRecast.Recast.Geom;

namespace StbEditor.Core;

/// <summary>How the navmesh generator sees an actor and the world (game units).</summary>
public sealed record NavmeshGenerationSettings
{
    /// <summary>Voxel size across and up: smaller follows walls closer, costs more.</summary>
    public float CellSize { get; init; } = 8f;
    public float CellHeight { get; init; } = 4f;

    /// <summary>A humanoid: about 1.8 m tall, its shoulder half-width from walls, steps it climbs, slopes it walks.</summary>
    public float AgentHeight { get; init; } = 120f;
    public float AgentRadius { get; init; } = 20f;
    public float AgentClimb { get; init; } = 36f;
    public float MaxSlope { get; init; } = 50f;

    /// <summary>Longest border edge, and how far a simplified border may stray from the walkable outline.</summary>
    public float EdgeMaxLength { get; init; } = 512f;
    public float EdgeMaxError { get; init; } = 1.3f;

    /// <summary>Islands smaller than this (in voxels²) are dropped; regions smaller than the merge size join their neighbours.</summary>
    public int MinRegion { get; init; } = 8;
    public int MergeRegion { get; init; } = 20;

    /// <summary>Heights sampled for the vertices (in voxels), and their allowed error (units).</summary>
    public float DetailSampleDistance { get; init; } = 6f;
    public float DetailSampleError { get; init; } = 1f;

    /// <summary>A walkable piece this big (game units²) is kept even when no door reaches it; 0 keeps only what doors reach.</summary>
    public float KeepUnreachedArea { get; init; } = 40000f;
}

/// <summary>A generated navmesh: vertices and triangles (corner indices, Skyrim's winding), in game coordinates.</summary>
public sealed record GeneratedNavmesh(List<Vector3> Vertices, List<(int A, int B, int C)> Triangles, int Polygons)
{
    /// <summary>Walkable pieces dropped because nothing reaches them (table tops, roofs, beams), and their triangles.</summary>
    public int DroppedPieces { get; init; }
    public int DroppedTriangles { get; init; }

    /// <summary>Areas of the dropped pieces, biggest first (checks).</summary>
    public IReadOnlyList<float> DroppedAreas { get; init; } = [];
}

/// <summary>
/// Navmesh generation (CK: Generate NavMesh) with Recast (DotRecast): the walkable surface of the given triangles for an
/// actor of the settings' size, as convex polygons cut into triangles. Skyrim is Z-up, Recast Y-up: (x, y, z) goes in as
/// (x, z, −y), a rotation, so windings keep their facing. Vertex heights come from Recast's detail mesh (the polygons'
/// own heights are voxel tops).
/// </summary>
public static class NavmeshGenerator
{
    /// <param name="min">Lower corner of the area to cover (game coordinates); geometry beyond it only blocks.</param>
    /// <param name="clip">Keep polygons whose centre is inside [min, max] in X and Y only (an exterior cell's square).</param>
    /// <param name="seeds">Where actors come in (doors): only the walkable pieces near one are kept; none keeps the biggest
    /// piece, every piece at least a tenth of its size and (with <paramref name="clip"/>) every piece at the square's edge.</param>
    /// <param name="roofed">An interior: a big piece no door reaches is kept only under a roof (the top of a dungeon's shell is not).</param>
    public static GeneratedNavmesh Generate(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices, Vector3 min, Vector3 max, NavmeshGenerationSettings s,
        bool clip = false, IReadOnlyList<Vector3>? seeds = null, bool roofed = false)
    {
        var verts = new float[vertices.Count * 3];
        for (var i = 0; i < vertices.Count; i++)
        {
            verts[i * 3] = vertices[i].X;
            verts[i * 3 + 1] = vertices[i].Z;
            verts[i * 3 + 2] = -vertices[i].Y;
        }
        var geom = new RcSampleInputGeomProvider(verts, indices.ToArray());
        var cfg = new RcConfig(RcPartition.WATERSHED, s.CellSize, s.CellHeight, s.MaxSlope, s.AgentHeight, s.AgentRadius, s.AgentClimb,
            s.MinRegion, s.MergeRegion, s.EdgeMaxLength, s.EdgeMaxError, 6, s.DetailSampleDistance, s.DetailSampleError,
            true, true, true, new RcAreaModification(1), true);
        var bmin = new RcVec3f(min.X, min.Z, -max.Y);
        var bmax = new RcVec3f(max.X, max.Z, -min.Y);
        var result = new RcBuilder().Build(geom, new RcBuilderConfig(cfg, bmin, bmax), false);
        var mesh = result.Mesh;
        var detail = result.MeshDetail;

        // Vertices from the polygon mesh, heights from the detail mesh (each polygon's first detail vertices are its own).
        var heights = new float?[mesh.nverts];
        if (detail is not null)
        {
            for (var p = 0; p < mesh.npolys; p++)
            {
                var vertBase = detail.meshes[p * 4];
                var k = 0;
                for (var j = 0; j < mesh.nvp; j++)
                {
                    var v = mesh.polys[p * 2 * mesh.nvp + j];
                    if (v == RcRecast.RC_MESH_NULL_IDX) break;
                    heights[v] ??= detail.verts[(vertBase + k) * 3 + 1];
                    k++;
                }
            }
        }
        var output = new List<Vector3>(mesh.nverts);
        for (var i = 0; i < mesh.nverts; i++)
        {
            var x = mesh.bmin.X + mesh.verts[i * 3] * mesh.cs;
            var y = heights[i] ?? mesh.bmin.Y + mesh.verts[i * 3 + 1] * mesh.ch;
            var z = mesh.bmin.Z + mesh.verts[i * 3 + 2] * mesh.cs;
            output.Add(new Vector3(x, -z, y));
        }

        var triangles = new List<(int, int, int)>();
        var kept = 0;
        for (var p = 0; p < mesh.npolys; p++)
        {
            var poly = new List<int>();
            for (var j = 0; j < mesh.nvp; j++)
            {
                var v = mesh.polys[p * 2 * mesh.nvp + j];
                if (v == RcRecast.RC_MESH_NULL_IDX) break;
                poly.Add(v);
            }
            if (poly.Count < 3) continue;
            if (clip)
            {
                var c = poly.Aggregate(Vector3.Zero, (sum, v) => sum + output[v]) / poly.Count;
                if (c.X < min.X || c.X >= max.X || c.Y < min.Y || c.Y >= max.Y) continue;
            }
            kept++;
            // Recast's polygons are clockwise seen from above in its frame; flipped into Skyrim's they are counterclockwise, as the game's are.
            for (var j = 1; j + 1 < poly.Count; j++) triangles.Add((poly[0], poly[j], poly[j + 1]));
        }

        var roof = roofed ? new RoofTest(vertices, indices, s.AgentHeight) : null;
        var (reachable, pieces, droppedAreas) = Reachable(output, triangles, seeds, roof, s.KeepUnreachedArea, clip ? (min, max, s.CellSize * 3) : null);
        var dropped = triangles.Count - reachable.Count;
        return new GeneratedNavmesh(output, reachable, kept) { DroppedPieces = pieces, DroppedTriangles = dropped, DroppedAreas = droppedAreas };
    }

    /// <summary>
    /// The triangles of the pieces actors can reach: connected through shared vertices, a piece is kept when it comes
    /// within reach of a seed (a door: 160 units across, 120 up or down), or — with no seeds — when it is the biggest or
    /// at least a tenth of it; and any piece of <see cref="NavmeshGenerationSettings.KeepUnreachedArea"/> or more. Returns the kept triangles and how many
    /// pieces were dropped.
    /// </summary>
    /// <summary>Whether some geometry is overhead (straight up from a point, beyond an actor's height): a grid of triangle footprints.</summary>
    private sealed class RoofTest
    {
        private const float Cell = 256f;
        private readonly IReadOnlyList<Vector3> _vertices;
        private readonly IReadOnlyList<int> _indices;
        private readonly float _clearance;
        private readonly Dictionary<(int, int), List<int>> _grid = new();

        public RoofTest(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices, float clearance)
        {
            _vertices = vertices;
            _indices = indices;
            _clearance = clearance;
            for (var t = 0; t + 2 < indices.Count; t += 3)
            {
                Vector3 a = vertices[indices[t]], b = vertices[indices[t + 1]], c = vertices[indices[t + 2]];
                int x0 = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / Cell), x1 = (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / Cell);
                int y0 = (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) / Cell), y1 = (int)MathF.Floor(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) / Cell);
                if ((x1 - x0 + 1) * (y1 - y0 + 1) > 4096) continue;
                for (var x = x0; x <= x1; x++)
                    for (var y = y0; y <= y1; y++)
                    {
                        if (!_grid.TryGetValue((x, y), out var list)) _grid[(x, y)] = list = [];
                        list.Add(t);
                    }
            }
        }

        public bool Covered(Vector3 p)
        {
            if (!_grid.TryGetValue(((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell)), out var list)) return false;
            foreach (var t in list)
            {
                Vector3 a = _vertices[_indices[t]], b = _vertices[_indices[t + 1]], c = _vertices[_indices[t + 2]];
                var d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                if (MathF.Abs(d) < 1e-6f) continue;
                var u = ((b.Y - c.Y) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Y)) / d;
                var v = ((c.Y - a.Y) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Y)) / d;
                var w = 1 - u - v;
                if (u < 0 || v < 0 || w < 0) continue;
                if (u * a.Z + v * b.Z + w * c.Z > p.Z + _clearance) return true;
            }
            return false;
        }
    }

    private static (List<(int, int, int)> Kept, int Dropped, List<float> DroppedAreas) Reachable(List<Vector3> vertices, List<(int A, int B, int C)> triangles, IReadOnlyList<Vector3>? seeds, RoofTest? roof, float bigPiece,
        (Vector3 Min, Vector3 Max, float Reach)? border)
    {
        if (triangles.Count == 0) return ([], 0, []);
        var parent = Enumerable.Range(0, vertices.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
        foreach (var (a, b, c) in triangles) { Union(a, b); Union(b, c); }

        var area = new Dictionary<int, float>();
        foreach (var (a, b, c) in triangles)
        {
            var root = Find(a);
            area[root] = area.GetValueOrDefault(root) + Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).Length() / 2;
        }
        HashSet<int> keep;
        if (seeds is { Count: > 0 })
        {
            keep = [];
            foreach (var (a, b, c) in triangles)
            {
                var root = Find(a);
                if (keep.Contains(root)) continue;
                foreach (var corner in new[] { vertices[a], vertices[b], vertices[c], (vertices[a] + vertices[b] + vertices[c]) / 3 })
                    if (seeds.Any(s => MathF.Abs(s.Z - corner.Z) < 120 && Vector2.Distance(new(s.X, s.Y), new(corner.X, corner.Y)) < 160)) { keep.Add(root); break; }
            }
            if (keep.Count == 0) keep = [area.MaxBy(p => p.Value).Key];
        }
        else
        {
            var biggest = area.Values.Max();
            keep = area.Where(p => p.Value >= biggest * 0.1f).Select(p => p.Key).ToHashSet();
            // Outdoors the ground goes on into the next cell: a piece at the square's edge is reached from there.
            if (border is var (low, high, reach))
                foreach (var (a, b, c) in triangles)
                    foreach (var v in new[] { vertices[a], vertices[b], vertices[c] })
                        if (v.X - low.X < reach || high.X - v.X < reach || v.Y - low.Y < reach || high.Y - v.Y < reach) { keep.Add(Find(a)); break; }
        }
        // Rooms reached some way a walk on the flat does not show (past a gate, down a drop) are big; table tops,
        // beds and beams are not. Indoors such a room has a ceiling; the outside of the rooms' shell has none.
        var byPiece = triangles.GroupBy(t => Find(t.A)).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (root, size) in area)
        {
            if (bigPiece <= 0 || size < bigPiece || keep.Contains(root)) continue;
            if (roof is not null)
            {
                var samples = byPiece[root];
                var step = Math.Max(1, samples.Count / 24);
                var tested = 0;
                var covered = 0;
                for (var i = 0; i < samples.Count; i += step)
                {
                    var (a, b, c) = samples[i];
                    tested++;
                    if (roof.Covered((vertices[a] + vertices[b] + vertices[c]) / 3)) covered++;
                }
                if (covered * 2 < tested) continue;
            }
            keep.Add(root);
        }
        var kept = triangles.Where(t => keep.Contains(Find(t.A))).Select(t => (t.A, t.B, t.C)).ToList();
        return (kept, area.Count - keep.Count, area.Where(p => !keep.Contains(p.Key)).Select(p => p.Value).OrderByDescending(a => a).ToList());
    }
}
