using System.Numerics;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace StbEditor.Core;

/// <summary>An alpha (ATXT/VTXT) texture layer of one quadrant: opacity per vertex of its 17×17 grid.</summary>
public sealed class LandLayer(FormKey texture, float[] opacity)
{
    public FormKey Texture { get; } = texture;
    public float[] Opacity { get; } = opacity;

    public LandLayer Clone() => new(Texture, (float[])Opacity.Clone());
}

/// <summary>Base texture and alpha layers (bottom to top) of one quadrant (0 SW, 1 SE, 2 NW, 3 NE).</summary>
public sealed class LandQuadrant
{
    public FormKey? BaseTexture { get; set; }
    public List<LandLayer> Layers { get; } = [];

    public LandQuadrant Clone()
    {
        var copy = new LandQuadrant { BaseTexture = BaseTexture };
        copy.Layers.AddRange(Layers.Select(l => l.Clone()));
        return copy;
    }
}

/// <summary>
/// Editable form of a LAND record: world-unit heights, vertex normals and colours (33×33) and
/// texture layers per quadrant. <see cref="WriteTo"/> encodes it back the way the game stores it:
/// VHGT as an offset plus signed byte steps of 8 units (steeper slopes are clamped — a limit of
/// the format), VNML from the heights, BTXT/ATXT/VTXT layers.
/// </summary>
public sealed class LandscapeData
{
    public const int Size = 33;
    public const int QuadrantSize = 17;
    public const float CellSize = 4096f;
    public const float Spacing = CellSize / (Size - 1);
    public const int MaxAlphaLayers = 6;

    /// <summary>Row-major (y * 33 + x), world units.</summary>
    public float[] Heights { get; private init; } = new float[Size * Size];

    /// <summary>Unit normals; recomputed from the heights after edits.</summary>
    public Vector3[] Normals { get; private init; } = new Vector3[Size * Size];

    /// <summary>RGB per vertex (R in the low byte); null when the land has no vertex colours.</summary>
    public uint[]? Colors { get; set; }

    public LandQuadrant[] Quadrants { get; private init; } = [new(), new(), new(), new()];

    /// <summary>LAND DATA flags of the original record, kept on save.</summary>
    public Landscape.Flag OriginalFlags { get; set; }

    /// <summary>Heights changed: normals are recomputed on save (else the stored ones are kept).</summary>
    public bool HeightsEdited { get; set; }

    public LandscapeData Clone() => new()
    {
        Heights = (float[])Heights.Clone(),
        Normals = (Vector3[])Normals.Clone(),
        Colors = (uint[]?)Colors?.Clone(),
        Quadrants = Quadrants.Select(q => q.Clone()).ToArray(),
        OriginalFlags = OriginalFlags,
        HeightsEdited = HeightsEdited,
    };

    public static LandscapeData Flat(float height)
    {
        var data = new LandscapeData { HeightsEdited = true };
        Array.Fill(data.Heights, MathF.Round(height / 8f) * 8f);
        Array.Fill(data.Normals, Vector3.UnitZ);
        return data;
    }

    public static LandscapeData From(ILandscapeGetter land)
    {
        var data = new LandscapeData { OriginalFlags = land.Flags ?? 0 };

        if (land.VertexHeightMap is { } map)
        {
            var steps = map.HeightMap;
            var rowStart = map.Offset;
            for (var y = 0; y < Size; y++)
            {
                rowStart += steps[0, y];
                var h = rowStart;
                for (var x = 0; x < Size; x++)
                {
                    if (x > 0) h += steps[x, y];
                    data.Heights[y * Size + x] = h * 8f;
                }
            }
        }

        if (land.VertexNormals is { } normals)
        {
            for (var y = 0; y < Size; y++)
                for (var x = 0; x < Size; x++)
                {
                    var v = normals[x, y];
                    var n = new Vector3((sbyte)v.X, (sbyte)v.Y, (sbyte)v.Z);
                    data.Normals[y * Size + x] = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitZ;
                }
        }
        else
        {
            data.RecomputeNormals();
        }

        if (land.VertexColors is { } colors)
        {
            data.Colors = new uint[Size * Size];
            for (var y = 0; y < Size; y++)
                for (var x = 0; x < Size; x++)
                {
                    var c = colors[x, y];
                    data.Colors[y * Size + x] = c.X | ((uint)c.Y << 8) | ((uint)c.Z << 16);
                }
        }

        var alpha = new List<(int Quadrant, int Number, LandLayer Layer)>();
        foreach (var layer in land.Layers ?? [])
        {
            var q = (int)layer.Header.Quadrant;
            if (q is < 0 or > 3) continue;
            var texture = layer.Header.Texture.FormKey;
            if (layer is IAlphaLayerGetter a)
            {
                var opacity = new float[QuadrantSize * QuadrantSize];
                foreach (var point in a.AlphaLayerData ?? [])
                    if (point.Position < opacity.Length) opacity[point.Position] = point.Opacity;
                alpha.Add((q, layer.Header.LayerNumber, new LandLayer(texture, opacity)));
            }
            else
            {
                data.Quadrants[q].BaseTexture = texture.IsNull ? null : texture;
            }
        }
        foreach (var (q, _, layer) in alpha.OrderBy(a => a.Number))
            data.Quadrants[q].Layers.Add(layer);

        return data;
    }

