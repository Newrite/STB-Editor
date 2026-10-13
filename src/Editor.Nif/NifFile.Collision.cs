using System.Numerics;

namespace StbEditor.Nif;

/// <summary>
/// The Havok collision of a mesh as convex pieces (model space, game units) — what physics needs for loose objects: a node's
/// bhkCollisionObject → bhkRigidBody(T) → box, sphere, capsule, convex vertices, list, (convex) transform or MOPP shapes.
/// Triangle-mesh collision (compressed / packed strips, what statics use) is not read: such a model's pieces are marked
/// incomplete and physics falls back to the render mesh. Layouts from niftools nif.xml (Skyrim, 20.2.0.7).
/// </summary>
public static partial class NifFile
{
    /// <summary>Havok units in game units (Skyrim's world scale).</summary>
    public const float HavokScale = 69.99124f;

    private static void ReadCollision(Context ctx, int index, NifTransform node)
    {
        if (!ctx.IsBlock(index)) return;
        var type = ctx.Header.TypeOf(index);
        if (type is not ("bhkCollisionObject" or "bhkSPCollisionObject" or "bhkBlendCollisionObject")) return;
        try
        {
            var r = new BlockReader(ctx.Block(index));
            r.Int(); // target
            r.UShort(); // flags
            var body = r.Int();
            if (!ctx.IsBlock(body)) return;
            var bodyType = ctx.Header.TypeOf(body);
            if (bodyType is not ("bhkRigidBody" or "bhkRigidBodyT")) { ctx.Model.CollisionComplete = false; return; }
            var b = new BlockReader(ctx.Block(body));
            var shape = b.Int();
            // The T variant places its shape: translation and rotation inside the body's construction info.
            var placement = Matrix4x4.Identity;
            if (bodyType == "bhkRigidBodyT" && b.Remaining >= 80)
            {
                b.Skip(4 + 20 + 20); // filter, world object cinfo, the start of the rigid body cinfo
                var t = b.Vec4();
                var q = new Quaternion(b.Float(), b.Float(), b.Float(), b.Float());
                placement = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q)) * Matrix4x4.CreateTranslation(new Vector3(t.X, t.Y, t.Z) * HavokScale);
            }
            ReadShape(ctx, shape, placement, node, 0);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            ctx.Model.CollisionComplete = false;
            ctx.Model.Warnings.Add($"block {index} collision: {ex.Message}");
        }
    }

    /// <summary>One shape into convex pieces; <paramref name="local"/> is body space (game units) → the node, then the node → model.</summary>
    private static void ReadShape(Context ctx, int index, Matrix4x4 local, NifTransform node, int depth)
    {
        if (!ctx.IsBlock(index) || depth > 16) { ctx.Model.CollisionComplete = false; return; }
        var type = ctx.Header.TypeOf(index);
        var r = new BlockReader(ctx.Block(index));
        void Piece(IEnumerable<Vector3> havokPoints)
        {
            var points = havokPoints.Select(p => node.Apply(Vector3.Transform(p * HavokScale, local))).ToArray();
            if (points.Length >= 4) ctx.Model.CollisionPieces.Add(points);
        }
        switch (type)
        {
            case "bhkConvexVerticesShape":
            {
                r.Skip(4); // material
                var radius = r.Float();
                r.Skip(12 + 12); // vertex and normal properties
                var count = r.Int();
                if (count is < 4 or > 65536) { ctx.Model.CollisionComplete = false; return; }
                var points = new List<Vector3>(count * 7);
                for (var i = 0; i < count; i++)
                {
                    var v = r.Vec4();
                    var p = new Vector3(v.X, v.Y, v.Z);
                    points.Add(p);
                    // The shape is the hull grown by its convex radius (about 2 game units): the hull of each corner's
                    // six neighbours along the axes comes close.
                    if (radius > 0)
                        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                        {
                            points.Add(p + axis * radius);
                            points.Add(p - axis * radius);
                        }
                }
                Piece(points);
                break;
            }
            case "bhkBoxShape":
            {
                r.Skip(4);
                var radius = r.Float();
                r.Skip(8);
                var d = r.Vec3() + new Vector3(radius);
                Piece([new(-d.X, -d.Y, -d.Z), new(d.X, -d.Y, -d.Z), new(-d.X, d.Y, -d.Z), new(d.X, d.Y, -d.Z),
                    new(-d.X, -d.Y, d.Z), new(d.X, -d.Y, d.Z), new(-d.X, d.Y, d.Z), new(d.X, d.Y, d.Z)]);
                break;
            }
            case "bhkSphereShape":
            {
                r.Skip(4);
                Piece(Sphere(Vector3.Zero, r.Float()));
                break;
            }
            case "bhkCapsuleShape":
            {
                r.Skip(4);
                var radius = r.Float();
                r.Skip(8);
                var a = r.Vec3();
                var ra = r.Float();
                var b = r.Vec3();
                var rb = r.Float();
                Piece(Sphere(a, MathF.Max(radius, ra)).Concat(Sphere(b, MathF.Max(radius, rb))));
                break;
            }
            case "bhkListShape":
            {
                var children = r.Refs();
                foreach (var child in children) ReadShape(ctx, child, local, node, depth + 1);
                break;
            }
            case "bhkConvexTransformShape" or "bhkTransformShape":
            {
                var child = r.Int();
                r.Skip(4 + 4 + 8); // material, radius, unused
                var m = new float[16];
                for (var i = 0; i < 16; i++) m[i] = r.Float();
                // Stored with the translation in the last four floats' first three (row vectors), as NifSkope shows it.
                var matrix = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
                if (MathF.Abs(m[3]) + MathF.Abs(m[7]) + MathF.Abs(m[11]) > 1e-4f && MathF.Abs(m[12]) + MathF.Abs(m[13]) + MathF.Abs(m[14]) < 1e-6f) matrix = Matrix4x4.Transpose(matrix);
                matrix.M41 *= HavokScale;
                matrix.M42 *= HavokScale;
                matrix.M43 *= HavokScale;
                ReadShape(ctx, child, matrix * local, node, depth + 1);
                break;
            }
            case "bhkMoppBvTreeShape":
                ReadShape(ctx, r.Int(), local, node, depth + 1);
                break;
            default:
                // Triangle meshes (compressed / packed strips) and anything else: the render mesh stands in.
                ctx.Model.CollisionComplete = false;
                break;
        }
    }

    /// <summary>Points on a sphere (enough for a convex hull).</summary>
    private static IEnumerable<Vector3> Sphere(Vector3 centre, float radius)
    {
        for (var i = 0; i < 3; i++)
        {
            var axis = new Vector3(i == 0 ? 1 : 0, i == 1 ? 1 : 0, i == 2 ? 1 : 0) * radius;
            yield return centre + axis;
            yield return centre - axis;
        }
        var d = radius / MathF.Sqrt(3);
        for (var x = -1; x <= 1; x += 2)
            for (var y = -1; y <= 1; y += 2)
                for (var z = -1; z <= 1; z += 2)
                    yield return centre + new Vector3(x, y, z) * d;
    }
}
