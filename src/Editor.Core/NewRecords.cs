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

    /// <summary>
    /// Records that are not made from the Object Window: cells, worlds and what lives in them have
    /// their own tools; quests and dialogue wait for their editors; the header and the default
    /// object manager exist once.
    /// </summary>
    public static readonly HashSet<string> NotCreatable =
        ["CELL", "WRLD", "LAND", "NAVM", "NAVI", "REFR", "ACHR", "PGRE", "PHZD", "PMIS", "PARW", "PBAR", "PBEA", "PCON", "PFLA",
         "DIAL", "INFO", "DLBR", "DLVW", "TES4", "DOBJ"];

    /// <summary>
    /// Classes a new record of a type can be: one for most, several for abstract ones (a global is
    /// GlobalFloat / GlobalInt / GlobalShort, a game setting Float / Int / String / Bool).
    /// </summary>
    public static IReadOnlyList<Type> RecordClasses(Type getter)
    {
        if (!getter.IsInterface || !getter.Name.StartsWith('I') || !getter.Name.EndsWith("Getter", StringComparison.Ordinal)) return [];
        var cls = getter.Assembly.GetType($"{getter.Namespace}.{getter.Name[1..^6]}");
        if (cls is null) return [];
        if (!cls.IsAbstract) return HasRecordConstructor(cls) ? [cls] : [];
        return getter.Assembly.GetTypes()
            .Where(t => !t.IsAbstract && cls.IsAssignableFrom(t) && HasRecordConstructor(t) && !t.Name.EndsWith("Unknown", StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
    }

    private static bool HasRecordConstructor(Type type) => type.GetConstructor([typeof(FormKey), typeof(SkyrimRelease)]) is not null;

    /// <summary>
    /// A new, empty record of <paramref name="cls"/> with the parts the file format requires filled
    /// in (as the CK's «New» gives a blank form).
    /// </summary>
    public static Mutagen.Bethesda.Plugins.Records.IMajorRecord Blank(Type cls, FormKey key, SkyrimRelease release, string editorId)
    {
        var record = (Mutagen.Bethesda.Plugins.Records.IMajorRecord)System.Activator.CreateInstance(cls, key, release)!;
        record.EditorID = editorId;
        RecordFields.FillRequired(record);
        // A global's value subrecord is required (FLTV): 0, as the CK starts it.
        if (record is Global && cls.GetProperty("Data") is { } data && Nullable.GetUnderlyingType(data.PropertyType) is { } valueType)
            data.SetValue(record, System.Activator.CreateInstance(valueType));
        if (record is Package package) PackageDefaults(package);
        // Every vanilla scene writes its flags (FNAM), even when none are set.
        if (record is Scene scene) scene.Flags ??= 0;
        return record;
    }

    /// <summary>
    /// A new package as vanilla ones are: any time (Mutagen's zeros would mean January, Sundays, 0:00), run,
    /// every interrupt allowed, and the begin / end / change events present with an empty script header
    /// and a null topic (POBA INAM SCHR PDTO…).
    /// </summary>
    private static void PackageDefaults(Package package)
    {
        package.Type = Package.Types.Package;
        package.ScheduleMonth = -1;
        package.ScheduleDayOfWeek = Package.DayOfWeek.Any;
        package.ScheduleDate = 0;
        package.ScheduleHour = -1;
        package.ScheduleMinute = -1;
        package.PreferredSpeed = Package.Speed.Run;
        package.InterruptFlags = (Package.InterruptFlag)0xFFFF;
        PackageEvent Event()
        {
            var header = new byte[20];
            header[18] = 1;
            var e = new PackageEvent { SCHR = header };
            e.Topics.Add(new TopicReference());
            return e;
        }
        package.OnBegin ??= Event();
        package.OnEnd ??= Event();
        package.OnChange ??= Event();
    }

    /// <summary>Game settings name their kind by the EditorID's first letter (fJumpHeight, iLevel, sName, bFlag).</summary>
    public static string? GameSettingPrefix(Type cls) => cls.Name switch
    {
        "GameSettingFloat" => "f",
        "GameSettingInt" => "i",
        "GameSettingString" => "s",
        "GameSettingBool" => "b",
        _ => null,
    };
}
