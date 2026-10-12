using System.Text.Json;

namespace StbEditor.Core;

/// <summary>Where a heightmap lies and how its 16-bit values map to heights (the .json next to the .r16).</summary>
public sealed class HeightmapInfo
{
    public string World { get; set; } = "";
    public int MinCellX { get; set; }
    public int MinCellY { get; set; }
    public int CellsX { get; set; }
    public int CellsY { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Height (game units) of value 0 and of value 65535.</summary>
    public float MinHeight { get; set; }
    public float MaxHeight { get; set; }
}

/// <summary>
/// CK: Heightmap Editor import / export, for the loaded exterior cells: one 16-bit sample per landscape vertex
/// (32 per cell plus the shared edge), north at the top, as a raw little-endian .r16 that World Machine, Gaea or an
/// image editor read; the .json beside it keeps the cell range and the height range.
/// </summary>
public static class Heightmap
{
    private const int Step = LandscapeData.Size - 1;

    public static (HeightmapInfo Info, ushort[] Samples) Build(string world, IReadOnlyList<(int X, int Y, float[] Heights)> cells)
    {
        var minX = cells.Min(c => c.X);
        var minY = cells.Min(c => c.Y);
        var cellsX = cells.Max(c => c.X) - minX + 1;
        var cellsY = cells.Max(c => c.Y) - minY + 1;
        var info = new HeightmapInfo
        {
            World = world, MinCellX = minX, MinCellY = minY, CellsX = cellsX, CellsY = cellsY,
            Width = cellsX * Step + 1, Height = cellsY * Step + 1,
            MinHeight = cells.Min(c => c.Heights.Min()), MaxHeight = cells.Max(c => c.Heights.Max()),
        };
        if (info.MaxHeight - info.MinHeight < 1) info.MaxHeight = info.MinHeight + 1;
        var samples = new ushort[info.Width * info.Height];
        foreach (var (x, y, heights) in cells)
            for (var j = 0; j < LandscapeData.Size; j++)
                for (var i = 0; i < LandscapeData.Size; i++)
                {
                    var (column, row) = Pixel(info, x, y, i, j);
                    samples[row * info.Width + column] = ToSample(info, heights[j * LandscapeData.Size + i]);
                }
        return (info, samples);
    }

    /// <summary>The pixel of a cell's vertex: columns go east, rows go south (north on top).</summary>
    private static (int Column, int Row) Pixel(HeightmapInfo info, int cellX, int cellY, int i, int j) =>
        ((cellX - info.MinCellX) * Step + i, info.Height - 1 - ((cellY - info.MinCellY) * Step + j));

    private static ushort ToSample(HeightmapInfo info, float height) =>
        (ushort)Math.Clamp(Math.Round((height - info.MinHeight) / (info.MaxHeight - info.MinHeight) * 65535), 0, 65535);

    /// <summary>A cell's 33×33 heights from the map (null when the cell is outside it).</summary>
    public static float[]? CellHeights(HeightmapInfo info, ushort[] samples, int cellX, int cellY)
    {
        if (cellX < info.MinCellX || cellY < info.MinCellY || cellX >= info.MinCellX + info.CellsX || cellY >= info.MinCellY + info.CellsY) return null;
        var heights = new float[LandscapeData.Size * LandscapeData.Size];
        for (var j = 0; j < LandscapeData.Size; j++)
            for (var i = 0; i < LandscapeData.Size; i++)
            {
                var (column, row) = Pixel(info, cellX, cellY, i, j);
                heights[j * LandscapeData.Size + i] = info.MinHeight + samples[row * info.Width + column] / 65535f * (info.MaxHeight - info.MinHeight);
            }
        return heights;
    }

    public static void Write(string path, HeightmapInfo info, ushort[] samples)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length); // little-endian on every PC this runs on
        File.WriteAllBytes(path, bytes);
        File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static (HeightmapInfo Info, ushort[] Samples) Read(string path)
    {
        var info = JsonSerializer.Deserialize<HeightmapInfo>(File.ReadAllText(Path.ChangeExtension(path, ".json")))
            ?? throw new InvalidDataException("the .json beside the heightmap is empty");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != info.Width * info.Height * 2)
            throw new InvalidDataException($"{Path.GetFileName(path)} has {bytes.Length / 2} samples, the .json says {info.Width} x {info.Height}");
        var samples = new ushort[info.Width * info.Height];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return (info, samples);
    }
}
