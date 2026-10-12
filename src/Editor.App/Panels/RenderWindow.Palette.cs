using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using StbEditor.App.Services;
using StbEditor.Render;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>The object palette (CK: Object Palette): base objects a click places at random, with random turn and size.</summary>
internal sealed class ObjectPalette
{
    public List<FormKey> Objects { get; } = [];
    public float ScaleMin { get; set; } = 0.85f;
    public float ScaleMax { get; set; } = 1.15f;
    public bool RandomYaw { get; set; } = true;
    /// <summary>Random tilt up to this many degrees (rocks, logs); 0 keeps objects upright.</summary>
    public float MaxTilt { get; set; }
    /// <summary>How far below the surface the origin goes (so rocks sit in the ground).</summary>
    public float Sink { get; set; }
}

/// <summary>
/// Palette mode of the render window: a click places one object of the palette where the cursor points — chosen at
/// random, turned and sized at random within the palette's ranges — as its own undo step, so clutter, rocks or trees
/// go down click by click.
/// </summary>
internal sealed partial class RenderWindow
{
    public ObjectPalette Palette { get; } = new();
    private readonly Random _paletteRandom = new();

    public bool PaletteMode => _tool == EditTool.Palette;

    public void SetPaletteMode(bool on) => _tool = on ? EditTool.Palette : EditTool.Select;

    private void HandlePalette(EditorApp app, Vector2 mouse, Vector2 size, bool hovered)
    {
        var io = ImGui.GetIO();
        if (!hovered || !ImGui.IsMouseClicked(ImGuiMouseButton.Left) || io.KeyAlt || _scene is null) return;
        var (origin, direction) = _camera.Ray(mouse, size);
        var point = Picking.Pick(_scene, origin, direction, _renderer!.Options)?.Point ?? Picking.PickTerrain(_scene, origin, direction);
        if (point is { } p) PlacePaletteObject(app, p);
    }

    /// <summary>One palette object at <paramref name="point"/> (autotests call it directly); null when nothing was placed.</summary>
    public SceneObject? PlacePaletteObject(EditorApp app, Vector3 point)
    {
        if (_scene is null || app.Session is not { } session) return null;
        if (Palette.Objects.Count == 0)
        {
            Log.Warn(T("The object palette is empty: add objects to it first (World → Object palette)"));
            return null;
        }
        if (app.ActivePlugin is null)
        {
            app.ShowActiveFileDialog(T("New objects need an active file for their FormIDs. Choose one, then drop again."));
            return null;
        }
        var key = Palette.Objects[_paletteRandom.Next(Palette.Objects.Count)];
        if (session.Catalog.Get(key)?.Signature is not { } signature || !StbEditor.Data.Catalog.RecordTypes.IsPlaceable(signature)) return null;
        float Random(float min, float max) => min + (float)_paletteRandom.NextDouble() * (max - min);
        var tilt = Palette.MaxTilt * MathF.PI / 180f;
        var rotation = new Vector3(Random(-tilt, tilt), Random(-tilt, tilt), Palette.RandomYaw ? Random(0, MathF.Tau) : 0);
        var scale = MathF.Round(Random(Math.Min(Palette.ScaleMin, Palette.ScaleMax), Math.Max(Palette.ScaleMin, Palette.ScaleMax)), 2);
        return CreateNew(app, key, signature, point - new Vector3(0, 0, Palette.Sink), rotation, Math.Clamp(scale, 0.01f, 10f));
    }
}
