using System.Buffers.Binary;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace StbEditor.Render;

/// <summary>
/// DDS → D3D11 texture. Block-compressed formats go to the GPU as they are; the few legacy
/// uncompressed layouts Skyrim mods use (24-bit RGB, luminance) are expanded to RGBA8.
/// </summary>
public static class Dds
{
    private const uint Magic = 0x20534444; // "DDS "
    private const uint DDPF_ALPHAPIXELS = 0x1;
    private const uint DDPF_FOURCC = 0x4;
    private const uint DDPF_RGB = 0x40;
    private const uint DDPF_LUMINANCE = 0x20000;
    private const uint DDSCAPS2_CUBEMAP = 0x200;

    public sealed record Info(Format Format, int Width, int Height, int Mips, bool IsCube);

    public static (ID3D11ShaderResourceView View, Info Info) CreateTexture(ID3D11Device device, byte[] file)
    {
        var span = file.AsSpan();
        if (span.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic)
            throw new InvalidDataException("not a DDS file");

        var height = (int)U32(span, 12);
        var width = (int)U32(span, 16);
        var mips = Math.Max(1, (int)U32(span, 28));
        var pfFlags = U32(span, 80);
        var fourCC = U32(span, 84);
        var bitCount = U32(span, 88);
        uint rMask = U32(span, 92), gMask = U32(span, 96), bMask = U32(span, 100), aMask = U32(span, 104);
        var caps2 = U32(span, 112);

        var offset = 128;
        var arraySize = (caps2 & DDSCAPS2_CUBEMAP) != 0 ? 6 : 1;
        Format format;
        var expand = Expand.None;

        if ((pfFlags & DDPF_FOURCC) != 0)
        {
            format = fourCC switch
            {
                0x31545844 => Format.BC1_UNorm, // DXT1
                0x32545844 or 0x33545844 => Format.BC2_UNorm, // DXT2/3
                0x34545844 or 0x35545844 => Format.BC3_UNorm, // DXT4/5
                0x31495441 or 0x55344342 => Format.BC4_UNorm, // ATI1 / BC4U
                0x32495441 or 0x55354342 => Format.BC5_UNorm, // ATI2 / BC5U
                0x30315844 => ReadDx10(span, ref offset, ref arraySize), // DX10
                _ => throw new NotSupportedException($"DDS FourCC 0x{fourCC:X8}"),
            };
        }
        else if ((pfFlags & DDPF_RGB) != 0 && bitCount == 32)
        {
            if (rMask == 0x00FF0000 && gMask == 0x0000FF00 && bMask == 0x000000FF)
                format = (pfFlags & DDPF_ALPHAPIXELS) != 0 && aMask != 0 ? Format.B8G8R8A8_UNorm : Format.B8G8R8X8_UNorm;
            else if (rMask == 0x000000FF)
                format = Format.R8G8B8A8_UNorm;
            else
                throw new NotSupportedException("32-bit DDS with unusual masks");
        }
        else if ((pfFlags & DDPF_RGB) != 0 && bitCount == 24)
        {
            format = Format.R8G8B8A8_UNorm;
            expand = Expand.Bgr24;
        }
        else if ((pfFlags & DDPF_LUMINANCE) != 0 && bitCount == 8)
        {
            format = Format.R8G8B8A8_UNorm;
            expand = Expand.Luminance8;
        }
        else
        {
            throw new NotSupportedException($"DDS pixel format flags 0x{pfFlags:X}, {bitCount} bpp");
        }

        // Some textures claim more mips than a 1x1 chain has, or are truncated: clamp to what fits.
        mips = Math.Min(mips, 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height))));

        var subresources = new List<SubresourceData>();
        var pinned = new List<System.Runtime.InteropServices.GCHandle>();
        var buffers = new List<byte[]>();

        try
        {
            var usableMips = mips;
            for (var face = 0; face < arraySize; face++)
            {
                int w = width, h = height;
                for (var mip = 0; mip < mips; mip++)
                {
                    var (rowPitch, slice) = Pitch(format, expand, w, h);
                    var sourceSize = expand switch
                    {
                        Expand.Bgr24 => w * h * 3,
                        Expand.Luminance8 => w * h,
                        _ => slice,
                    };

                    if (offset + sourceSize > file.Length)
                    {
                        usableMips = Math.Min(usableMips, mip);
                        break;
                    }

                    byte[] data;
                    int dataOffset;
                    if (expand == Expand.None)
                    {
                        data = file;
                        dataOffset = offset;
                    }
                    else
                    {
                        data = ExpandPixels(span.Slice(offset, sourceSize), expand, w, h);
                        dataOffset = 0;
                    }

                    buffers.Add(data);
                    var handle = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
                    pinned.Add(handle);
                    subresources.Add(new SubresourceData(handle.AddrOfPinnedObject() + dataOffset, (uint)rowPitch, (uint)slice));

                    offset += sourceSize;
                    w = Math.Max(1, w / 2);
                    h = Math.Max(1, h / 2);
                }
            }

            if (usableMips == 0) throw new InvalidDataException("truncated DDS");
            if (usableMips < mips)
            {
                // Rebuild the subresource list with the shorter chain on every face.
                var trimmed = new List<SubresourceData>();
                for (var face = 0; face < arraySize; face++)
                    for (var mip = 0; mip < usableMips; mip++)
                        trimmed.Add(subresources[face * mips + mip]);
                subresources = trimmed;
                mips = usableMips;
            }

            var description = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = (uint)mips,
                ArraySize = (uint)arraySize,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                MiscFlags = arraySize == 6 ? ResourceOptionFlags.TextureCube : ResourceOptionFlags.None,
            };

            using var texture = device.CreateTexture2D(description, subresources.ToArray());
            var view = device.CreateShaderResourceView(texture);
            return (view, new Info(format, width, height, mips, arraySize == 6));
        }
        finally
        {
            foreach (var handle in pinned) handle.Free();
        }
    }

    private enum Expand { None, Bgr24, Luminance8 }

    private static Format ReadDx10(ReadOnlySpan<byte> span, ref int offset, ref int arraySize)
    {
        var dxgi = (Format)U32(span, 128);
        var misc = U32(span, 136);
        var count = (int)U32(span, 140);
        offset = 148;
        arraySize = (misc & 0x4) != 0 ? 6 * Math.Max(1, count) : Math.Max(1, count);
        if (arraySize > 6) throw new NotSupportedException("DDS texture arrays");

        // sRGB variants: Skyrim shades in gamma space, so read them as plain UNORM.
        return dxgi switch
        {
            Format.BC1_UNorm_SRgb => Format.BC1_UNorm,
            Format.BC2_UNorm_SRgb => Format.BC2_UNorm,
            Format.BC3_UNorm_SRgb => Format.BC3_UNorm,
            Format.BC7_UNorm_SRgb => Format.BC7_UNorm,
            Format.R8G8B8A8_UNorm_SRgb => Format.R8G8B8A8_UNorm,
            Format.B8G8R8A8_UNorm_SRgb => Format.B8G8R8A8_UNorm,
            _ => dxgi,
        };
    }

    private static (int RowPitch, int Slice) Pitch(Format format, Expand expand, int w, int h)
    {
        if (expand != Expand.None) return (w * 4, w * h * 4);

        var blockBytes = format switch
        {
            Format.BC1_UNorm or Format.BC4_UNorm or Format.BC4_SNorm => 8,
            Format.BC2_UNorm or Format.BC3_UNorm or Format.BC5_UNorm or Format.BC5_SNorm
                or Format.BC6H_Uf16 or Format.BC6H_Sf16 or Format.BC7_UNorm => 16,
            _ => 0,
        };

        if (blockBytes > 0)
        {
            var bw = Math.Max(1, (w + 3) / 4);
            var bh = Math.Max(1, (h + 3) / 4);
            return (bw * blockBytes, bw * bh * blockBytes);
        }

        var bpp = format switch
        {
            Format.R8G8B8A8_UNorm or Format.B8G8R8A8_UNorm or Format.B8G8R8X8_UNorm => 4,
            Format.R8_UNorm or Format.A8_UNorm => 1,
            Format.R8G8_UNorm or Format.B5G6R5_UNorm or Format.B5G5R5A1_UNorm or Format.R16_UNorm => 2,
            Format.R16G16B16A16_Float or Format.R16G16B16A16_UNorm => 8,
            Format.R32G32B32A32_Float => 16,
            _ => throw new NotSupportedException($"DDS DXGI format {format}"),
        };
        return (w * bpp, w * h * bpp);
    }

    private static byte[] ExpandPixels(ReadOnlySpan<byte> source, Expand expand, int w, int h)
    {
        var output = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            if (expand == Expand.Bgr24)
            {
                output[i * 4 + 0] = source[i * 3 + 2];
                output[i * 4 + 1] = source[i * 3 + 1];
                output[i * 4 + 2] = source[i * 3 + 0];
            }
            else
            {
                output[i * 4 + 0] = output[i * 4 + 1] = output[i * 4 + 2] = source[i];
            }
            output[i * 4 + 3] = 255;
        }
        return output;
    }

    private static uint U32(ReadOnlySpan<byte> span, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);
}
