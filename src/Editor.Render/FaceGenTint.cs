using System.Collections.Concurrent;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Assets;

namespace StbEditor.Render;

/// <summary>
/// The FaceGen tint texture of an NPC as the Creation Kit bakes it: the skin colour (texture lighting, QNAM) under
/// every tint layer of the NPC in record order (but the skin tone layer, which QNAM already is), each one mixed in by
/// its race mask (red channel) times its value.
/// Checked against the CK's own tints with <c>stbedit-cli facegentint</c>.
/// </summary>
public static class FaceGenTint
{
    public const int Size = 512;

    private static readonly ConcurrentDictionary<string, TextureImage?> Masks = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="head">The race's head data for the NPC's sex (its tint masks).</param>
    /// <param name="skip">Layers left out (index → true), for checks.</param>
    public static TextureImage Compose(INpcGetter npc, IHeadDataGetter? head, Func<string, byte[]?> readTexture, Func<ITintAssetsGetter, bool>? skip = null)
    {
        var image = new TextureImage(Size, Size);
        var skin = npc.TextureLighting;
        float r = skin?.R ?? 255, g = skin?.G ?? 255, b = skin?.B ?? 255;
        var buffer = new float[Size * Size * 3];
        for (var i = 0; i < Size * Size; i++) { buffer[i * 3] = r; buffer[i * 3 + 1] = g; buffer[i * 3 + 2] = b; }

        foreach (var layer in npc.TintLayers)
        {
            if (layer.Index is not { } index || layer.Color is not { } color) continue;
            var value = layer.InterpolationValue ?? 0f;
            if (value <= 0) continue;
            var mask = head?.TintMasks.FirstOrDefault(m => m.Index == index);
            // The skin tone layer is the base colour itself (QNAM holds it); the CK does not paint it again.
            if (mask?.FileName?.GivenPath is not { Length: > 0 } file || mask.MaskType == TintAssets.TintMaskType.SkinTone || skip?.Invoke(mask) == true) continue;
            var maskImage = Masks.GetOrAdd(AssetFileSystem.TexturePath(file), path =>
            {
                try { return readTexture(path) is { } bytes ? TextureImage.Decode(bytes).Resize(Size, Size) : null; }
                catch { return null; }
            });
            if (maskImage is null) continue;
            for (var i = 0; i < Size * Size; i++)
            {
                var a = maskImage.Pixels[i * 4] / 255f * value;
                if (a <= 0) continue;
                buffer[i * 3] += (color.R - buffer[i * 3]) * a;
                buffer[i * 3 + 1] += (color.G - buffer[i * 3 + 1]) * a;
                buffer[i * 3 + 2] += (color.B - buffer[i * 3 + 2]) * a;
            }
        }

        for (var i = 0; i < Size * Size; i++)
        {
            image.Pixels[i * 4] = (byte)Math.Clamp(MathF.Round(buffer[i * 3]), 0, 255);
            image.Pixels[i * 4 + 1] = (byte)Math.Clamp(MathF.Round(buffer[i * 3 + 1]), 0, 255);
            image.Pixels[i * 4 + 2] = (byte)Math.Clamp(MathF.Round(buffer[i * 3 + 2]), 0, 255);
            image.Pixels[i * 4 + 3] = 255;
        }
        return image;
    }
}