    public float HeightAt(int x, int y) => Heights[y * Size + x];

    /// <summary>Normals from central differences of the heights (one-sided on the border).</summary>
    public void RecomputeNormals()
    {
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var x0 = Math.Max(x - 1, 0);
                var x1 = Math.Min(x + 1, Size - 1);
                var y0 = Math.Max(y - 1, 0);
                var y1 = Math.Min(y + 1, Size - 1);
                var dx = (HeightAt(x1, y) - HeightAt(x0, y)) / ((x1 - x0) * Spacing);
                var dy = (HeightAt(x, y1) - HeightAt(x, y0)) / ((y1 - y0) * Spacing);
                Normals[y * Size + x] = Vector3.Normalize(new Vector3(-dx, -dy, 1f));
            }
    }

    /// <summary>Rounds heights to what VHGT can hold (8-unit steps, ±127 steps between neighbours).</summary>
    public void Quantize()
    {
        var (offset, steps) = Encode();
        var rowStart = offset;
        for (var y = 0; y < Size; y++)
        {
            rowStart += steps[0, y];
            var h = rowStart;
            for (var x = 0; x < Size; x++)
            {
                if (x > 0) h += steps[x, y];
                Heights[y * Size + x] = h * 8f;
            }
        }
    }

    private (float Offset, sbyte[,] Steps) Encode()
    {
        var steps = new sbyte[Size, Size];
        var offset = MathF.Round(Heights[0] / 8f);
        var rowStart = (int)offset;
        for (var y = 0; y < Size; y++)
        {
            var target = (int)MathF.Round(Heights[y * Size] / 8f);
            var step = Math.Clamp(target - rowStart, -128, 127);
            steps[0, y] = (sbyte)step;
            rowStart += step;
            var h = rowStart;
            for (var x = 1; x < Size; x++)
            {
                target = (int)MathF.Round(Heights[y * Size + x] / 8f);
                step = Math.Clamp(target - h, -128, 127);
                steps[x, y] = (sbyte)step;
                h += step;
            }
        }
        return (offset, steps);
    }

    /// <summary>Writes heights, normals, colours and layers into a LAND record.</summary>
    public void WriteTo(Landscape land)
    {
        Quantize();
        if (HeightsEdited) RecomputeNormals();
        var (offset, steps) = Encode();

        var heightMap = new Array2d<sbyte>(new sbyte[Size, Size]);
        var normals = new Array2d<P3UInt8>(new P3UInt8[Size, Size]);
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                heightMap[new P2Int(x, y)] = steps[x, y];
                var n = Normals[y * Size + x];
                normals[new P2Int(x, y)] = new P3UInt8(Signed(n.X), Signed(n.Y), Signed(n.Z));
            }

        land.VertexHeightMap = new LandscapeVertexHeightMap { Offset = offset, HeightMap = heightMap };
        land.VertexNormals = normals;

        if (Colors is { } colors)
        {
            var vertexColors = new Array2d<P3UInt8>(new P3UInt8[Size, Size]);
            for (var y = 0; y < Size; y++)
                for (var x = 0; x < Size; x++)
                {
                    var c = colors[y * Size + x];
                    vertexColors[new P2Int(x, y)] = new P3UInt8((byte)c, (byte)(c >> 8), (byte)(c >> 16));
                }
            land.VertexColors = vertexColors;
        }
        else
        {
            land.VertexColors = null;
        }

        var layers = new ExtendedList<BaseLayer>();
        for (var q = 0; q < 4; q++)
        {
            var quadrant = Quadrants[q];
            if (quadrant.BaseTexture is { } baseTexture)
            {
                var layer = new BaseLayer { Header = new LayerHeader { Quadrant = (Quadrant)q, LayerNumber = 0 } };
                layer.Header.Texture.SetTo(baseTexture);
                layers.Add(layer);
            }

            var number = 0;
            foreach (var paint in quadrant.Layers)
            {
                var points = new ExtendedList<AlphaLayerData>();
                for (var i = 0; i < paint.Opacity.Length; i++)
                    if (paint.Opacity[i] > 0.001f)
                        points.Add(new AlphaLayerData { Position = (ushort)i, Opacity = Math.Clamp(paint.Opacity[i], 0f, 1f) });
                if (points.Count == 0) continue;

                var alpha = new AlphaLayer
                {
                    Header = new LayerHeader { Quadrant = (Quadrant)q, LayerNumber = (ushort)number++ },
                    AlphaLayerData = points,
                };
                alpha.Header.Texture.SetTo(paint.Texture);
                layers.Add(alpha);
            }
        }

        // Layers is init-only in Mutagen; the setter is an ordinary one underneath.
        typeof(Landscape).GetProperty(nameof(Landscape.Layers))!.SetValue(land, layers.Count > 0 ? layers : null);

        var flags = OriginalFlags | Landscape.Flag.VertexNormalsHeightMap;
        flags = Colors is null ? flags & ~Landscape.Flag.VertexColors : flags | Landscape.Flag.VertexColors;
        flags = layers.Count == 0 ? flags & ~Landscape.Flag.Layers : flags | Landscape.Flag.Layers;
        land.Flags = flags;
    }

    private static byte Signed(float v) => (byte)(sbyte)Math.Clamp((int)MathF.Round(v * 127f), -127, 127);

    // ------------------------------------------------------------------ brushes

    /// <summary>Quadrants a vertex belongs to (the middle row/column is shared) and its index in each.</summary>
    public static IEnumerable<(int Quadrant, int Index)> QuadrantVertices(int x, int y)
    {
        for (var q = 0; q < 4; q++)
        {
            var qx = (q & 1) * 16;
            var qy = (q >> 1) * 16;
            var lx = x - qx;
            var ly = y - qy;
            if (lx is >= 0 and < QuadrantSize && ly is >= 0 and < QuadrantSize)
                yield return (q, ly * QuadrantSize + lx);
        }
    }

    /// <summary>
    /// Paints <paramref name="texture"/> at a vertex by <paramref name="amount"/> (0..1). The
    /// quadrant base texture is "painted" by fading the layers above it; another texture gets (or
    /// reuses) an alpha layer, and layers above it fade so it shows. Returns false when a quadrant
    /// has no room for another layer.
    /// </summary>
    public bool Paint(int x, int y, FormKey texture, float amount)
    {
        var ok = true;
        foreach (var (q, index) in QuadrantVertices(x, y))
        {
            // No base texture means the game's default land texture underneath: still blend on top.
            var quadrant = Quadrants[q];
            int position;
            if (quadrant.BaseTexture == texture)
            {
                position = -1;
            }
            else
            {
                position = quadrant.Layers.FindIndex(l => l.Texture == texture);
                if (position < 0)
                {
                    // Reuse a layer that has faded away completely before giving up.
                    var empty = quadrant.Layers.FindIndex(l => l.Opacity.All(o => o <= 0.001f));
                    if (empty >= 0) quadrant.Layers.RemoveAt(empty);
                    if (quadrant.Layers.Count >= MaxAlphaLayers)
                    {
                        ok = false;
                        continue;
                    }
                    quadrant.Layers.Add(new LandLayer(texture, new float[QuadrantSize * QuadrantSize]));
                    position = quadrant.Layers.Count - 1;
                }

                var layer = quadrant.Layers[position];
                layer.Opacity[index] = Math.Min(1f, layer.Opacity[index] + amount);
            }

            for (var i = position + 1; i < quadrant.Layers.Count; i++)
                quadrant.Layers[i].Opacity[index] = Math.Max(0f, quadrant.Layers[i].Opacity[index] - amount);
        }
        return ok;
    }
}
