using System.Numerics;
using System.Text;

namespace StbEditor.Nif;

/// <summary>
/// Reads Skyrim NIFs (20.2.0.7: SSE BS version 100, LE 83) far enough to render them: the node
/// tree with transforms, BSTriShape and NiTriShape/NiTriStrips geometry, lighting/effect shaders,
/// texture sets and alpha. Every other block is skipped by its size from the header, so unknown
/// or broken blocks cost that block, not the file.
/// </summary>
public static partial class NifFile
{
    private const uint Version20207 = 0x14020007;

    private sealed class Header
    {
        public uint Version;
        public uint UserVersion;
        public uint BsVersion;
        public string[] BlockTypes = [];
        public ushort[] BlockTypeIndex = [];
        public uint[] BlockSizes = [];
        public int[] BlockOffsets = [];
        public string[] Strings = [];

        public string TypeOf(int block) => BlockTypes[BlockTypeIndex[block] & 0x7FFF];
    }

    private sealed class Context(byte[] data, Header header, NifModel model)
    {
        public readonly byte[] Data = data;
        public readonly Header Header = header;
        public readonly NifModel Model = model;
        public readonly HashSet<int> Visited = [];

        /// <summary>Model-space transform of every walked node (emitter / gravity objects).</summary>
        public readonly Dictionary<int, NifTransform> NodeWorld = new();
        public readonly Dictionary<int, NifShape> ShapeByBlock = new();
        public readonly List<(int Index, NifTransform Parent, bool Hidden, bool Marker)> PendingParticles = [];

        public bool IsBlock(int index) => index >= 0 && index < Header.BlockSizes.Length;

        public ReadOnlySpan<byte> Block(int index) =>
            Data.AsSpan(Header.BlockOffsets[index], (int)Header.BlockSizes[index]);

        public string? String(int index) =>
            index >= 0 && index < Header.Strings.Length ? Header.Strings[index] : null;
    }

