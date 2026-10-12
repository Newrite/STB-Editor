using System.Drawing;
using Mutagen.Bethesda.Skyrim;

namespace StbEditor.Core;

/// <summary>
/// CK: Update NPC Body Tint Colors. The body's tint (QNAM, «texture lighting») must match the face's skin tone or the
/// neck shows a seam. The CK sets it to mid grey mixed with the skin tone tint layer's colour by that layer's
/// interpolation, rounded down — true for every NPC of Skyrim.esm (2168) and Dawnguard.esm (213).
/// </summary>
public static class BodyTint
{
    /// <summary>The body tint the CK would set, or null when the NPC has no skin tone layer (its race has none).</summary>
    public static Color? Expected(INpcGetter npc, IRaceGetter race)
    {
        var female = npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
        var head = female ? race.HeadData?.Female : race.HeadData?.Male;
        var skin = head?.TintMasks.FirstOrDefault(t => t.MaskType == TintAssets.TintMaskType.SkinTone);
        if (skin is null || npc.TintLayers.FirstOrDefault(l => l.Index == skin.Index) is not { Color: { } color } layer) return null;
        var mix = layer.InterpolationValue ?? 1f;
        int Channel(byte value) => (int)Math.Floor(127.5 + (value - 127.5) * mix);
        return Color.FromArgb(0, Channel(color.R), Channel(color.G), Channel(color.B));
    }

    /// <summary>Whether the NPC's body tint differs from the expected one by more than a seam can show (3 of 255 per channel).</summary>
    public static bool Stale(INpcGetter npc, IRaceGetter race, out Color expected)
    {
        expected = default;
        if (Expected(npc, race) is not { } wanted) return false;
        expected = wanted;
        if (npc.TextureLighting is not { } current) return true;
        return Math.Abs(current.R - wanted.R) > 3 || Math.Abs(current.G - wanted.G) > 3 || Math.Abs(current.B - wanted.B) > 3;
    }
}
