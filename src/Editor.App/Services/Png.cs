using System.IO.Compression;

namespace StbEditor.App.Services;

/// <summary>A minimal PNG writer (8-bit RGBA, no filtering) for pictures sent to Claude.</summary>
internal static class Png
{
    private static readonly uint[] Crc = MakeCrc();

    /// <summary>RGBA rows (top first) as a PNG; with <paramref name="maxWidth"/> smaller, scaled down by whole-pixel boxes.</summary>
    public static byte[] Encode(byte[] rgba, int width, int height, int maxWidth = 0)
    {
        if (maxWidth > 0 && width > maxWidth) (rgba, width, height) = Shrink(rgba, width, height, (width + maxWidth - 1) / maxWidth);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[width * 4 + 1];
            for (var y = 0; y < height; y++)
            {
                row[0] = 0;
                Buffer.BlockCopy(rgba, y * width * 4, row, 1, width * 4);
                // Opaque: the viewport's alpha is whatever the passes left.
                for (var x = 0; x < width; x++) row[1 + x * 4 + 3] = 255;
                z.Write(row);
            }
        }
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BigEndian(header, 0, (uint)width);
        BigEndian(header, 4, (uint)height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // RGBA
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static (byte[], int, int) Shrink(byte[] rgba, int width, int height, int factor)
    {
        var w = width / factor;
        var h = height / factor;
        var result = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var c = 0; c < 4; c++)
                {
                    var sum = 0;
                    for (var dy = 0; dy < factor; dy++)
                        for (var dx = 0; dx < factor; dx++)
                            sum += rgba[((y * factor + dy) * width + x * factor + dx) * 4 + c];
                    result[(y * w + x) * 4 + c] = (byte)(sum / (factor * factor));
                }
        return (result, w, h);
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var length = new byte[4];
        BigEndian(length, 0, (uint)data.Length);
        s.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in typeBytes) crc = Crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = Crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        var tail = new byte[4];
        BigEndian(tail, 0, crc ^ 0xFFFFFFFFu);
        s.Write(tail);
    }

    private static void BigEndian(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }

    private static uint[] MakeCrc()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
