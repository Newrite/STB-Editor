using System.Numerics;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Services;
using StbEditor.Data;
using StbEditor.Nif;
using StbEditor.Render;

namespace StbEditor.App.Panels;

/// <summary>
/// Where the game's skill menu puts a perk tree, read out of SkyrimSE.exe (StatsMenu): the menu loads
/// <c>Interface\INTPerkSkydome.nif</c>; skill slot N's stars hang from its node <c>PointNN</c> (18 of them on a ring of ~99 units
/// around the camera, each with its constellation picture <c>cImageNN</c> just in front), at
/// x = (width / 2 − (column + horizontal)) × fStatsStarXIncrement, y = (row + vertical) × fStatsStarYIncrement,
/// z = fStatsStarZInitialOffset − (row + vertical) × fStatsStarZIncrement in the node's space, width = the biggest column of
/// a node with a perk. Stars are <c>INTPerkStars01.nif</c> × fStatsStarScale. Higher columns are further left, rows go away
/// and up, so upper rows close in as in the game.
/// </summary>
internal static class PerkSky
{
    public const string Skydome = @"meshes\interface\intperkskydome.nif";
    public const string Star = @"meshes\interface\intperkstars01.nif";

    /// <summary>Skill → the menu's slot (Point / cImage number), in the order the ring has them.</summary>
    public static readonly Dictionary<string, int> Slots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AVEnchanting"] = 0, ["AVSmithing"] = 1, ["AVHeavyArmor"] = 2, ["AVBlock"] = 3, ["AVTwoHanded"] = 4, ["AVOneHanded"] = 5,
        ["AVMarksman"] = 6, ["AVLightArmor"] = 7, ["AVSneak"] = 8, ["AVLockpicking"] = 9, ["AVPickpocket"] = 10, ["AVSpeechcraft"] = 11,
        ["AVAlchemy"] = 12, ["AVMysticism"] = 13, ["AVConjuration"] = 14, ["AVDestruction"] = 15, ["AVRestoration"] = 16, ["AVAlteration"] = 17,
    };

    /// <summary>The menu's game settings (the exe's defaults unless a plugin sets them).</summary>
    public sealed record Settings(float XIncrement, float YIncrement, float ZIncrement, float ZInitialOffset, float StarScale, float Fov);

    private static NifModel? _dome;
    private static object? _domeFor;

    public static Settings Read(EditorSession session, RecordLookup lookup)
    {
        float Get(string name, float fallback) =>
            session.ByEditorId(name) is { } key && lookup.Resolve<IGameSettingFloatGetter>(key)?.Data is { } value ? value : fallback;
        return new Settings(Get("fStatsStarXIncrement", 5), Get("fStatsStarYIncrement", 15), Get("fStatsStarZIncrement", -9),
            Get("fStatsStarZInitialOffset", 5), Get("fStatsStarScale", 0.15f), Get("fStatsCameraFOV", 50));
    }

    /// <summary>The skydome model (for its Point / cImage nodes), read once per session.</summary>
    public static NifModel? Dome(EditorSession session)
    {
        if (ReferenceEquals(_domeFor, session)) return _dome;
        _domeFor = session;
        try { _dome = session.Assets.ReadAllBytes(Skydome) is { } bytes ? NifFile.Load(bytes, Skydome) : null; }
        catch (Exception e) { Log.Warn($"perk sky: {e.Message}"); _dome = null; }
        return _dome;
    }

    /// <summary>The space a skill's stars live in; slot 0's when the skill is not one of the menu's 18.</summary>
    public static (NifTransform Point, int Slot, bool Known) Place(NifModel dome, IActorValueInformationGetter avif)
    {
        var known = Slots.TryGetValue(avif.EditorID ?? "", out var slot);
        if (!known) slot = 0;
        return (dome.Nodes.TryGetValue($"Point{slot:00}", out var point) ? point : NifTransform.Identity, slot, known);
    }

    /// <summary>The tree's width: the biggest column of a node with a perk (the root's leftovers do not count).</summary>
    public static uint Width(IReadOnlyList<IActorValuePerkNodeGetter> nodes) =>
        nodes.Where(n => !n.Perk.IsNull).Select(n => n.PerkGridX ?? 0).DefaultIfEmpty(0u).Max();

    /// <summary>A star's place in its Point's space for a node at column + horizontal / row + vertical.</summary>
    public static Vector3 Local(Vector2 grid, uint width, Settings s) =>
        new((width * 0.5f - grid.X) * s.XIncrement, grid.Y * s.YIncrement, s.ZInitialOffset - grid.Y * s.ZIncrement);

    /// <summary>
    /// The grid place under a ray (world space): where it meets the tree's slope (the plane the stars lie on), as
    /// column + horizontal / row + vertical. Null when the ray runs along the slope or away from it.
    /// </summary>
    public static Vector2? GridAt(Vector3 origin, Vector3 direction, NifTransform point, uint width, Settings s)
    {
        var inverse = point.Inverse();
        var o = inverse.Apply(origin);
        var d = inverse.Rotation.Transform(direction);
        // Stars lie on p(x, t) = (x, Y t, Z0 − Z t): the plane through (0, 0, Z0) with normal (0, Z, Y).
        var normal = new Vector3(0, s.ZIncrement, s.YIncrement);
        var along = Vector3.Dot(normal, d);
        if (MathF.Abs(along) < 1e-6f) return null;
        var hit = Vector3.Dot(normal, new Vector3(0, 0, s.ZInitialOffset) - o) / along;
        if (hit <= 0) return null;
        var p = o + d * hit;
        var row = MathF.Abs(s.YIncrement) > 1e-6f ? p.Y / s.YIncrement : (s.ZInitialOffset - p.Z) / s.ZIncrement;
        var column = MathF.Abs(s.XIncrement) > 1e-6f ? width * 0.5f - p.X / s.XIncrement : 0;
        return new Vector2(column, row);
    }

    /// <summary>The constellation picture's corners in world space (its shape's bounds), if the dome has one for the slot.</summary>
    public static (Vector3 Min, Vector3 Max)? Picture(NifModel dome, int slot) =>
        dome.Shapes.FirstOrDefault(sh => sh.Name.StartsWith($"cImage{slot:00}", StringComparison.OrdinalIgnoreCase)) is { } shape ? (shape.BoundsMin, shape.BoundsMax) : null;

    /// <summary>A scene object for a model at a world place (no record behind it).</summary>
    public static SceneObject Object(string model, Vector3 position, float scale)
    {
        var transform = new NifTransform(Matrix3.Identity, position, scale);
        return new SceneObject
        {
            Reference = Mutagen.Bethesda.Plugins.FormKey.Null, Base = Mutagen.Bethesda.Plugins.FormKey.Null, BaseSignature = "STAT", Kind = SceneObjectKind.Mesh,
            ModelPath = model, Position = position, Rotation = Vector3.Zero, Scale = scale, Transform = transform, World = transform.ToMatrix(),
        };
    }
}
