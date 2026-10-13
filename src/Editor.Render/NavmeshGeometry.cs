using System.Numerics;
using StbEditor.Nif;

namespace StbEditor.Render;

/// <summary>
/// What a navmesh is generated over: the triangles of the things an actor walks on or bumps into — architecture,
/// furniture, containers, activators, trees, movable statics — and the terrain; not doors (actors open them: the
/// navmesh goes through, as the CK's does), small loose items, actors, markers, effects or see-through cards.
/// </summary>
public static class NavmeshGeometry
{
    /// <summary>Base types whose meshes count as ground or obstacles.</summary>
    public static readonly HashSet<string> Solid = ["STAT", "MSTT", "FURN", "CONT", "TREE", "FLOR", "SCOL"];

    /// <summary>What an object is made of for the navmesh: its render shapes, whether it has collision, and that collision when it was read.</summary>
    public sealed record Source(IReadOnlyList<NifShape> Shapes, bool HasCollision, IReadOnlyList<Vector3>? CollisionPositions = null,
        IReadOnlyList<int>? CollisionIndices = null, IReadOnlyList<Vector3[]>? Pieces = null)
    {
        public bool HasCollisionGeometry => (CollisionIndices?.Count ?? 0) > 0 || (Pieces?.Count ?? 0) > 0;

        public static Source Of(NifModel model) => new(model.Shapes, model.HasCollision, model.CollisionPositions, model.CollisionIndices, model.CollisionPieces);
    }

    /// <param name="sources">The object's mesh, or null when not loaded. Meshes without collision (black void planes, hanging cloth, light
    /// shafts) are left out: actors walk through them. With <paramref name="useCollision"/>, the Havok collision stands in for the render
    /// mesh where it could be read — as the CK builds navmeshes; otherwise (and where it could not) the render mesh does. Off by default:
    /// against the game's navmeshes the render meshes did as well or better (the inn's entrance was cut off by collision).</param>
    public static (List<Vector3> Vertices, List<int> Indices) Collect(Scene scene, Vector3 min, Vector3 max, Func<SceneObject, Source?> sources, bool useCollision = false)
        => Collect(scene.Objects, scene.Terrain, min, max, sources, useCollision);

    /// <summary>The same over given objects and terrain (a snapshot taken on the UI thread, for a worker).</summary>
    public static (List<Vector3> Vertices, List<int> Indices) Collect(IEnumerable<SceneObject> objects, IEnumerable<TerrainCell> terrain, Vector3 min, Vector3 max,
        Func<SceneObject, Source?> sources, bool useCollision = false)
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        foreach (var obj in objects)
        {
            if (obj.IsDeleted || obj.InitiallyDisabled || obj.Kind != SceneObjectKind.Mesh || !Solid.Contains(obj.BaseSignature)) continue;
            if (obj.BoundsCenter.X + obj.BoundsRadius < min.X || obj.BoundsCenter.X - obj.BoundsRadius > max.X
                || obj.BoundsCenter.Y + obj.BoundsRadius < min.Y || obj.BoundsCenter.Y - obj.BoundsRadius > max.Y
                || obj.BoundsCenter.Z + obj.BoundsRadius < min.Z || obj.BoundsCenter.Z - obj.BoundsRadius > max.Z) continue;
            if (sources(obj) is not { HasCollision: true } found) continue;
            if (useCollision && found.HasCollisionGeometry)
            {
                AddCollision(found, obj.World, vertices, indices);
                continue;
            }
            foreach (var shape in found.Shapes)
            {
                if (shape.IsHidden || shape.IsEditorMarker || shape.Billboard is not null || shape.Material.Kind == ShaderKind.Effect || shape.Material.AlphaBlend) continue;
                if (shape.Material.Flags1.HasFlag(ShaderFlags1.Refraction)) continue;
                var start = vertices.Count;
                foreach (var p in shape.Positions) vertices.Add(Vector3.Transform(p, obj.World));
                foreach (var i in shape.Indices) indices.Add(start + (int)i);
            }
        }

        foreach (var cell in terrain)
        {
            var origin = cell.Origin;
            var size = (TerrainCell.Size - 1) * TerrainCell.Spacing;
            if (origin.X > max.X || origin.Y > max.Y || origin.X + size < min.X || origin.Y + size < min.Y) continue;
            var start = vertices.Count;
            for (var y = 0; y < TerrainCell.Size; y++)
                for (var x = 0; x < TerrainCell.Size; x++)
                    vertices.Add(origin + new Vector3(x * TerrainCell.Spacing, y * TerrainCell.Spacing, cell.HeightAt(x, y)));
            for (var y = 0; y < TerrainCell.Size - 1; y++)
                for (var x = 0; x < TerrainCell.Size - 1; x++)
                {
                    int a = start + y * TerrainCell.Size + x, b = a + 1, c = a + TerrainCell.Size, d = c + 1;
                    // Counterclockwise seen from above, like the meshes.
                    indices.AddRange([a, b, d, a, d, c]);
                }
        }
        return (vertices, indices);
    }

    /// <summary>Four of the points span a tetrahedron of some volume.</summary>
    private static bool HasVolume(Vector3[] points)
    {
        if (points.Length < 4) return false;
        var a = points[0];
        var b = points.MaxBy(p => Vector3.DistanceSquared(p, a));
        var ab = b - a;
        if (ab.LengthSquared() < 1e-4f) return false;
        var c = points.MaxBy(p => Vector3.Cross(ab, p - a).LengthSquared());
        var normal = Vector3.Cross(ab, c - a);
        if (normal.LengthSquared() < 1e-6f) return false;
        return points.Any(p => MathF.Abs(Vector3.Dot(normal, p - a)) > 1e-3f * MathF.Sqrt(normal.Length()));
    }

    /// <summary>The collision's triangles and its convex pieces' hulls (faces fanned into triangles), placed by the object.</summary>
    private static void AddCollision(Source source, Matrix4x4 world, List<Vector3> vertices, List<int> indices)
    {
        if (source.CollisionPositions is { } positions && source.CollisionIndices is { } list)
        {
            var start = vertices.Count;
            foreach (var p in positions) vertices.Add(Vector3.Transform(p, world));
            foreach (var i in list) indices.Add(start + i);
        }
        if (source.Pieces is not { Count: > 0 } pieces) return;
        var pool = new BepuUtilities.Memory.BufferPool();
        try
        {
            foreach (var raw in pieces)
            {
                // Bepu's hull builder wants a real volume: finite, distinct points not all in one plane.
                var piece = raw.Where(p => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)).Distinct().ToArray();
                if (!HasVolume(piece)) continue;
                BepuPhysics.Collidables.ConvexHullHelper.ComputeHull(piece.AsSpan(), pool, out var hull);
                try
                {
                    var start = vertices.Count;
                    for (var i = 0; i < hull.OriginalVertexMapping.Length; i++) vertices.Add(Vector3.Transform(piece[hull.OriginalVertexMapping[i]], world));
                    for (var f = 0; f < hull.FaceStartIndices.Length; f++)
                    {
                        var first = hull.FaceStartIndices[f];
                        var end = f + 1 < hull.FaceStartIndices.Length ? hull.FaceStartIndices[f + 1] : hull.FaceVertexIndices.Length;
                        for (var k = first + 1; k + 1 < end; k++)
                            indices.AddRange([start + hull.FaceVertexIndices[first], start + hull.FaceVertexIndices[k], start + hull.FaceVertexIndices[k + 1]]);
                    }
                }
                finally { hull.Dispose(pool); }
            }
        }
        finally { pool.Clear(); }
    }
}
