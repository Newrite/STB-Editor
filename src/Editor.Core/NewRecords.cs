using System.Drawing;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace StbEditor.Core;

/// <summary>Interior cell lighting entered by hand (XCLL).</summary>
public sealed record LightingSpec(Color Ambient, Color Directional, Color Fog, float FogNear, float FogFar, int RotationXY, int RotationZ)
{
    /// <summary>A plain, workable default: dim warm ambient, light from above-front.</summary>
    public static readonly LightingSpec Default = new(
        Color.FromArgb(70, 66, 62), Color.FromArgb(110, 100, 86), Color.FromArgb(14, 14, 16), 64f, 12000f, 45, 45);
}

/// <summary>
/// Builders for records the editor creates (cells, worldspaces), filled the way the Creation
/// Kit fills new ones. They become <see cref="Draft"/>s in the document and are written on save.
/// </summary>
public static class NewRecords
{
    /// <summary>EditorIDs the CK accepts: letters, digits and underscores, starting with a letter.</summary>
    public static bool IsValidEditorId(string id) =>
        id.Length is > 0 and <= 200 && char.IsAsciiLetter(id[0]) && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public static CellDraft InteriorCell(FormKey key, SkyrimRelease release, string editorId, string? name, LightingSpec lighting, ILightingTemplateGetter? template)
    {
        var cell = new Cell(key, release)
        {
            EditorID = editorId,
            Flags = Cell.Flag.IsInteriorCell,
        };
        if (!string.IsNullOrWhiteSpace(name)) cell.Name = name;

        if (template is not null)
        {
            // Everything inherited from the template, values copied so the cell reads the same without it.
            cell.LightingTemplate.SetTo(template.FormKey);
            cell.Lighting = new CellLighting
            {
                AmbientColor = template.AmbientColor,
                DirectionalColor = template.DirectionalColor,
                FogNearColor = template.FogNearColor,
                FogNear = template.FogNear,
                FogFar = template.FogFar,
                DirectionalRotationXY = template.DirectionalRotationXY,
                DirectionalRotationZ = template.DirectionalRotationZ,
                DirectionalFade = template.DirectionalFade,
                FogClipDistance = template.FogClipDistance,
                FogPower = template.FogPower,
                FogFarColor = template.FogFarColor,
                FogMax = template.FogMax,
                LightFadeBegin = template.LightFadeStartDistance,
                LightFadeEnd = template.LightFadeEndDistance,
                Inherits = AllInherited,
            };
            return new CellDraft(cell, null);
        }

        cell.Lighting = new CellLighting
        {
            AmbientColor = lighting.Ambient,
            DirectionalColor = lighting.Directional,
            FogNearColor = lighting.Fog,
            FogFarColor = lighting.Fog,
            FogNear = lighting.FogNear,
            FogFar = lighting.FogFar,
            DirectionalRotationXY = lighting.RotationXY,
            DirectionalRotationZ = lighting.RotationZ,
            DirectionalFade = 1f,
            FogClipDistance = 0f,
            FogPower = 1f,
            FogMax = 1f,
            LightFadeBegin = 0f,
            LightFadeEnd = 0f,
            AmbientColors = new AmbientColors
            {
                DirectionalXPlus = lighting.Ambient,
                DirectionalXMinus = lighting.Ambient,
                DirectionalYPlus = lighting.Ambient,
                DirectionalYMinus = lighting.Ambient,
                DirectionalZPlus = lighting.Ambient,
                DirectionalZMinus = lighting.Ambient,
                Specular = Color.Black,
                Scale = 1f,
            },
        };
        return new CellDraft(cell, null);
    }

    private static CellLighting.Inherit AllInherited =>
        Enum.GetValues<CellLighting.Inherit>().Aggregate((CellLighting.Inherit)0, (all, flag) => all | flag);

    /// <summary>A worldspace with its persistent cell; land and water heights as the CK defaults them.</summary>
    public static WorldDraft Worldspace(FormKey key, FormKey persistentCell, SkyrimRelease release, string editorId, string? name,
        FormKey? climate, FormKey? water, float waterHeight, float landHeight, bool smallWorld)
    {
        var world = new Worldspace(key, release)
        {
            EditorID = editorId,
            LandDefaults = new WorldspaceLandDefaults { DefaultLandHeight = landHeight, DefaultWaterHeight = waterHeight },
            ObjectBoundsMin = new P2Float(-64f, -64f),
            ObjectBoundsMax = new P2Float(64f, 64f),
            DistantLodMultiplier = 1f,
            TopCell = new Cell(persistentCell, release),
        };
        if (!string.IsNullOrWhiteSpace(name)) world.Name = name;
        if (smallWorld) world.Flags |= Mutagen.Bethesda.Skyrim.Worldspace.Flag.SmallWorld;
        if (climate is { } c) world.Climate.SetTo(c);
        if (water is { } w) world.Water.SetTo(w);
        return new WorldDraft(world);
    }

    public static CellDraft ExteriorCell(FormKey key, SkyrimRelease release, FormKey world, int x, int y) =>
        new(new Cell(key, release) { Grid = new CellGrid { Point = new P2Int(x, y) } }, world);
}
