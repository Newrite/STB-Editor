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
            if (bodyType == "bhkRigidBodyT" && ctx.Header.BlockSizes[body] >= 96)
            {
                // Translation (Vector4) then rotation (quaternion) inside the construction info; where they start differs a
                // little between versions, so the rotation is found as the first unit quaternion from there.
                var start = ctx.Header.BlockOffsets[body];
                var bytes = ctx.Data;
                float F(int at) => BitConverter.ToSingle(bytes, start + at);
                for (var at = 60; at <= 76; at += 4)
                {
                    var q = new Quaternion(F(at), F(at + 4), F(at + 8), F(at + 12));
                    if (!float.IsFinite(q.Length()) || MathF.Abs(q.Length() - 1) > 1e-3f) continue;
                    var t = new Vector3(F(at - 16), F(at - 12), F(at - 8));
                    if (!float.IsFinite(t.X) || !float.IsFinite(t.Y) || !float.IsFinite(t.Z) || t.Length() > 1e5f) t = Vector3.Zero;
                    placement = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q)) * Matrix4x4.CreateTranslation(t * HavokScale);
                    break;
                }
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
            var points = havokPoints.Select(p => node.Apply(Vector3.Transform(p * HavokScale, local))).Where(p => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)).ToArray();
            if (points.Length >= 4) ctx.Model.CollisionPieces.Add(SafePiece(points));
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
                var points = new Vector3[count];
                for (var i = 0; i < count; i++) { var v = r.Vec4(); points[i] = new Vector3(v.X, v.Y, v.Z); }
                // The shape is the hull grown by its convex radius (about 2 game units): each corner pushed out from the middle.
                if (radius > 0)
                {
                    var middle = points.Aggregate(Vector3.Zero, (a, b) => a + b) / count;
                    for (var i = 0; i < count; i++)
                    {
                        var away = points[i] - middle;
                        if (away.LengthSquared() > 1e-12f) points[i] += Vector3.Normalize(away) * radius;
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
            case "bhkCompressedMeshShape":
            {
                // A triangle mesh (statics, buildings): kept as triangles (collision view, navmesh); not a body that can fall.
                ctx.Model.CollisionComplete = false;
                r.Skip(4 + 4 + 4 + 4 + 16 + 4 + 16); // target, user data, radius, unknown, scale, radius copy, scale copy
                var data = r.Int();
                if (ctx.IsBlock(data) && ctx.Header.TypeOf(data) == "bhkCompressedMeshShapeData")
                    ReadCompressedMesh(ctx, data, p => node.Apply(Vector3.Transform(p * HavokScale, local)));
                break;
            }
            default:
                // Triangle meshes (compressed / packed strips) and anything else: the render mesh stands in.
                ctx.Model.CollisionComplete = false;
                break;
        }
    }

    /// <summary>
    /// bhkCompressedMeshShapeData: chunks of quantized vertices (chunk translation + steps of the error, then the chunk's
    /// transform) with strips and a triangle list, and the "big" triangles of full-precision vertices.
    /// </summary>
    private static void ReadCompressedMesh(Context ctx, int index, Func<Vector3, Vector3> place)
    {
        var r = new BlockReader(ctx.Block(index));
        r.Skip(4 * 4); // bits per index / W index, masks
        var error = r.Float();
        if (error is <= 0 or > 0.1f) error = 0.001f;
        r.Skip(16 + 16 + 2); // bounds, welding and material type
        for (var i = 0; i < 3; i++) r.Skip(4 * r.Int()); // 32 / 16 / 8-bit materials
        r.Skip(8 * r.Int()); // chunk materials
        r.Int(); // named materials
        var transformCount = r.Int();
        var transforms = new (Vector3 Translation, Quaternion Rotation)[transformCount];
        for (var i = 0; i < transformCount; i++)
        {
            var t = r.Vec4();
            var q = new Quaternion(r.Float(), r.Float(), r.Float(), r.Float());
            transforms[i] = (new Vector3(t.X, t.Y, t.Z), Quaternion.Normalize(q));
        }
        var bigCount = r.Int();
        var big = new Vector3[bigCount];
        for (var i = 0; i < bigCount; i++) { var v = r.Vec4(); big[i] = new Vector3(v.X, v.Y, v.Z); }
        var positions = ctx.Model.CollisionPositions;
        var indices = ctx.Model.CollisionIndices;
        var bigTris = r.Int();
        var bigStart = positions.Count;
        foreach (var v in big) positions.Add(place(v));
        for (var i = 0; i < bigTris; i++)
        {
            int a = r.UShort(), b = r.UShort(), c = r.UShort();
            r.Skip(4 + 2);
            if (a < bigCount && b < bigCount && c < bigCount) indices.AddRange([bigStart + a, bigStart + b, bigStart + c]);
        }
        var chunks = r.Int();
        for (var k = 0; k < chunks; k++)
        {
            var origin = r.Vec4();
            r.UInt(); // material
            r.UShort(); // reference
            var transform = r.UShort();
            var count = r.Int();
            var start = positions.Count;
            for (var i = 0; i + 2 < count; i += 3)
            {
                var v = new Vector3(origin.X, origin.Y, origin.Z) + new Vector3(r.UShort(), r.UShort(), r.UShort()) * error;
                if (transform < transforms.Length) v = Vector3.Transform(v, transforms[transform].Rotation) + transforms[transform].Translation;
                positions.Add(place(v));
            }
            r.Skip(2 * (count % 3));
            var vertexCount = positions.Count - start;
            var indexCount = r.Int();
            var chunkIndices = new int[indexCount];
            for (var i = 0; i < indexCount; i++) chunkIndices[i] = r.UShort();
            var stripCount = r.Int();
            var at = 0;
            for (var s = 0; s < stripCount; s++)
            {
                var length = r.UShort();
                for (var i = 0; i + 2 < length && at + i + 2 < indexCount; i++)
                {
                    int a = chunkIndices[at + i], b = chunkIndices[at + i + 1], c = chunkIndices[at + i + 2];
                    if (a == b || b == c || a == c || a >= vertexCount || b >= vertexCount || c >= vertexCount) continue;
                    if (i % 2 == 0) indices.AddRange([start + a, start + b, start + c]);
                    else indices.AddRange([start + a, start + c, start + b]);
                }
                at += length;
            }
            // What the strips leave is a plain triangle list.
            for (var i = at; i + 2 < indexCount; i += 3)
            {
                int a = chunkIndices[i], b = chunkIndices[i + 1], c = chunkIndices[i + 2];
                if (a < vertexCount && b < vertexCount && c < vertexCount) indices.AddRange([start + a, start + b, start + c]);
            }
            r.Skip(2 * r.Int()); // welding
        }
    }

    /// <summary>
    /// A piece hull builders take without trouble: points closer than a hundredth of a unit merged, and a piece with no real
    /// thickness (a cloth sheet) made a thin box around its points — Bepu's hull builder crashes on nearly flat sets.
    /// </summary>
    public static Vector3[] SafePiece(Vector3[] points)
    {
        var merged = points.GroupBy(p => (MathF.Round(p.X * 100), MathF.Round(p.Y * 100), MathF.Round(p.Z * 100))).Select(g => g.First()).ToArray();
        var low = merged.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var high = merged.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        var size = (high - low).Length();
        if (merged.Length >= 4 && Thickness(merged) > MathF.Max(0.5f, size * 0.03f)) return merged;
        var grow = new Vector3(0.5f);
        (low, high) = (low - grow, high + grow);
        return [new(low.X, low.Y, low.Z), new(high.X, low.Y, low.Z), new(low.X, high.Y, low.Z), new(high.X, high.Y, low.Z),
            new(low.X, low.Y, high.Z), new(high.X, low.Y, high.Z), new(low.X, high.Y, high.Z), new(high.X, high.Y, high.Z)];
    }

    /// <summary>How far the points stand off the plane through three far-apart ones (the smallest is not sought: enough to spot flat sets).</summary>
    private static float Thickness(Vector3[] points)
    {
        var a = points[0];
        var b = points.MaxBy(p => Vector3.DistanceSquared(p, a));
        var ab = b - a;
        if (ab.LengthSquared() < 1e-8f) return 0;
        var c = points.MaxBy(p => Vector3.Cross(ab, p - a).LengthSquared());
        var normal = Vector3.Cross(ab, c - a);
        if (normal.LengthSquared() < 1e-10f) return 0;
        normal = Vector3.Normalize(normal);
        var d = points.Select(p => Vector3.Dot(normal, p - a)).ToArray();
        return d.Max() - d.Min();
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
