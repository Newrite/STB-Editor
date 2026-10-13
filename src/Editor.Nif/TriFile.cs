using System.Numerics;
using System.Text;

namespace StbEditor.Nif;

/// <summary>
/// A FaceGen TRI file (FRTRI003): the head part's base vertices and its morphs. Difference morphs store an int16 delta
/// per vertex and a scale (delta × scale = offset); stat morphs move a few vertices to stored positions. Head parts
/// name three of them: the race morph, expressions and the chargen morphs (the face sliders and presets).
/// </summary>
public sealed class TriFile
{
    public required Vector3[] Vertices { get; init; }
    public required int[] Triangles { get; init; }
    public required IReadOnlyList<TriMorph> Morphs { get; init; }
    public required IReadOnlyList<TriStatMorph> StatMorphs { get; init; }

    public TriMorph? Morph(string name) => Morphs.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    public static TriFile Load(byte[] data)
    {
        var r = new BinaryReader(new MemoryStream(data));
        var magic = Encoding.ASCII.GetString(r.ReadBytes(8));
        if (magic != "FRTRI003") throw new InvalidDataException($"not a TRI file ({magic})");
        var vertexCount = r.ReadInt32();
        var triangleCount = r.ReadInt32();
        var quadCount = r.ReadInt32();
        var labelledVertices = r.ReadInt32();
        var labelledSurfacePoints = r.ReadInt32();
        var texCoordCount = r.ReadInt32();
        var extension = r.ReadInt32();
        var differenceMorphs = r.ReadInt32();
        var statMorphs = r.ReadInt32();
        var statVertices = r.ReadInt32();
        r.ReadBytes(16);

        var vertices = new Vector3[vertexCount];
        for (var i = 0; i < vertexCount; i++) vertices[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var statPositions = new Vector3[statVertices];
        for (var i = 0; i < statVertices; i++) statPositions[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        var triangles = new int[triangleCount * 3];
        for (var i = 0; i < triangles.Length; i++) triangles[i] = r.ReadInt32();
        r.ReadBytes(quadCount * 16);
        r.ReadBytes(labelledVertices * 4);
        for (var i = 0; i < labelledSurfacePoints; i++)
        {
            r.ReadBytes(4 + 12);
            r.ReadBytes(r.ReadInt32());
        }
        if (texCoordCount > 0)
        {
            r.ReadBytes(texCoordCount * 8);
            if ((extension & 1) != 0) r.ReadBytes(triangleCount * 12 + quadCount * 16);
        }

        var morphs = new List<TriMorph>(differenceMorphs);
        for (var m = 0; m < differenceMorphs; m++)
        {
            var name = ReadName(r);
            var scale = r.ReadSingle();
            var deltas = new Vector3[vertexCount];
            for (var i = 0; i < vertexCount; i++) deltas[i] = new Vector3(r.ReadInt16(), r.ReadInt16(), r.ReadInt16()) * scale;
            morphs.Add(new TriMorph(name, scale, deltas));
        }

        var stats = new List<TriStatMorph>(statMorphs);
        var next = 0;
        for (var m = 0; m < statMorphs; m++)
        {
            var name = ReadName(r);
            var count = r.ReadInt32();
            var indices = new int[count];
            for (var i = 0; i < count; i++) indices[i] = r.ReadInt32();
            stats.Add(new TriStatMorph(name, indices, statPositions[next..Math.Min(next + count, statPositions.Length)]));
            next += count;
        }

        return new TriFile { Vertices = vertices, Triangles = triangles, Morphs = morphs, StatMorphs = stats };
    }

    private static string ReadName(BinaryReader r)
    {
        var length = r.ReadInt32();
        return Encoding.ASCII.GetString(r.ReadBytes(length)).TrimEnd('\0');
    }
}

/// <summary>A difference morph: the offset of every vertex at full weight (already multiplied by the scale).</summary>
public sealed record TriMorph(string Name, float Scale, Vector3[] Deltas);

/// <summary>A stat morph: these vertices go to these positions.</summary>
public sealed record TriStatMorph(string Name, int[] Indices, Vector3[] Positions);
