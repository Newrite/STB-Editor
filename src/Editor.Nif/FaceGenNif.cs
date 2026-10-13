using System.Numerics;

namespace StbEditor.Nif;

/// <summary>One head part going into a FaceGen head: its mesh file and what the NPC changes in it.</summary>
public sealed record FaceGenPart
{
    /// <summary>Shape name in the FaceGen file: the head part's EditorID (the game finds parts by it).</summary>
    public required string Name { get; init; }

    /// <summary>The head part's NIF.</summary>
    public required NifDocument Source { get; init; }

    /// <summary>Morphed vertex positions (skin space, in the mesh's vertex order); null keeps the mesh's own.</summary>
    public Vector3[]? Positions { get; init; }

    /// <summary>Texture slots that replace the mesh's (null or empty entries keep the mesh's path).</summary>
    public string?[]? Textures { get; init; }

    /// <summary>The texture slots are written as given even when they name the mesh's own file (the face's head texture set).</summary>
    public bool TexturesAsGiven { get; init; }

    /// <summary>Tint colour for hair / skin tint shaders (0–1, the shader's float colour).</summary>
    public Vector3? TintColor { get; init; }
}

/// <summary>
/// Builds a FaceGen head NIF the way the Creation Kit lays it out: a BSFadeNode named after the file, the skeleton's
/// bones the parts are skinned to, and a "BSFaceGenNiNodeSkinned" node holding every head part's shape. Each part's
/// shape, skin, skin data, partition, shader, texture set and alpha are its own mesh's blocks, with the morphed
/// positions written into the dynamic vertex block and the skin partition's vertex buffer.
/// </summary>
public static class FaceGenNif
{
    public static byte[] Build(string fileName, IReadOnlyList<FaceGenPart> parts)
    {
        var doc = new NifDocument();
        doc.Add("BSFadeNode", []); // filled in last: its children are known then
        var bones = new Dictionary<string, int>(StringComparer.Ordinal);
        var shapes = new List<int>();

        // Bones first (as the CK does): every bone any part is skinned to, with the part mesh's bind transform.
        foreach (var part in parts)
        {
            foreach (var bone in BonesOf(part.Source))
            {
                if (bones.ContainsKey(bone.Name)) continue;
                bones[bone.Name] = doc.Add("NiNode", Node(doc, bone.Name, bone.Flags, bone.Transform, []));
            }
        }
        var faceGenNode = doc.Add("NiNode", []);

        foreach (var part in parts)
        {
            var shape = AddPart(doc, part, bones);
            if (shape >= 0) shapes.Add(shape);
        }

        doc.Blocks[faceGenNode] = ("NiNode", Node(doc, "BSFaceGenNiNodeSkinned", 14, Identity, shapes));
        doc.Blocks[0] = ("BSFadeNode", Node(doc, fileName, 14, Identity, [.. bones.Values, faceGenNode]));
        doc.Roots.Add(0);
        return doc.Write();
    }

    // ------------------------------------------------------------------ parts

