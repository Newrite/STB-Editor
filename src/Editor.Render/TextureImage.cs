using System.Buffers.Binary;

namespace StbEditor.Render;

/// <summary>
/// A texture in memory as 8-bit RGBA (CPU side, for baking FaceGen tints): decoded from the DDS layouts Skyrim's tint
/// masks and tints use (DXT1/3/5, BC4/5, 24 / 32-bit RGB, 8-bit luminance) and written back as DXT5 with mipmaps.
/// </summary>
public sealed class TextureImage(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;

    /// <summary>RGBA, row by row.</summary>
    public byte[] Pixels { get; } = pixels;

    public TextureImage(int width, int height) : this(width, height, new byte[width * height * 4]) { }

    /// <summary>First mip of a DDS file.</summary>
    public static TextureImage Decode(byte[] file)
    {
        var span = file.AsSpan();
        if (span.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(span) != 0x20534444) throw new InvalidDataException("not a DDS file");
        var height = (int)U32(span, 12);
        var width = (int)U32(span, 16);
        var pfFlags = U32(span, 80);
        var fourCC = U32(span, 84);
        var bits = U32(span, 88);
        uint rMask = U32(span, 92), gMask = U32(span, 96), bMask = U32(span, 100), aMask = U32(span, 104);
        var offset = 128;
        var image = new TextureImage(width, height);
        var data = span[offset..];

        if ((pfFlags & 0x4) != 0)
        {
            if (fourCC == 0x30315844) // DX10: format code after the header
            {
                var dxgi = U32(span, 128);
                data = span[148..];
                fourCC = dxgi switch
                {
                    71 or 72 => 0x31545844, // BC1
                    74 or 75 => 0x33545844, // BC2
                    77 or 78 => 0x35545844, // BC3
                    80 => 0x31495441, // BC4
                    83 => 0x32495441, // BC5
                    28 or 29 => 0, // R8G8B8A8
                    87 or 91 => 1, // B8G8R8A8
                    _ => throw new NotSupportedException($"DDS DXGI format {dxgi}"),
                };
                if (fourCC == 0) { Copy32(data, image, 0, 1, 2, 3); return image; }
                if (fourCC == 1) { Copy32(data, image, 2, 1, 0, 3); return image; }
            }
            switch (fourCC)
            {
                case 0x31545844: Blocks(data, image, 8, (b, o) => Bc1(b, o, true)); break;
                case 0x32545844 or 0x33545844: Blocks(data, image, 16, Bc2); break;
                case 0x34545844 or 0x35545844: Blocks(data, image, 16, Bc3); break;
                case 0x31495441 or 0x55344342: Blocks(data, image, 8, Bc4); break;
                case 0x32495441 or 0x55354342: Blocks(data, image, 16, Bc5); break;
                default: throw new NotSupportedException($"DDS FourCC 0x{fourCC:X8}");
            }
            return image;
        }

        if ((pfFlags & 0x40) != 0 && bits is 24 or 32)
        {
            var step = (int)bits / 8;
            int Shift(uint mask) => mask == 0 ? -1 : System.Numerics.BitOperations.TrailingZeroCount(mask) / 8;
            int r = Shift(rMask), g = Shift(gMask), b = Shift(bMask), a = (pfFlags & 0x1) != 0 ? Shift(aMask) : -1;
            for (var i = 0; i < width * height; i++)
            {
                var s = data.Slice(i * step, step);
                image.Pixels[i * 4] = s[r];
                image.Pixels[i * 4 + 1] = s[g];
                image.Pixels[i * 4 + 2] = s[b];
                image.Pixels[i * 4 + 3] = a >= 0 ? s[a] : (byte)255;
            }
            return image;
        }

        if ((pfFlags & 0x20000) != 0 && bits == 8)
        {
            for (var i = 0; i < width * height; i++)
            {
                image.Pixels[i * 4] = image.Pixels[i * 4 + 1] = image.Pixels[i * 4 + 2] = data[i];
                image.Pixels[i * 4 + 3] = 255;
            }
            return image;
        }

        throw new NotSupportedException($"DDS pixel format flags 0x{pfFlags:X}, {bits} bpp");
    }

    private static void Copy32(ReadOnlySpan<byte> data, TextureImage image, int r, int g, int b, int a)
    {
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            image.Pixels[i * 4] = data[i * 4 + r];
            image.Pixels[i * 4 + 1] = data[i * 4 + g];
            image.Pixels[i * 4 + 2] = data[i * 4 + b];
            image.Pixels[i * 4 + 3] = data[i * 4 + a];
        }
    }

    /// <summary>Bilinear resample (masks of another size than the tint).</summary>
    public TextureImage Resize(int width, int height)
    {
        if (width == Width && height == Height) return this;
        var result = new TextureImage(width, height);
        for (var y = 0; y < height; y++)
        {
            var fy = Math.Clamp((y + 0.5f) * Height / height - 0.5f, 0, Height - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, Height - 1);
            var ty = fy - y0;
            for (var x = 0; x < width; x++)
            {
                var fx = Math.Clamp((x + 0.5f) * Width / width - 0.5f, 0, Width - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, Width - 1);
                var tx = fx - x0;
                for (var c = 0; c < 4; c++)
                {
                    float p00 = Pixels[(y0 * Width + x0) * 4 + c], p10 = Pixels[(y0 * Width + x1) * 4 + c];
                    float p01 = Pixels[(y1 * Width + x0) * 4 + c], p11 = Pixels[(y1 * Width + x1) * 4 + c];
                    var v = (p00 * (1 - tx) + p10 * tx) * (1 - ty) + (p01 * (1 - tx) + p11 * tx) * ty;
                    result.Pixels[(y * width + x) * 4 + c] = (byte)Math.Clamp(MathF.Round(v), 0, 255);
                }
            }
        }
        return result;
    }

    /// <summary>Half size, box filtered (one mip down).</summary>
    public TextureImage Half()
    {
        int w = Math.Max(1, Width / 2), h = Math.Max(1, Height / 2);
        var result = new TextureImage(w, h);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var c = 0; c < 4; c++)
                {
                    int x0 = Math.Min(x * 2, Width - 1), x1 = Math.Min(x * 2 + 1, Width - 1), y0 = Math.Min(y * 2, Height - 1), y1 = Math.Min(y * 2 + 1, Height - 1);
                    var sum = Pixels[(y0 * Width + x0) * 4 + c] + Pixels[(y0 * Width + x1) * 4 + c] + Pixels[(y1 * Width + x0) * 4 + c] + Pixels[(y1 * Width + x1) * 4 + c];
                    result.Pixels[(y * w + x) * 4 + c] = (byte)((sum + 2) / 4);
                }
        return result;
    }

    /// <summary>DDS file, DXT5 with the whole mip chain down to 1×1 (as the CK writes FaceGen tints).</summary>
    public byte[] EncodeDxt5()
    {
        var mips = new List<TextureImage> { this };
        while (mips[^1].Width > 1 || mips[^1].Height > 1) mips.Add(mips[^1].Half());
        using var output = new MemoryStream();
        var w = new BinaryWriter(output);
        var linearSize = Math.Max(1, (Width + 3) / 4) * Math.Max(1, (Height + 3) / 4) * 16;
        w.Write(0x20534444u);
        w.Write(124u);
        w.Write(0x1u | 0x2u | 0x4u | 0x1000u | 0x20000u | 0x80000u); // caps, height, width, pixel format, mip count, linear size
        w.Write((uint)Height);
        w.Write((uint)Width);
        w.Write((uint)linearSize);
        w.Write(0u);
        w.Write((uint)mips.Count);
        for (var i = 0; i < 11; i++) w.Write(0u);
        w.Write(32u);
        w.Write(0x4u); // FourCC
        w.Write(0x35545844u); // DXT5
        for (var i = 0; i < 5; i++) w.Write(0u);
        w.Write(0x1000u | 0x8u | 0x400000u); // texture, complex, mipmap
        w.Write(0u);
        w.Write(0u);
        w.Write(0u);
        w.Write(0u);
        foreach (var mip in mips) mip.WriteDxt5Blocks(w);
        return output.ToArray();
    }

    private void WriteDxt5Blocks(BinaryWriter w)
    {
        Span<byte> block = stackalloc byte[64];
        for (var by = 0; by < Math.Max(1, (Height + 3) / 4); by++)
            for (var bx = 0; bx < Math.Max(1, (Width + 3) / 4); bx++)
            {
                for (var y = 0; y < 4; y++)
                    for (var x = 0; x < 4; x++)
                    {
                        int sx = Math.Min(bx * 4 + x, Width - 1), sy = Math.Min(by * 4 + y, Height - 1);
                        for (var c = 0; c < 4; c++) block[(y * 4 + x) * 4 + c] = Pixels[(sy * Width + sx) * 4 + c];
                    }
                WriteAlphaBlock(w, block);
                WriteColorBlock(w, block);
            }
    }

    private static void WriteAlphaBlock(BinaryWriter w, ReadOnlySpan<byte> block)
    {
        byte min = 255, max = 0;
        for (var i = 0; i < 16; i++) { min = Math.Min(min, block[i * 4 + 3]); max = Math.Max(max, block[i * 4 + 3]); }
        w.Write(max);
        w.Write(min);
        ulong indices = 0;
        if (max != min)
        {
            // 8-value mode (a0 > a1): index 0 = max, 1 = min, 2..7 between, from max towards min.
            for (var i = 0; i < 16; i++)
            {
                var t = (float)(max - block[i * 4 + 3]) / (max - min) * 7f;
                var step = (int)MathF.Round(t);
                var index = step switch { 0 => 0, 7 => 1, _ => step + 1 };
                indices |= (ulong)index << (3 * i);
            }
        }
        for (var i = 0; i < 6; i++) w.Write((byte)(indices >> (8 * i)));
    }

    private static void WriteColorBlock(BinaryWriter w, ReadOnlySpan<byte> block)
    {
        // Endpoints along the colours' principal axis (from the extremes of their projection), then refined once.
        float mr = 0, mg = 0, mb = 0;
        for (var i = 0; i < 16; i++) { mr += block[i * 4]; mg += block[i * 4 + 1]; mb += block[i * 4 + 2]; }
        mr /= 16; mg /= 16; mb /= 16;
        float cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
        for (var i = 0; i < 16; i++)
        {
            float r = block[i * 4] - mr, g = block[i * 4 + 1] - mg, b = block[i * 4 + 2] - mb;
            cxx += r * r; cxy += r * g; cxz += r * b; cyy += g * g; cyz += g * b; czz += b * b;
        }
        float ax = 1, ay = 1, az = 1;
        for (var it = 0; it < 6; it++)
        {
            float nx = cxx * ax + cxy * ay + cxz * az, ny = cxy * ax + cyy * ay + cyz * az, nz = cxz * ax + cyz * ay + czz * az;
            var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-6f) break;
            ax = nx / len; ay = ny / len; az = nz / len;
        }
        float minT = float.MaxValue, maxT = float.MinValue;
        for (var i = 0; i < 16; i++)
        {
            var t = (block[i * 4] - mr) * ax + (block[i * 4 + 1] - mg) * ay + (block[i * 4 + 2] - mb) * az;
            minT = Math.Min(minT, t); maxT = Math.Max(maxT, t);
        }
        var c0 = Pack(mr + ax * maxT, mg + ay * maxT, mb + az * maxT);
        var c1 = Pack(mr + ax * minT, mg + ay * minT, mb + az * minT);
        if (c0 < c1) (c0, c1) = (c1, c0);

        Span<int> palette = stackalloc int[12];
        Unpack(c0, out palette[0], out palette[1], out palette[2]);
        Unpack(c1, out palette[3], out palette[4], out palette[5]);
        for (var c = 0; c < 3; c++)
        {
            palette[6 + c] = (2 * palette[c] + palette[3 + c] + 1) / 3;
            palette[9 + c] = (palette[c] + 2 * palette[3 + c] + 1) / 3;
        }
        uint indices = 0;
        for (var i = 0; i < 16; i++)
        {
            var best = 0;
            var bestError = int.MaxValue;
            for (var p = 0; p < 4; p++)
            {
                var slot = p switch { 0 => 0, 1 => 3, 2 => 6, _ => 9 };
                int dr = block[i * 4] - palette[slot], dg = block[i * 4 + 1] - palette[slot + 1], db = block[i * 4 + 2] - palette[slot + 2];
                var e = dr * dr + dg * dg + db * db;
                if (e < bestError) { bestError = e; best = p; }
            }
            indices |= (uint)best << (2 * i);
        }
        if (c0 == c1) indices = 0; // one colour: four-colour mode needs c0 > c1, index 0 is exact
        w.Write(c0);
        w.Write(c1);
        w.Write(indices);
    }

    private static ushort Pack(float r, float g, float b)
    {
        var ri = (int)Math.Clamp(MathF.Round(r * 31 / 255f), 0, 31);
        var gi = (int)Math.Clamp(MathF.Round(g * 63 / 255f), 0, 63);
        var bi = (int)Math.Clamp(MathF.Round(b * 31 / 255f), 0, 31);
        return (ushort)((ri << 11) | (gi << 5) | bi);
    }

    private static void Unpack(ushort c, out int r, out int g, out int b)
    {
        r = (c >> 11) * 255 / 31;
        g = ((c >> 5) & 63) * 255 / 63;
        b = (c & 31) * 255 / 31;
    }

    // ------------------------------------------------------------------ block decoding

    private delegate void BlockDecoder(ReadOnlySpan<byte> block, Span<byte> rgba);

    private static void Blocks(ReadOnlySpan<byte> data, TextureImage image, int blockSize, BlockDecoder decode)
    {
        Span<byte> rgba = stackalloc byte[64];
        int bw = Math.Max(1, (image.Width + 3) / 4), bh = Math.Max(1, (image.Height + 3) / 4);
        for (var by = 0; by < bh; by++)
            for (var bx = 0; bx < bw; bx++)
            {
                var at = (by * bw + bx) * blockSize;
                if (at + blockSize > data.Length) return;
                decode(data.Slice(at, blockSize), rgba);
                for (var y = 0; y < 4; y++)
                    for (var x = 0; x < 4; x++)
                    {
                        int px = bx * 4 + x, py = by * 4 + y;
                        if (px >= image.Width || py >= image.Height) continue;
                        rgba.Slice((y * 4 + x) * 4, 4).CopyTo(image.Pixels.AsSpan((py * image.Width + px) * 4, 4));
                    }
            }
    }

    private static void Bc1(ReadOnlySpan<byte> b, Span<byte> rgba, bool allowAlpha)
    {
        var c0 = BinaryPrimitives.ReadUInt16LittleEndian(b);
        var c1 = BinaryPrimitives.ReadUInt16LittleEndian(b[2..]);
        var bits = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        Span<int> p = stackalloc int[16];
        Unpack(c0, out p[0], out p[1], out p[2]); p[3] = 255;
        Unpack(c1, out p[4], out p[5], out p[6]); p[7] = 255;
        if (c0 > c1 || !allowAlpha)
        {
            for (var c = 0; c < 3; c++) { p[8 + c] = (2 * p[c] + p[4 + c]) / 3; p[12 + c] = (p[c] + 2 * p[4 + c]) / 3; }
            p[11] = p[15] = 255;
        }
        else
        {
            for (var c = 0; c < 3; c++) { p[8 + c] = (p[c] + p[4 + c]) / 2; p[12 + c] = 0; }
            p[11] = 255; p[15] = 0;
        }
        for (var i = 0; i < 16; i++)
        {
            var index = (int)(bits >> (2 * i)) & 3;
            for (var c = 0; c < 4; c++) rgba[i * 4 + c] = (byte)p[index * 4 + c];
        }
    }

    private static void Bc2(ReadOnlySpan<byte> b, Span<byte> rgba)
    {
        Bc1(b[8..], rgba, false);
        for (var i = 0; i < 16; i++)
        {
            var nibble = (b[i / 2] >> (4 * (i % 2))) & 0xF;
            rgba[i * 4 + 3] = (byte)(nibble * 17);
        }
    }

    private static void Bc3(ReadOnlySpan<byte> b, Span<byte> rgba)
    {
        Bc1(b[8..], rgba, false);
        Span<byte> alpha = stackalloc byte[16];
        Bc4Channel(b, alpha);
        for (var i = 0; i < 16; i++) rgba[i * 4 + 3] = alpha[i];
    }

    private static void Bc4(ReadOnlySpan<byte> b, Span<byte> rgba)
    {
        Span<byte> red = stackalloc byte[16];
        Bc4Channel(b, red);
        for (var i = 0; i < 16; i++) { rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = red[i]; rgba[i * 4 + 3] = 255; }
    }

    private static void Bc5(ReadOnlySpan<byte> b, Span<byte> rgba)
    {
        Span<byte> red = stackalloc byte[16];
        Span<byte> green = stackalloc byte[16];
        Bc4Channel(b, red);
        Bc4Channel(b[8..], green);
        for (var i = 0; i < 16; i++) { rgba[i * 4] = red[i]; rgba[i * 4 + 1] = green[i]; rgba[i * 4 + 2] = 0; rgba[i * 4 + 3] = 255; }
    }

    private static void Bc4Channel(ReadOnlySpan<byte> b, Span<byte> values)
    {
        int a0 = b[0], a1 = b[1];
        Span<int> p = stackalloc int[8];
        p[0] = a0; p[1] = a1;
        if (a0 > a1) for (var i = 1; i < 7; i++) p[i + 1] = ((7 - i) * a0 + i * a1) / 7;
        else
        {
            for (var i = 1; i < 5; i++) p[i + 1] = ((5 - i) * a0 + i * a1) / 5;
            p[6] = 0; p[7] = 255;
        }
        ulong bits = 0;
        for (var i = 0; i < 6; i++) bits |= (ulong)b[2 + i] << (8 * i);
        for (var i = 0; i < 16; i++) values[i] = (byte)p[(int)(bits >> (3 * i)) & 7];
    }

    private static uint U32(ReadOnlySpan<byte> span, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);
}
