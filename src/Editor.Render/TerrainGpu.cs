using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace StbEditor.Render;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct TerrainVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public uint Color;
    public Vector2 UV;
    public float Alpha;

    public static readonly int Size = Marshal.SizeOf<TerrainVertex>();
}

/// <summary>One textured pass over a quadrant: the base layer, or an alpha layer blended on top.</summary>
internal sealed class TerrainPass : IDisposable
{
    public required ID3D11Buffer Vertices { get; init; }
    public required string Texture { get; init; }
    public required bool IsBase { get; init; }

    public void Dispose() => Vertices.Dispose();
}

/// <summary>GPU form of a terrain cell: 4 quadrants × (base + alpha layers), 17×17 vertices each.</summary>
internal sealed class TerrainGpu : IDisposable
{
    public const int QuadrantVertices = 17;

    /// <summary>Landscape textures repeat this many times across a cell.</summary>
    private const float TilesPerCell = 6f;

    public required TerrainCell Cell { get; init; }
    public required List<TerrainPass> Passes { get; init; }
    public required Vector3 BoundsCenter { get; init; }
    public required float BoundsRadius { get; init; }

    public static TerrainGpu Create(ID3D11Device device, TerrainCell cell)
    {
        var passes = new List<TerrainPass>();
        var origin = cell.Origin;
        float minZ = float.MaxValue, maxZ = float.MinValue;

        for (var q = 0; q < 4; q++)
        {
            var qx = (q & 1) * 16;
            var qy = (q >> 1) * 16;

            foreach (var layer in cell.Quadrants[q])
            {
                var vertices = new TerrainVertex[QuadrantVertices * QuadrantVertices];
                for (var y = 0; y < QuadrantVertices; y++)
                {
                    for (var x = 0; x < QuadrantVertices; x++)
                    {
                        var vx = qx + x;
                        var vy = qy + y;
                        var i = vy * TerrainCell.Size + vx;
                        var z = cell.Heights[i];
                        minZ = Math.Min(minZ, z);
                        maxZ = Math.Max(maxZ, z);

                        vertices[y * QuadrantVertices + x] = new TerrainVertex
                        {
                            Position = origin + new Vector3(vx * TerrainCell.Spacing, vy * TerrainCell.Spacing, z),
                            Normal = cell.Normals[i],
                            Color = cell.Colors[i],
                            UV = new Vector2(vx, -vy) * (TilesPerCell / (TerrainCell.Size - 1)),
                            Alpha = layer.Opacity is null ? 1f : layer.Opacity[y * QuadrantVertices + x],
                        };
                    }
                }

                passes.Add(new TerrainPass
                {
                    Vertices = device.CreateBuffer(vertices, BindFlags.VertexBuffer, ResourceUsage.Immutable),
                    Texture = layer.TexturePath,
                    IsBase = layer.Opacity is null,
                });
            }
        }

        var center = origin + new Vector3(TerrainCell.CellSize / 2, TerrainCell.CellSize / 2, (minZ + maxZ) / 2);
        var radius = new Vector3(TerrainCell.CellSize / 2, TerrainCell.CellSize / 2, (maxZ - minZ) / 2).Length();
        return new TerrainGpu { Cell = cell, Passes = passes, BoundsCenter = center, BoundsRadius = radius };
    }

    /// <summary>Shared index buffer: 16×16 quads of a 17×17 grid.</summary>
    public static uint[] QuadrantIndices()
    {
        var indices = new List<uint>(16 * 16 * 6);
        for (var y = 0; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                var a = (uint)(y * QuadrantVertices + x);
                var b = a + 1;
                var c = a + QuadrantVertices;
                var d = c + 1;
                indices.AddRange([a, b, d, a, d, c]);
            }
        }
        return [.. indices];
    }

    public void Dispose()
    {
        foreach (var pass in Passes) pass.Dispose();
    }
}