    private static int AddPart(NifDocument doc, FaceGenPart part, Dictionary<string, int> bones)
    {
        var src = part.Source;
        var shapeIndex = src.Blocks.FindIndex(b => b.Type is "BSDynamicTriShape" or "BSTriShape");
        if (shapeIndex < 0) return -1;
        var shape = new Reader(src.Blocks[shapeIndex].Data);

        // NiObjectNET + NiAVObject.
        shape.Int(); // name
        shape.Skip(shape.Int() * 4); // extra data
        shape.Int(); // controller
        var flags = shape.UInt();
        var transform = shape.Bytes(13 * 4);
        shape.Int(); // collision
        shape.Skip(16); // bounding sphere (recomputed)
        var skinRef = shape.Int();
        var shaderRef = shape.Int();
        var alphaRef = shape.Int();
        var geometryStart = shape.Position; // vertex desc … to the dynamic block: copied as it is
        var desc = shape.ULong();
        var triangles = shape.UShort();
        var vertices = shape.UShort();
        var dataSize = shape.UInt();
        if (dataSize > 0) throw new NotSupportedException($"{part.Name}: unskinned or LE head part mesh (vertex data in the shape)");
        if (src.BsVersion == 100)
        {
            var particles = shape.UInt();
            if (particles > 0) shape.Skip(vertices * 12 + triangles * 6);
        }
        var geometryEnd = shape.Position;
        var dynamic = src.Blocks[shapeIndex].Type == "BSDynamicTriShape";
        var dynamicCount = 0;
        float[]? dynamicW = null;
        if (dynamic)
        {
            dynamicCount = (int)(shape.UInt() / 16);
            dynamicW = new float[dynamicCount];
            for (var i = 0; i < dynamicCount; i++) { shape.Skip(12); dynamicW[i] = shape.Float(); }
        }

        // Skin, skin data, partition.
        if (skinRef < 0 || skinRef >= src.Blocks.Count) throw new InvalidDataException($"{part.Name}: no skin");
        var (skinType, skinData) = src.Blocks[skinRef];
        var skin = new Reader(skinData);
        var dataRef = skin.Int();
        var partitionRef = skin.Int();
        skin.Int(); // skeleton root
        var boneRefs = new int[skin.Int()];
        for (var i = 0; i < boneRefs.Length; i++) boneRefs[i] = skin.Int();
        var skinTail = skin.Bytes(skinData.Length - skin.Position); // dismember partitions

        var original = PartitionPositions(src.Blocks[partitionRef].Data, src.BsVersion);
        var positions = part.Positions ?? original;
        if (positions.Length != vertices) throw new InvalidDataException($"{part.Name}: {positions.Length} positions for {vertices} vertices");

        // Shape first, then its skin, data, partition, shader, texture set, alpha (the CK's order).
        var shapeOut = doc.Add(src.Blocks[shapeIndex].Type, []);
        var skinOut = doc.Add(skinType, []);
        var dataOut = doc.Add("NiSkinData", src.Blocks[dataRef].Data);
        var partitionOut = doc.Add("NiSkinPartition", PatchPartition(src.Blocks[partitionRef].Data, src.BsVersion, positions));

        var shaderOut = -1;
        if (shaderRef >= 0 && shaderRef < src.Blocks.Count && src.Blocks[shaderRef].Type == "BSLightingShaderProperty")
        {
            shaderOut = doc.Add("BSLightingShaderProperty", []);
            var (shaderBytes, textureRef) = Shader(doc, src.Blocks[shaderRef].Data, src.BsVersion, part.TintColor);
            var textures = textureRef >= 0 && textureRef < src.Blocks.Count ? TextureSet(src.Blocks[textureRef].Data) : [];
            var setOut = doc.Add("BSShaderTextureSet", TextureSetBytes(Merge(textures, part.Textures, part.TexturesAsGiven)));
            doc.Blocks[shaderOut] = ("BSLightingShaderProperty", WithTextureSet(shaderBytes, src.BsVersion, setOut));
        }
        var alphaOut = -1;
        if (alphaRef >= 0 && alphaRef < src.Blocks.Count && src.Blocks[alphaRef].Type == "NiAlphaProperty")
        {
            var alpha = new Reader(src.Blocks[alphaRef].Data);
            alpha.Int();
            alpha.Skip(alpha.Int() * 4);
            alpha.Int();
            var w = new Writer();
            w.Int(-1); w.Int(0); w.Int(-1);
            w.Bytes(alpha.Bytes(3)); // flags, threshold
            alphaOut = doc.Add("NiAlphaProperty", w.ToArray());
        }

        // The shape with its new name, links and bounds, and the morphed positions in the dynamic block.
        var s = new Writer();
        s.Int(doc.String(part.Name));
        s.Int(0);
        s.Int(-1);
        s.UInt(flags);
        s.Bytes(transform);
        s.Int(-1);
        var (center, radius) = Bounds(positions);
        s.Vec3(center); s.Float(radius);
        s.Int(skinOut); s.Int(shaderOut); s.Int(alphaOut);
        s.Bytes(src.Blocks[shapeIndex].Data.AsSpan(geometryStart, geometryEnd - geometryStart));
        if (dynamic)
        {
            s.UInt((uint)(vertices * 16));
            for (var i = 0; i < vertices; i++) { s.Vec3(positions[i]); s.Float(i < dynamicCount ? dynamicW![i] : 0f); }
        }
        doc.Blocks[shapeOut] = (src.Blocks[shapeIndex].Type, s.ToArray());

        // The skin with links into this file: data, partition, the root as skeleton root, the bones by name.
        var k = new Writer();
        k.Int(dataOut); k.Int(partitionOut); k.Int(0);
        k.Int(boneRefs.Length);
        foreach (var bone in boneRefs)
        {
            var name = bone >= 0 && bone < src.Blocks.Count ? src.StringAt(BitConverter.ToInt32(src.Blocks[bone].Data, 0)) : null;
            k.Int(name is not null && bones.TryGetValue(name, out var mapped) ? mapped : -1);
        }
        k.Bytes(skinTail);
        doc.Blocks[skinOut] = (skinType, k.ToArray());
        _ = desc;
        return shapeOut;
    }

