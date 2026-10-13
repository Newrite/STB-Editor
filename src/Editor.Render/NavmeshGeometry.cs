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

    /// <param name="shapes">The object's mesh shapes (model space) and whether the mesh has collision, or null when not loaded.
    /// Meshes without collision (black void planes, hanging cloth, light shafts) are left out: actors walk through them.</param>
    public static (List<Vector3> Vertices, List<int> Indices) Collect(Scene scene, Vector3 min, Vector3 max, Func<SceneObject, (IReadOnlyList<NifShape> Shapes, bool Collision)?> shapes)
        => Collect(scene.Objects, scene.Terrain, min, max, shapes);

    /// <summary>The same over given objects and terrain (a snapshot taken on the UI thread, for a worker).</summary>
    public static (List<Vector3> Vertices, List<int> Indices) Collect(IEnumerable<SceneObject> objects, IEnumerable<TerrainCell> terrain, Vector3 min, Vector3 max,
        Func<SceneObject, (IReadOnlyList<NifShape> Shapes, bool Collision)?> shapes)
    {
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        foreach (var obj in objects)
        {
            if (obj.IsDeleted || obj.InitiallyDisabled || obj.Kind != SceneObjectKind.Mesh || !Solid.Contains(obj.BaseSignature)) continue;
            if (obj.BoundsCenter.X + obj.BoundsRadius < min.X || obj.BoundsCenter.X - obj.BoundsRadius > max.X
                || obj.BoundsCenter.Y + obj.BoundsRadius < min.Y || obj.BoundsCenter.Y - obj.BoundsRadius > max.Y
                || obj.BoundsCenter.Z + obj.BoundsRadius < min.Z || obj.BoundsCenter.Z - obj.BoundsRadius > max.Z) continue;
            if (shapes(obj) is not { Collision: true } found) continue;
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
}
