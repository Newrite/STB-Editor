using System.Numerics;
using StbEditor.Nif;

namespace StbEditor.Render;

public readonly record struct PickHit(SceneObject Object, float Distance, Vector3 Point);

/// <summary>
/// Ray picking on the CPU: bounding sphere first, then the triangles of the model in model space.
/// Precise enough for selection and cheap: only the handful of objects under the cursor get the
/// triangle test.
/// </summary>
public static class Picking
{
    public static PickHit? Pick(Scene scene, Vector3 origin, Vector3 direction, RenderOptions options)
    {
        PickHit? best = null;

        foreach (var obj in scene.Objects)
        {
            if (obj.IsDeleted || (obj.InitiallyDisabled && !options.ShowInitiallyDisabled)) continue;
            if (!RaySphere(origin, direction, obj.BoundsCenter, obj.BoundsRadius, out var sphereDistance)) continue;
            if (best is { } b && sphereDistance > b.Distance) continue;

            float? hit;
            if (obj.Model is { Shapes.Count: > 0 } model && obj.Kind is SceneObjectKind.Mesh or SceneObjectKind.Actor)
                hit = RayModel(obj, model, origin, direction, options);
            else
                hit = RaySphere(origin, direction, obj.BoundsCenter, obj.BoundsRadius * 0.8f, out var d) ? d : null;

            if (hit is { } distance && (best is null || distance < best.Value.Distance))
                best = new PickHit(obj, distance, origin + direction * distance);
        }

        return best;
    }

    /// <summary>
    /// Highest surface straight below <paramref name="origin"/>: terrain or any object not in
    /// <paramref name="exclude"/>. Returns its Z.
    /// </summary>
    public static float? CastDown(Scene scene, Vector3 origin, IReadOnlySet<SceneObject> exclude, RenderOptions options)
    {
        var down = -Vector3.UnitZ;
        float? best = null;

        if (PickTerrain(scene, origin, down) is { } ground) best = origin.Z - ground.Z;

        foreach (var obj in scene.Objects)
        {
            if (exclude.Contains(obj) || obj.IsDeleted || obj.Kind != SceneObjectKind.Mesh || obj.Model is not { Shapes.Count: > 0 } model) continue;
            // Only objects whose footprint can be under the point.
            var dx = obj.BoundsCenter.X - origin.X;
            var dy = obj.BoundsCenter.Y - origin.Y;
            if (dx * dx + dy * dy > obj.BoundsRadius * obj.BoundsRadius) continue;
            if (obj.BoundsCenter.Z - obj.BoundsRadius > origin.Z) continue;

            if (RayModel(obj, model, origin, down, options) is { } d && (best is null || d < best)) best = d;
        }

        return best is { } distance ? origin.Z - distance : null;
    }

    /// <summary>Ray against the terrain height fields (for "place on ground" and camera targeting).</summary>
    public static Vector3? PickTerrain(Scene scene, Vector3 origin, Vector3 direction)
    {
        float? best = null;
        foreach (var cell in scene.Terrain)
        {
            var o = cell.Origin;
            for (var y = 0; y < TerrainCell.Size - 1; y++)
            {
                for (var x = 0; x < TerrainCell.Size - 1; x++)
                {
                    var a = o + new Vector3(x * TerrainCell.Spacing, y * TerrainCell.Spacing, cell.HeightAt(x, y));
                    var b = o + new Vector3((x + 1) * TerrainCell.Spacing, y * TerrainCell.Spacing, cell.HeightAt(x + 1, y));
                    var c = o + new Vector3(x * TerrainCell.Spacing, (y + 1) * TerrainCell.Spacing, cell.HeightAt(x, y + 1));
                    var d = o + new Vector3((x + 1) * TerrainCell.Spacing, (y + 1) * TerrainCell.Spacing, cell.HeightAt(x + 1, y + 1));

                    foreach (var t in new[] { RayTriangle(origin, direction, a, b, d), RayTriangle(origin, direction, a, d, c) })
                        if (t is { } hit && (best is null || hit < best)) best = hit;
                }
            }
        }

        return best is { } distance ? origin + direction * distance : null;
    }

    private static float? RayModel(SceneObject obj, GpuModel model, Vector3 origin, Vector3 direction, RenderOptions options)
    {
        // Into model space: inverse of R·(s·v) + t.
        var t = obj.Transform;
        var inverse = new Matrix3(
            new Vector3(t.Rotation.Row0.X, t.Rotation.Row1.X, t.Rotation.Row2.X),
            new Vector3(t.Rotation.Row0.Y, t.Rotation.Row1.Y, t.Rotation.Row2.Y),
            new Vector3(t.Rotation.Row0.Z, t.Rotation.Row1.Z, t.Rotation.Row2.Z));
        var scale = Math.Abs(t.Scale) < 1e-6f ? 1f : t.Scale;
        var localOrigin = inverse.Transform(origin - t.Translation) / scale;
        var localDirection = inverse.Transform(direction) / scale;

        float? best = null;
        foreach (var shape in model.Shapes)
        {
            var source = shape.Source;
            if (!SceneRenderer.IsShapeVisible(source, options)) continue;
            // Camera-facing cards have no fixed triangles to hit.
            if (source.Billboard is not null) continue;
            if (!RayBox(localOrigin, localDirection, source.BoundsMin, source.BoundsMax)) continue;

            var p = source.Positions;
            var idx = source.Indices;
            for (var i = 0; i + 2 < idx.Length; i += 3)
            {
                var hit = RayTriangle(localOrigin, localDirection, p[idx[i]], p[idx[i + 1]], p[idx[i + 2]]);
                if (hit is { } h && (best is null || h < best)) best = h;
            }
        }

        // The parameter is the same along the scaled local ray as along the world ray.
        return best;
    }

    private static bool RaySphere(Vector3 origin, Vector3 direction, Vector3 center, float radius, out float distance)
    {
        var oc = origin - center;
        var b = Vector3.Dot(oc, direction);
        var c = oc.LengthSquared() - radius * radius;
        var discriminant = b * b - c;
        distance = 0;
        if (discriminant < 0) return false;
        var s = MathF.Sqrt(discriminant);
        distance = -b - s;
        if (distance < 0) distance = -b + s;
        return distance >= 0;
    }

    private static bool RayBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max)
    {
        var inv = new Vector3(1 / direction.X, 1 / direction.Y, 1 / direction.Z);
        var t1 = (min - origin) * inv;
        var t2 = (max - origin) * inv;
        var tmin = Vector3.Min(t1, t2);
        var tmax = Vector3.Max(t1, t2);
        var near = MathF.Max(MathF.Max(tmin.X, tmin.Y), tmin.Z);
        var far = MathF.Min(MathF.Min(tmax.X, tmax.Y), tmax.Z);
        return far >= MathF.Max(near, 0);
    }

    /// <summary>Möller–Trumbore, both faces.</summary>
    public static float? RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
    {
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(direction, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f) return null;
        var invDet = 1 / det;
        var s = origin - a;
        var u = Vector3.Dot(s, p) * invDet;
        if (u < 0 || u > 1) return null;
        var q = Vector3.Cross(s, e1);
        var v = Vector3.Dot(direction, q) * invDet;
        if (v < 0 || u + v > 1) return null;
        var t = Vector3.Dot(e2, q) * invDet;
        return t > 0.01f ? t : null;
    }
}