    public static NifModel Load(byte[] data, string path)
    {
        var header = ReadHeader(data, out var blocksStart);
        var model = new NifModel { Path = path, Version = header.Version, BsVersion = header.BsVersion };

        var offset = blocksStart;
        header.BlockOffsets = new int[header.BlockSizes.Length];
        for (var i = 0; i < header.BlockSizes.Length; i++)
        {
            header.BlockOffsets[i] = offset;
            offset += (int)header.BlockSizes[i];
        }

        if (offset > data.Length) throw new InvalidDataException("NIF blocks run past the end of the file");

        // Oblivion/FO3-era leftovers in vanilla archives (BS 11–34) are never drawn by Skyrim.
        if (header.BsVersion < 83)
        {
            model.Warnings.Add($"pre-Skyrim mesh (BS version {header.BsVersion}), not rendered");
            return model;
        }

        var context = new Context(data, header, model);
        var roots = ReadRoots(data, offset, header.BlockSizes.Length);

        foreach (var root in roots)
            PrecomputeNodes(context, root, NifTransform.Identity, 0);
        foreach (var root in roots)
            Walk(context, root, NifTransform.Identity, hidden: false, editorMarker: false, depth: 0, billboard: null);

        // Particle systems last: their emitter and gravity objects can be anywhere in the tree.
        foreach (var (index, parent, hidden, marker) in context.PendingParticles)
        {
            try
            {
                ReadParticleSystem(context, index, parent, hidden, marker);
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
            {
                model.Warnings.Add($"block {index} particles: {ex.Message}");
            }
        }

        foreach (var shape in model.Shapes)
        {
            model.BoundsMin = Vector3.Min(model.BoundsMin, shape.BoundsMin);
            model.BoundsMax = Vector3.Max(model.BoundsMax, shape.BoundsMax);
        }

        foreach (var particles in model.Particles)
        {
            if (particles.IsHidden || particles.IsEditorMarker) continue;
            model.BoundsMin = Vector3.Min(model.BoundsMin, particles.BoundsMin);
            model.BoundsMax = Vector3.Max(model.BoundsMax, particles.BoundsMax);
        }

        return model;
    }

    private static Header ReadHeader(byte[] data, out int end)
    {
        var newline = Array.IndexOf(data, (byte)'\n', 0, Math.Min(data.Length, 128));
        if (newline < 0) throw new InvalidDataException("not a NIF file");

        var line = Encoding.ASCII.GetString(data, 0, newline);
        if (!line.StartsWith("Gamebryo File Format", StringComparison.Ordinal) && !line.StartsWith("NetImmerse File Format", StringComparison.Ordinal))
            throw new InvalidDataException("not a NIF file");

        var r = new BlockReader(data.AsSpan(newline + 1));
        var header = new Header { Version = r.UInt() };
        if (header.Version != Version20207)
            throw new NotSupportedException($"NIF version 0x{header.Version:X8} (only 20.2.0.7 Skyrim meshes are supported)");

        if (r.Byte() != 1) throw new NotSupportedException("big-endian NIF");
        header.UserVersion = r.UInt();
        var blockCount = r.Int();

        header.BsVersion = r.UInt();
        ShortString(ref r); // author
        if (header.BsVersion > 130) r.UInt();
        ShortString(ref r); // process script
        ShortString(ref r); // export script
        if (header.BsVersion >= 103) ShortString(ref r); // max filepath

        var typeCount = r.UShort();
        header.BlockTypes = new string[typeCount];
        for (var i = 0; i < typeCount; i++) header.BlockTypes[i] = r.SizedString();

        header.BlockTypeIndex = new ushort[blockCount];
        for (var i = 0; i < blockCount; i++) header.BlockTypeIndex[i] = r.UShort();

        header.BlockSizes = new uint[blockCount];
        for (var i = 0; i < blockCount; i++) header.BlockSizes[i] = r.UInt();

        var stringCount = r.Int();
        r.UInt(); // max string length
        header.Strings = new string[stringCount];
        for (var i = 0; i < stringCount; i++) header.Strings[i] = r.SizedString();

        var groupCount = r.Int();
        r.Skip(groupCount * 4);

        end = newline + 1 + r.Position;
        return header;
    }

    private static void ShortString(ref BlockReader r)
    {
        var length = r.Byte();
        r.Skip(length);
    }

    private static int[] ReadRoots(byte[] data, int footer, int blockCount)
    {
        if (footer + 4 <= data.Length)
        {
            var r = new BlockReader(data.AsSpan(footer));
            var count = r.Int();
            if (count > 0 && count <= blockCount && r.Remaining >= count * 4)
            {
                var roots = new int[count];
                for (var i = 0; i < count; i++) roots[i] = r.Int();
                return roots;
            }
        }

        return blockCount > 0 ? [0] : [];
    }

    // ---------------------------------------------------------------- node tree

    /// <summary>Inside an NiBillboardNode: shapes keep the node's frame and turn to the camera.</summary>
    private sealed record BillboardFrame(BillboardMode Mode, Vector3 Origin, Vector3 Up);

    private static NifTransform ModelSpace(NifTransform t, BillboardFrame? billboard) =>
        billboard is null ? t : new NifTransform(t.Rotation, billboard.Origin + t.Translation, t.Scale);

    private static void Walk(Context ctx, int index, NifTransform parent, bool hidden, bool editorMarker, int depth, BillboardFrame? billboard)
    {
        if (!ctx.IsBlock(index) || depth > 64 || !ctx.Visited.Add(index)) return;

        var type = ctx.Header.TypeOf(index);
        try
        {
            if (IsGeometry(type))
            {
                ReadGeometry(ctx, index, type, parent, hidden, editorMarker, billboard);
                return;
            }

            if (type is "NiParticleSystem" or "BSStripParticleSystem")
            {
                ctx.PendingParticles.Add((index, ModelSpace(parent, billboard), hidden, editorMarker));
                return;
            }

            if (!IsNode(type)) return;

            var r = new BlockReader(ctx.Block(index));
            var av = ReadAvObject(ctx, ref r);
            var world = parent.Then(av.Transform);
            var name = av.Name ?? string.Empty;
            ctx.NodeWorld[index] = ModelSpace(world, billboard);

            // Collision-only node trees and LE "RootCollisionNode" are not visible geometry.
            if (type == "RootCollisionNode") return;

            var childHidden = hidden || av.Hidden;
            var childMarker = editorMarker || name.Equals("EditorMarker", StringComparison.OrdinalIgnoreCase);

            var children = r.Refs();
            if (ctx.Header.BsVersion < 130) r.Refs(); // effects

            // Switch and LOD nodes: only the active / most detailed child is drawn.
            if (type == "NiSwitchNode" && r.Remaining >= 6)
            {
                r.UShort();
                var active = r.Int();
                if (active >= 0 && active < children.Length)
                    Walk(ctx, children[active], world, childHidden, childMarker, depth + 1, billboard);
                return;
            }

            if (type == "NiLODNode" && children.Length > 0)
            {
                Walk(ctx, children[0], world, childHidden, childMarker, depth + 1, billboard);
                return;
            }

            // Billboards replace the node's rotation with one facing the camera: children are
            // kept in the node's own (scaled) frame around its model-space position.
            if (type == "NiBillboardNode" && billboard is null && r.Remaining >= 2)
            {
                var mode = r.UShort() & 0xF;
                var up = world.Rotation.Transform(mode == 5 ? Vector3.UnitZ : Vector3.UnitY);
                var frame = new BillboardFrame(mode is 1 or 5 or 9 ? BillboardMode.RotateAboutUp : BillboardMode.FaceCamera, world.Translation,
                    up.LengthSquared() > 1e-8f ? Vector3.Normalize(up) : Vector3.UnitZ);
                var local = new NifTransform(Matrix3.Identity, Vector3.Zero, world.Scale);
                foreach (var child in children)
                    Walk(ctx, child, local, childHidden, childMarker, depth + 1, frame);
                return;
            }

            foreach (var child in children)
                Walk(ctx, child, world, childHidden, childMarker, depth + 1, billboard);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            ctx.Model.Warnings.Add($"block {index} {type}: {ex.Message}");
        }
    }

    /// <summary>Model-space transform of every node, so skinned shapes can find their bones.</summary>
    private static void PrecomputeNodes(Context ctx, int index, NifTransform parent, int depth)
    {
        if (!ctx.IsBlock(index) || depth > 64 || ctx.NodeWorld.ContainsKey(index)) return;
        var type = ctx.Header.TypeOf(index);
        if (!IsNode(type)) return;
        try
        {
            var r = new BlockReader(ctx.Block(index));
            var av = ReadAvObject(ctx, ref r);
            var world = parent.Then(av.Transform);
            ctx.NodeWorld[index] = world;
            foreach (var child in r.Refs())
                PrecomputeNodes(ctx, child, world, depth + 1);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
        }
    }

    /// <summary>
    /// Skinned vertices are in skin space. Puts them where the file's bone nodes hold them
    /// (linear blend of bone node × the bone's skin transform, weights from NiSkinData or from the
    /// skin partitions). Returns false when the bones are not in this file — the caller then uses
    /// the shape's own transform.
    /// </summary>
    private static bool ApplySkin(Context ctx, int skinRef, RawGeometry g)
    {
        if (!ctx.IsBlock(skinRef) || ctx.Header.TypeOf(skinRef) is not ("NiSkinInstance" or "BSDismemberSkinInstance")) return false;
        var r = new BlockReader(ctx.Block(skinRef));
        var dataRef = r.Int();
        var partitionRef = r.Int();
        r.Int(); // skeleton root
        var bones = r.Refs();
        if (bones.Length == 0 || !ctx.IsBlock(dataRef) || ctx.Header.TypeOf(dataRef) != "NiSkinData") return false;

        var d = new BlockReader(ctx.Block(dataRef));
        d.Skip(52); // overall skin transform (root parent → skin)
        var boneCount = Math.Min(d.Int(), bones.Length);
        var hasWeights = d.Bool();
        if (boneCount <= 0) return false;

        var matrices = new NifTransform?[boneCount];
        var weights = new List<(int Vertex, int Bone, float Weight)>();
        for (var b = 0; b < boneCount; b++)
        {
            var skin = new NifTransform(d.Matrix33(), d.Vec3(), d.Float());
            if (ctx.NodeWorld.TryGetValue(bones[b], out var node)) matrices[b] = node.Then(skin);
            d.Skip(16); // bounding sphere
            int count = d.UShort();
            if (!hasWeights) continue;
            for (var i = 0; i < count; i++)
            {
                int vertex = d.UShort();
                var weight = d.Float();
                if (weight > 0) weights.Add((vertex, b, weight));
            }
        }

        // SSE keeps the weights in the partitions (and the vertex buffer) instead.
        if (weights.Count == 0 && ctx.IsBlock(partitionRef) && ctx.Header.TypeOf(partitionRef) == "NiSkinPartition")
            ReadPartitionWeights(ctx, partitionRef, weights);
        if (weights.Count == 0 || matrices.All(m => m is null)) return false;

        var count3 = g.Positions.Length;
        var positions = new Vector3[count3];
        var normals = g.Normals is null ? null : new Vector3[count3];
        var tangents = g.Tangents is null ? null : new Vector3[count3];
        var bitangents = g.Bitangents is null ? null : new Vector3[count3];
        var total = new float[count3];

        foreach (var (vertex, bone, weight) in weights)
        {
            if (vertex >= count3 || bone >= matrices.Length || matrices[bone] is not { } m) continue;
            positions[vertex] += m.Apply(g.Positions[vertex]) * weight;
            if (normals is not null) normals[vertex] += m.Rotation.Transform(g.Normals![vertex]) * weight;
            if (tangents is not null) tangents[vertex] += m.Rotation.Transform(g.Tangents![vertex]) * weight;
            if (bitangents is not null) bitangents[vertex] += m.Rotation.Transform(g.Bitangents![vertex]) * weight;
            total[vertex] += weight;
        }

        // Unweighted vertices (or ones on missing bones) follow the first bone that exists.
        var fallback = matrices.First(m => m is not null)!.Value;
        for (var i = 0; i < count3; i++)
        {
            if (total[i] > 1e-4f)
            {
                positions[i] /= total[i];
                continue;
            }
            positions[i] = fallback.Apply(g.Positions[i]);
            if (normals is not null) normals[i] = fallback.Rotation.Transform(g.Normals![i]);
            if (tangents is not null) tangents[i] = fallback.Rotation.Transform(g.Tangents![i]);
            if (bitangents is not null) bitangents[i] = fallback.Rotation.Transform(g.Bitangents![i]);
        }

        g.Positions = positions;
        g.Normals = Normalized(normals);
        g.Tangents = Normalized(tangents);
        g.Bitangents = Normalized(bitangents);
        return true;
    }

    private static Vector3[]? Normalized(Vector3[]? vectors)
    {
        if (vectors is null) return null;
        for (var i = 0; i < vectors.Length; i++)
        {
            var length = vectors[i].Length();
            vectors[i] = length > 1e-6f ? vectors[i] / length : Vector3.UnitZ;
        }
        return vectors;
    }

    /// <summary>Per-partition vertex weights and bone indices, mapped to global vertices and skin bones.</summary>
    private static void ReadPartitionWeights(Context ctx, int partitionRef, List<(int Vertex, int Bone, float Weight)> weights)
    {
        var r = new BlockReader(ctx.Block(partitionRef));
        var partitionCount = r.Int();
        if (partitionCount is < 0 or > 4096) return;
        if (ctx.Header.BsVersion == 100)
        {
            var dataSize = r.Int();
            r.Int(); // vertex size
            r.ULong(); // desc
            r.Skip(dataSize);
        }

        for (var p = 0; p < partitionCount; p++)
        {
            int vertices = r.UShort();
            int triangles = r.UShort();
            int boneCount = r.UShort();
            int strips = r.UShort();
            int perVertex = r.UShort();
            var bones = new int[boneCount];
            for (var i = 0; i < boneCount; i++) bones[i] = r.UShort();

            var map = new int[vertices];
            var hasMap = r.Bool();
            for (var i = 0; i < vertices; i++) map[i] = hasMap ? r.UShort() : i;

            float[]? vertexWeights = null;
            if (r.Bool())
            {
                vertexWeights = new float[vertices * perVertex];
                for (var i = 0; i < vertexWeights.Length; i++) vertexWeights[i] = r.Float();
            }

            var stripTotal = 0;
            for (var i = 0; i < strips; i++) stripTotal += r.UShort();
            if (r.Bool()) r.Skip(strips > 0 ? stripTotal * 2 : triangles * 6);

            byte[]? boneIndices = null;
            if (r.Bool())
            {
                boneIndices = new byte[vertices * perVertex];
                for (var i = 0; i < boneIndices.Length; i++) boneIndices[i] = r.Byte();
            }
            if (ctx.Header.BsVersion > 34) r.Skip(2); // LOD level, global VB
            if (ctx.Header.BsVersion == 100) r.Skip(8 + triangles * 6); // desc, triangles copy

            if (vertexWeights is null || boneIndices is null) continue;
            for (var v = 0; v < vertices; v++)
                for (var k = 0; k < perVertex; k++)
                {
                    var weight = vertexWeights[v * perVertex + k];
                    var local = boneIndices[v * perVertex + k];
                    if (weight > 0 && local < bones.Length) weights.Add((map[v], bones[local], weight));
                }
        }
    }

    private static bool IsNode(string type) => type switch
    {
        "NiNode" or "BSFadeNode" or "BSLeafAnimNode" or "BSTreeNode" or "BSMultiBoundNode" or "BSOrderedNode"
            or "BSValueNode" or "BSBlastNode" or "BSDamageStage" or "BSDebrisNode" or "NiSwitchNode" or "NiLODNode"
            or "NiBillboardNode" or "BSRangeNode" or "AvoidNode" or "NiBone" or "BSFaceGenNiNode" or "NiSortAdjustNode"
            or "BSNiNode" or "BSWeakReferenceNode" or "BSMasterParticleSystem" or "NiRoomGroup" or "NiRoom"
            or "RootCollisionNode" => true,
        _ => false,
    };

    private static bool IsGeometry(string type) => type switch
    {
        "BSTriShape" or "BSSubIndexTriShape" or "BSMeshLODTriShape" or "BSLODTriShape" or "BSDynamicTriShape"
            or "NiTriShape" or "NiTriStrips" or "BSSegmentedTriShape" => true,
        _ => false,
    };

    private readonly record struct AvObject(string? Name, NifTransform Transform, bool Hidden, int[] ExtraData, int Controller, int Collision);

    /// <summary>NiObjectNET + NiAVObject fields (Skyrim: 32-bit flags, no property list).</summary>
    private static AvObject ReadAvObject(Context ctx, ref BlockReader r)
    {
        var name = ctx.String(r.Int());
        var extra = r.Refs();
        var controller = r.Int();

        var flags = ctx.Header.BsVersion > 26 ? r.UInt() : r.UShort();
        var translation = r.Vec3();
        var rotation = r.Matrix33();
        var scale = r.Float();
        if (ctx.Header.BsVersion <= 34) r.Refs(); // properties (pre-Skyrim)
        var collision = r.Int();

        return new AvObject(name, new NifTransform(rotation, translation, scale), (flags & 1) != 0, extra, controller, collision);
    }

    // ---------------------------------------------------------------- geometry

    private static void ReadGeometry(Context ctx, int index, string type, NifTransform parent, bool hidden, bool editorMarker, BillboardFrame? billboard)
    {
        var r = new BlockReader(ctx.Block(index));
        var av = ReadAvObject(ctx, ref r);
        var world = parent.Then(av.Transform);

        RawGeometry? geometry;
        int shaderRef, alphaRef;
        bool skinned;
        var skinRefForBind = -1;

        // BSLODTriShape and BSSegmentedTriShape are LE NiTriBasedGeom subclasses despite the prefix.
        if (type is "BSTriShape" or "BSSubIndexTriShape" or "BSMeshLODTriShape" or "BSDynamicTriShape")
        {
            r.Vec3(); // bound center
            r.Float(); // bound radius
            var skinRef = r.Int();
            shaderRef = r.Int();
            alphaRef = r.Int();
            skinned = skinRef >= 0;
            skinRefForBind = skinRef;
            geometry = ReadBsTriShape(ctx, ref r, type);
            if (skinned && (geometry is null || geometry.Indices.Length == 0)) geometry = ReadSkinPartition(ctx, skinRef, geometry);
        }
        else
        {
            var dataRef = r.Int();
            var skinRef = r.Int();
            // MaterialData (20.2.0.5+): names, extra data, active index, needs-update.
            var materials = r.Int();
            r.Skip(materials * 8);
            r.Int();
            r.Byte();
            shaderRef = r.Int();
            alphaRef = r.Int();
            skinned = skinRef >= 0;
            skinRefForBind = skinRef;
            geometry = ctx.IsBlock(dataRef) ? ReadNiGeometryData(ctx, dataRef) : null;
        }

        if (geometry is null || geometry.Positions.Length == 0 || geometry.Indices.Length == 0) return;

        // Skinned vertices are in skin space; put them where the bones hold them in the bind pose.
        if (skinned && billboard is null && ApplySkin(ctx, skinRefForBind, geometry)) world = NifTransform.Identity;

        var material = new NifMaterial();
        if (ctx.IsBlock(shaderRef)) ReadShader(ctx, shaderRef, material);
        if (ctx.IsBlock(alphaRef) && ctx.Header.TypeOf(alphaRef) == "NiAlphaProperty") ReadAlpha(ctx, alphaRef, material);

        // Bake the node transform: shapes render with the reference transform only, which is
        // what makes instancing of identical meshes possible.
        var positions = geometry.Positions;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i < positions.Length; i++)
        {
            var p = world.Apply(positions[i]);
            positions[i] = p;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        RotateAll(geometry.Normals, world.Rotation);
        RotateAll(geometry.Tangents, world.Rotation);
        RotateAll(geometry.Bitangents, world.Rotation);

        if (billboard is not null)
        {
            // Any facing of the card stays within its radius around the origin.
            var radius = 0f;
            foreach (var p in positions) radius = Math.Max(radius, p.Length());
            min = billboard.Origin - new Vector3(radius);
            max = billboard.Origin + new Vector3(radius);
        }

        var shape = new NifShape
        {
            Name = av.Name ?? $"shape{index}",
            Positions = positions,
            Normals = geometry.Normals,
            Tangents = geometry.Tangents,
            Bitangents = geometry.Bitangents,
            UVs = geometry.UVs,
            Colors = geometry.Colors,
            Indices = geometry.Indices,
            Material = material,
            IsHidden = hidden || av.Hidden,
            IsEditorMarker = editorMarker || (av.Name?.Contains("EditorMarker", StringComparison.OrdinalIgnoreCase) ?? false),
            IsSkinned = skinned,
            Billboard = billboard?.Mode,
            BillboardOrigin = billboard?.Origin ?? Vector3.Zero,
            BillboardUp = billboard?.Up ?? Vector3.UnitZ,
            BoundsMin = min,
            BoundsMax = max,
        };
        ctx.Model.Shapes.Add(shape);
        ctx.ShapeByBlock[index] = shape;
    }

    private static void RotateAll(Vector3[]? vectors, Matrix3 rotation)
    {
        if (vectors is null) return;
        for (var i = 0; i < vectors.Length; i++)
        {
            var v = rotation.Transform(vectors[i]);
            var length = v.Length();
            vectors[i] = length > 1e-6f ? v / length : Vector3.UnitZ;
        }
    }

    private sealed class RawGeometry
    {
        public Vector3[] Positions = [];
        public Vector3[]? Normals;
        public Vector3[]? Tangents;
        public Vector3[]? Bitangents;
        public Vector2[]? UVs;
        public uint[]? Colors;
        public uint[] Indices = [];
    }

    [Flags]
    private enum VertexAttribute : ushort
    {
        Vertex = 0x1,
        UV = 0x2,
        UV2 = 0x4,
        Normal = 0x8,
        Tangent = 0x10,
        Colors = 0x20,
        Skinned = 0x40,
        LandData = 0x80,
        EyeData = 0x100,
        Instance = 0x200,
        FullPrecision = 0x400,
    }

    /// <summary>SSE packed vertex format (BSVertexDesc); attributes follow in fixed order.</summary>
    private static RawGeometry? ReadBsTriShape(Context ctx, ref BlockReader r, string type)
    {
        var desc = r.ULong();
        int triangleCount = ctx.Header.BsVersion >= 130 ? r.Int() : r.UShort();
        int vertexCount = r.UShort();
        var dataSize = r.UInt();

        // Skinned SSE shapes keep their vertices in the NiSkinPartition instead (dataSize 0).
        if (dataSize == 0 && type != "BSDynamicTriShape") return null;

        var g = dataSize > 0 ? DecodeVertices(ctx, ref r, desc, vertexCount) : EmptyVertices(desc, vertexCount);
        if (dataSize > 0)
        {
            var indices = new uint[triangleCount * 3];
            for (var i = 0; i < indices.Length; i++) indices[i] = r.UShort();
            g.Indices = indices;
        }

        if (type == "BSDynamicTriShape")
        {
            // Facegen heads: positions live in the dynamic block that follows.
            if (ctx.Header.BsVersion == 100) SkipParticleData(ref r, vertexCount, triangleCount);
            var dynamicSize = r.UInt();
            var count = (int)(dynamicSize / 16);
            for (var i = 0; i < Math.Min(count, vertexCount); i++)
            {
                var v = r.Vec4();
                g.Positions[i] = new Vector3(v.X, v.Y, v.Z);
            }
        }

        return g;
    }

    private static RawGeometry EmptyVertices(ulong desc, int vertexCount)
    {
        var attributes = (VertexAttribute)((desc >> 44) & 0xFFF);
        return new RawGeometry
        {
            Positions = new Vector3[vertexCount],
            UVs = attributes.HasFlag(VertexAttribute.UV) ? new Vector2[vertexCount] : null,
            Normals = attributes.HasFlag(VertexAttribute.Normal) ? new Vector3[vertexCount] : null,
            Tangents = attributes.HasFlag(VertexAttribute.Tangent) ? new Vector3[vertexCount] : null,
            Bitangents = attributes.HasFlag(VertexAttribute.Tangent) ? new Vector3[vertexCount] : null,
            Colors = attributes.HasFlag(VertexAttribute.Colors) ? new uint[vertexCount] : null,
        };
    }

    /// <summary>BSVertexDataSSE records (BSTriShape and NiSkinPartition share the layout).</summary>
    private static RawGeometry DecodeVertices(Context ctx, ref BlockReader r, ulong desc, int vertexCount)
    {
        var attributes = (VertexAttribute)((desc >> 44) & 0xFFF);
        var stride = (int)(desc & 0xF) * 4;
        var g = EmptyVertices(desc, vertexCount);
        var positions = g.Positions;
        var uvs = g.UVs;
        var normals = g.Normals;
        var tangents = g.Tangents;
        var bitangents = g.Bitangents;
        var colors = g.Colors;

        // SSE always stores full-precision positions; FO4 (130) uses halves unless flagged.
        var fullPrecision = ctx.Header.BsVersion == 100 || attributes.HasFlag(VertexAttribute.FullPrecision);

        for (var i = 0; i < vertexCount; i++)
        {
            var start = r.Position;
            float bitangentX = 0, bitangentY = 0, bitangentZ = 0;

            if (attributes.HasFlag(VertexAttribute.Vertex))
            {
                if (fullPrecision)
                {
                    positions[i] = r.Vec3();
                    bitangentX = r.Float();
                }
                else
                {
                    positions[i] = new Vector3(r.Half(), r.Half(), r.Half());
                    bitangentX = r.Half();
                }
            }

            if (uvs is not null) uvs[i] = new Vector2(r.Half(), r.Half());

            if (normals is not null)
            {
                normals[i] = UnpackNormal(r.Byte(), r.Byte(), r.Byte());
                bitangentY = r.Byte() / 255f * 2f - 1f;
            }

            if (tangents is not null)
            {
                // Bethesda's "tangent" slot holds what is conventionally the bitangent.
                bitangents![i] = UnpackNormal(r.Byte(), r.Byte(), r.Byte());
                bitangentZ = r.Byte() / 255f * 2f - 1f;
                tangents[i] = Vector3.Normalize(new Vector3(bitangentX, bitangentY, bitangentZ) + new Vector3(1e-7f));
            }

            if (colors is not null) colors[i] = r.UInt();

            // Skin weights, eye data and anything else: skip to the stride.
            var used = r.Position - start;
            if (stride > used) r.Skip(stride - used);
        }

        return g;
    }

    /// <summary>
    /// SSE skinned geometry: the vertex buffer and the triangles of every partition live in the
    /// NiSkinPartition of the shape's skin instance. Drawn in the bind pose (no skeleton applied).
    /// </summary>
    private static RawGeometry? ReadSkinPartition(Context ctx, int skinRef, RawGeometry? existing)
    {
        if (!ctx.IsBlock(skinRef)) return existing;
        var skinType = ctx.Header.TypeOf(skinRef);
        if (skinType is not ("NiSkinInstance" or "BSDismemberSkinInstance")) return existing;

        var skin = new BlockReader(ctx.Block(skinRef));
        skin.Int(); // skin data
        var partitionRef = skin.Int();
        if (!ctx.IsBlock(partitionRef) || ctx.Header.TypeOf(partitionRef) != "NiSkinPartition") return existing;

        var r = new BlockReader(ctx.Block(partitionRef));
        var partitionCount = r.Int();
        if (partitionCount is < 0 or > 4096) throw new InvalidDataException("bad partition count");

        var g = existing;
        if (ctx.Header.BsVersion == 100)
        {
            var dataSize = r.Int();
            var vertexSize = r.Int();
            var desc = r.ULong();
            if (dataSize > 0 && vertexSize > 0)
            {
                var vertexCount = dataSize / vertexSize;
                var start = r.Position;
                var decoded = DecodeVertices(ctx, ref r, desc, vertexCount);
                r.Skip(dataSize - (r.Position - start));
                if (g is null || g.Positions.Length == 0 || g.Positions.All(p => p == Vector3.Zero))
                {
                    g = decoded;
                }
                else if (g.Positions.Length == decoded.Positions.Length)
                {
                    // Facegen heads (BSDynamicTriShape): morphed positions from the shape, the
                    // rest (UV, normals, colours) from the skin's vertex buffer.
                    decoded.Positions = g.Positions;
                    g = decoded;
                }
            }
        }

        if (g is null) return null;

        var indices = new List<uint>();
        for (var p = 0; p < partitionCount; p++)
        {
            int vertices = r.UShort();
            int triangles = r.UShort();
            int bones = r.UShort();
            int strips = r.UShort();
            int weightsPerVertex = r.UShort();
            r.Skip(bones * 2);

            ushort[]? map = null;
            if (r.Bool())
            {
                map = new ushort[vertices];
                for (var i = 0; i < vertices; i++) map[i] = r.UShort();
            }
            if (r.Bool()) r.Skip(vertices * weightsPerVertex * 4);

            var stripLengths = new int[strips];
            for (var i = 0; i < strips; i++) stripLengths[i] = r.UShort();

            // SSE partitions index the shared vertex buffer directly (their values are the vertex
            // map's entries, and match "triangles copy"); older files index through the map.
            var sse = ctx.Header.BsVersion == 100;
            uint Global(int local) => !sse && map is not null && local < map.Length ? map[local] : (uint)local;

            if (r.Bool())
            {
                if (strips > 0)
                {
                    foreach (var length in stripLengths)
                    {
                        var strip = new ushort[length];
                        for (var i = 0; i < length; i++) strip[i] = r.UShort();
                        for (var i = 2; i < length; i++)
                        {
                            int a = strip[i - 2], b = strip[i - 1], c = strip[i];
                            if (a == b || b == c || a == c) continue;
                            if ((i & 1) == 0) indices.AddRange([Global(a), Global(b), Global(c)]);
                            else indices.AddRange([Global(a), Global(c), Global(b)]);
                        }
                    }
                }
                else
                {
                    for (var i = 0; i < triangles * 3; i++) indices.Add(Global(r.UShort()));
                }
            }

            if (r.Bool()) r.Skip(vertices * weightsPerVertex); // bone indices
            if (ctx.Header.BsVersion > 34)
            {
                r.Byte(); // LOD level
                r.Bool(); // global VB
            }
            if (ctx.Header.BsVersion == 100)
            {
                r.ULong(); // vertex desc
                r.Skip(triangles * 6); // triangles copy
            }
        }

        // Partitions only matter when the shape had no triangles of its own.
        if (g.Indices.Length == 0)
        {
            var count = (uint)g.Positions.Length;
            g.Indices = indices.All(i => i < count) ? [.. indices] : [];
        }

        return g;
    }

    private static void SkipParticleData(ref BlockReader r, int vertexCount, int triangleCount)
    {
        var particleSize = r.UInt();
        if (particleSize > 0) r.Skip(vertexCount * 6 * 2 + triangleCount * 6);
    }

    private static Vector3 UnpackNormal(byte x, byte y, byte z) =>
        Vector3.Normalize(new Vector3(x / 255f * 2f - 1f, y / 255f * 2f - 1f, z / 255f * 2f - 1f) + new Vector3(1e-7f));

    /// <summary>LE geometry: NiTriShapeData / NiTriStripsData.</summary>
    private static RawGeometry? ReadNiGeometryData(Context ctx, int index)
    {
        var type = ctx.Header.TypeOf(index);
        if (type is not ("NiTriShapeData" or "NiTriStripsData")) return null;

        var r = new BlockReader(ctx.Block(index));
        r.Int(); // group id
        int vertexCount = r.UShort();
        r.Byte(); // keep flags
        r.Byte(); // compress flags

        var g = new RawGeometry();
        if (r.Bool())
        {
            g.Positions = new Vector3[vertexCount];
            for (var i = 0; i < vertexCount; i++) g.Positions[i] = r.Vec3();
        }

        var dataFlags = r.UShort(); // BS data flags: bit 0 UV, bit 12 tangents
        if (ctx.Header.BsVersion > 34) r.UInt(); // material CRC

        if (r.Bool())
        {
            g.Normals = new Vector3[vertexCount];
            for (var i = 0; i < vertexCount; i++) g.Normals[i] = r.Vec3();

            if ((dataFlags & 0x1000) != 0)
            {
                g.Tangents = new Vector3[vertexCount];
                g.Bitangents = new Vector3[vertexCount];
                for (var i = 0; i < vertexCount; i++) g.Tangents[i] = r.Vec3();
                for (var i = 0; i < vertexCount; i++) g.Bitangents[i] = r.Vec3();
            }
        }

        r.Vec3(); // center
        r.Float(); // radius

        if (r.Bool())
        {
            g.Colors = new uint[vertexCount];
            for (var i = 0; i < vertexCount; i++)
            {
                var c = r.Vec4();
                g.Colors[i] = PackColor(c);
            }
        }

        var uvSets = dataFlags & 0x1;
        if (uvSets > 0)
        {
            g.UVs = new Vector2[vertexCount];
            for (var i = 0; i < vertexCount; i++) g.UVs[i] = r.Vec2();
        }

        r.UShort(); // consistency
        r.Int(); // additional data

        int triangleCount = r.UShort();

        if (type == "NiTriShapeData")
        {
            r.UInt(); // triangle points
            if (r.Bool())
            {
                g.Indices = new uint[triangleCount * 3];
                for (var i = 0; i < g.Indices.Length; i++) g.Indices[i] = r.UShort();
            }
        }
        else
        {
            int stripCount = r.UShort();
            var lengths = new int[stripCount];
            for (var i = 0; i < stripCount; i++) lengths[i] = r.UShort();

            if (r.Bool())
            {
                var indices = new List<uint>(triangleCount * 3);
                foreach (var length in lengths)
                {
                    var strip = new ushort[length];
                    for (var i = 0; i < length; i++) strip[i] = r.UShort();

                    for (var i = 2; i < length; i++)
                    {
                        uint a = strip[i - 2], b = strip[i - 1], c = strip[i];
                        if (a == b || b == c || a == c) continue;
                        if ((i & 1) == 0) indices.AddRange([a, b, c]);
                        else indices.AddRange([a, c, b]);
                    }
                }
                g.Indices = [.. indices];
            }
        }

        return g;
    }

    private static uint PackColor(Vector4 c)
    {
        static uint B(float v) => (uint)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
        return B(c.X) | (B(c.Y) << 8) | (B(c.Z) << 16) | (B(c.W) << 24);
    }

    // ---------------------------------------------------------------- materials

    private static void ReadShader(Context ctx, int index, NifMaterial material)
    {
        var type = ctx.Header.TypeOf(index);
        var r = new BlockReader(ctx.Block(index));

        if (type == "BSLightingShaderProperty")
        {
            material.Kind = ShaderKind.Lighting;
            if (ctx.Header.BsVersion < 130) material.LightingType = (LightingShaderType)r.UInt();

            ctx.String(r.Int());
            r.Refs();
            ReadShaderControllers(ctx, r.Int(), material);

            material.Flags1 = (ShaderFlags1)r.UInt();
            material.Flags2 = (ShaderFlags2)r.UInt();
            material.UvOffset = r.Vec2();
            material.UvScale = r.Vec2();

            var textureSet = r.Int();
            material.EmissiveColor = new Vector4(r.Vec3(), 1f);
            material.EmissiveMultiple = r.Float();
            r.UInt(); // clamp mode
            material.Alpha = r.Float();
            r.Float(); // refraction strength
            material.Glossiness = r.Float();
            material.SpecularColor = r.Vec3();
            material.SpecularStrength = r.Float();
            r.Float(); // lighting effect 1
            r.Float(); // lighting effect 2

            switch (material.LightingType)
            {
                case LightingShaderType.EnvironmentMap:
                    material.EnvMapScale = r.Float();
                    break;
                case LightingShaderType.SkinTint:
                case LightingShaderType.HairTint:
                    material.TintColor = r.Vec3();
                    break;
                case LightingShaderType.EyeEnvmap:
                    material.EnvMapScale = r.Float();
                    break;
            }

            if (ctx.IsBlock(textureSet) && ctx.Header.TypeOf(textureSet) == "BSShaderTextureSet")
                material.Textures = ReadTextureSet(ctx, textureSet);
        }
        else if (type == "BSEffectShaderProperty")
        {
            material.Kind = ShaderKind.Effect;
            ctx.String(r.Int());
            r.Refs();
            ReadShaderControllers(ctx, r.Int(), material);

            material.Flags1 = (ShaderFlags1)r.UInt();
            material.Flags2 = (ShaderFlags2)r.UInt();
            material.UvOffset = r.Vec2();
            material.UvScale = r.Vec2();
            var source = r.SizedString();
            r.Skip(4); // clamp mode, lighting influence, env map min LOD, unknown
            r.Float(); // falloff start angle
            r.Float(); // falloff stop angle
            r.Float(); // falloff start opacity
            r.Float(); // falloff stop opacity
            material.EmissiveColor = r.Vec4();
            material.EmissiveMultiple = r.Float();
            if (r.Remaining >= 8)
            {
                r.Float(); // soft falloff depth
                var greyscale = r.SizedString();
                if (greyscale.Length > 0) material.GreyscaleTexture = greyscale;
            }
            material.Textures = [source];
        }
    }

    private static string[] ReadTextureSet(Context ctx, int index)
    {
        var r = new BlockReader(ctx.Block(index));
        var count = r.Int();
        var textures = new string[Math.Clamp(count, 0, 16)];
        for (var i = 0; i < textures.Length; i++) textures[i] = r.SizedString();
        return textures;
    }

    private static void ReadAlpha(Context ctx, int index, NifMaterial material)
    {
        var r = new BlockReader(ctx.Block(index));
        ctx.String(r.Int());
        r.Refs();
        r.Int();
        material.AlphaFlags = r.UShort();
        material.AlphaThreshold = r.Byte();
    }
}