    /// <summary>Bones a mesh is skinned to: the nodes its shapes' skins name, with their flags and transforms.</summary>
    private static IEnumerable<(string Name, uint Flags, byte[] Transform)> BonesOf(NifDocument src)
    {
        foreach (var (type, data) in src.Blocks)
        {
            if (type is not ("NiSkinInstance" or "BSDismemberSkinInstance")) continue;
            var skin = new Reader(data);
            skin.Skip(12);
            var count = skin.Int();
            for (var i = 0; i < count; i++)
            {
                var bone = skin.Int();
                if (bone < 0 || bone >= src.Blocks.Count) continue;
                var node = new Reader(src.Blocks[bone].Data);
                var name = src.StringAt(node.Int());
                node.Skip(node.Int() * 4);
                node.Int();
                var flags = node.UInt();
                var transform = node.Bytes(13 * 4);
                if (name is not null) yield return (name, flags, transform);
            }
        }
    }

    // ------------------------------------------------------------------ skin partition

    /// <summary>
    /// Vertex positions of an SSE skin partition's shared vertex buffer (half floats unless the vertex format says full
    /// precision); none for head part meshes, whose buffer has no positions: the dynamic block holds them.
    /// </summary>
    public static Vector3[] PartitionPositions(byte[] data, uint bsVersion) => PartitionBuffer(data, bsVersion, null);

    private static byte[] PatchPartition(byte[] data, uint bsVersion, Vector3[] positions)
    {
        var copy = (byte[])data.Clone();
        PartitionBuffer(copy, bsVersion, positions);
        return copy;
    }

    /// <summary>Reads the buffer's positions, or writes <paramref name="write"/> into it (same precision).</summary>
    private static Vector3[] PartitionBuffer(byte[] data, uint bsVersion, Vector3[]? write)
    {
        if (bsVersion != 100) return [];
        var r = new Reader(data);
        r.Int(); // partitions
        var dataSize = r.Int();
        var vertexSize = r.Int();
        var desc = r.ULong();
        if (dataSize <= 0 || vertexSize <= 0) return [];
        // BSDynamicTriShape buffers carry no positions at all (no Vertex attribute): UVs come first.
        if (((desc >> 44) & 0x1) == 0) return [];
        var full = ((desc >> 44) & 0x400) != 0;
        var start = r.Position;
        var result = new Vector3[dataSize / vertexSize];
        for (var i = 0; i < result.Length; i++)
        {
            var at = start + i * vertexSize;
            if (write is not null && i < write.Length)
            {
                if (full)
                {
                    BitConverter.TryWriteBytes(data.AsSpan(at), write[i].X);
                    BitConverter.TryWriteBytes(data.AsSpan(at + 4), write[i].Y);
                    BitConverter.TryWriteBytes(data.AsSpan(at + 8), write[i].Z);
                }
                else
                {
                    BitConverter.TryWriteBytes(data.AsSpan(at), (Half)write[i].X);
                    BitConverter.TryWriteBytes(data.AsSpan(at + 2), (Half)write[i].Y);
                    BitConverter.TryWriteBytes(data.AsSpan(at + 4), (Half)write[i].Z);
                }
            }
            result[i] = full
                ? new Vector3(BitConverter.ToSingle(data, at), BitConverter.ToSingle(data, at + 4), BitConverter.ToSingle(data, at + 8))
                : new Vector3((float)BitConverter.ToHalf(data, at), (float)BitConverter.ToHalf(data, at + 2), (float)BitConverter.ToHalf(data, at + 4));
        }
        return result;
    }

    // ------------------------------------------------------------------ shader and textures

    /// <summary>The shader without its name and extra data, the tint colour set; the texture set link found.</summary>
    private static (byte[] Bytes, int TextureSet) Shader(NifDocument doc, byte[] data, uint bsVersion, Vector3? tint)
    {
        var r = new Reader(data);
        var w = new Writer();
        var type = 0u;
        if (bsVersion < 130) { type = r.UInt(); w.UInt(type); }
        r.Int();
        r.Skip(r.Int() * 4);
        r.Int();
        w.Int(-1); w.Int(0); w.Int(-1);
        w.Bytes(r.Bytes(4 + 4 + 8 + 8)); // flags 1 and 2, UV offset and scale
        var textureSet = r.Int();
        w.Int(-1); // texture set: set by WithTextureSet
        var rest = r.Bytes(data.Length - r.Position);
        // Hair tint (6) and skin tint (5) shaders end the common part with their colour.
        const int common = 12 + 4 + 4 + 4 + 4 + 4 + 12 + 4 + 4 + 4;
        if (tint is { } c && type is 5 or 6 && rest.Length >= common + 12)
        {
            BitConverter.TryWriteBytes(rest.AsSpan(common), c.X);
            BitConverter.TryWriteBytes(rest.AsSpan(common + 4), c.Y);
            BitConverter.TryWriteBytes(rest.AsSpan(common + 8), c.Z);
        }
        w.Bytes(rest);
        _ = doc;
        return (w.ToArray(), textureSet);
    }

    private static byte[] WithTextureSet(byte[] shader, uint bsVersion, int textureSet)
    {
        var at = (bsVersion < 130 ? 4 : 0) + 12 + 24;
        BitConverter.TryWriteBytes(shader.AsSpan(at), textureSet);
        return shader;
    }

    private static string[] TextureSet(byte[] data)
    {
        var r = new Reader(data);
        var result = new string[r.Int()];
        for (var i = 0; i < result.Length; i++) result[i] = System.Text.Encoding.ASCII.GetString(r.Bytes(r.Int()));
        return result;
    }

    private static string[] Merge(string[] mesh, string?[]? overrides, bool asGiven)
    {
        var count = Math.Max(Math.Max(mesh.Length, overrides?.Length ?? 0), 9);
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            var own = i < mesh.Length ? mesh[i] : "";
            var given = overrides is not null && i < overrides.Length ? overrides[i] : null;
            // The CK keeps the mesh's spelling when the texture set names the same file.
            // The face's head texture set is written as it is, but for the specular slot (7), like the rest.
            result[i] = string.IsNullOrEmpty(given) || (!(asGiven && i < 7) && SameTexture(given, own)) ? own : given;
        }
        return result;
    }

    private static bool SameTexture(string a, string b)
    {
        static string Key(string p)
        {
            p = p.Replace('/', '\\').ToLowerInvariant();
            if (p.StartsWith("data\\", StringComparison.Ordinal)) p = p[5..];
            return p.StartsWith("textures\\", StringComparison.Ordinal) ? p[9..] : p;
        }
        return b.Length > 0 && Key(a) == Key(b);
    }

    private static byte[] TextureSetBytes(string[] textures)
    {
        var w = new Writer();
        w.Int(textures.Length);
        foreach (var t in textures)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(t);
            w.Int(bytes.Length);
            w.Bytes(bytes);
        }
        return w.ToArray();
    }

    // ------------------------------------------------------------------ helpers

    private static readonly byte[] Identity = IdentityTransform();

    private static byte[] IdentityTransform()
    {
        var w = new Writer();
        w.Vec3(Vector3.Zero);
        w.Float(1); w.Float(0); w.Float(0);
        w.Float(0); w.Float(1); w.Float(0);
        w.Float(0); w.Float(0); w.Float(1);
        w.Float(1);
        return w.ToArray();
    }

    private static byte[] Node(NifDocument doc, string name, uint flags, byte[] transform, IReadOnlyList<int> children)
    {
        var w = new Writer();
        w.Int(doc.String(name));
        w.Int(0);
        w.Int(-1);
        w.UInt(flags);
        w.Bytes(transform);
        w.Int(-1); // collision
        w.Int(children.Count);
        foreach (var child in children) w.Int(child);
        w.Int(0); // effects
        return w.ToArray();
    }

    private static (Vector3 Center, float Radius) Bounds(Vector3[] positions)
    {
        if (positions.Length == 0) return (Vector3.Zero, 0);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        var center = (min + max) / 2;
        var radius = positions.Max(p => Vector3.Distance(p, center));
        return (center, radius);
    }

    private sealed class Reader(byte[] data)
    {
        public int Position { get; private set; }
        public int Int() { var v = BitConverter.ToInt32(data, Position); Position += 4; return v; }
        public uint UInt() { var v = BitConverter.ToUInt32(data, Position); Position += 4; return v; }
        public ushort UShort() { var v = BitConverter.ToUInt16(data, Position); Position += 2; return v; }
        public ulong ULong() { var v = BitConverter.ToUInt64(data, Position); Position += 8; return v; }
        public float Float() { var v = BitConverter.ToSingle(data, Position); Position += 4; return v; }
        public void Skip(int count) => Position += count;
        public byte[] Bytes(int count) { var v = data.AsSpan(Position, count).ToArray(); Position += count; return v; }
    }

    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _w;
        public Writer() => _w = new BinaryWriter(_stream);
        public void Int(int v) => _w.Write(v);
        public void UInt(uint v) => _w.Write(v);
        public void Float(float v) => _w.Write(v);
        public void Vec3(Vector3 v) { _w.Write(v.X); _w.Write(v.Y); _w.Write(v.Z); }
        public void Bytes(ReadOnlySpan<byte> v) => _w.Write(v);
        public byte[] ToArray() { _w.Flush(); return _stream.ToArray(); }
    }
}
