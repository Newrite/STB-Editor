using Hexa.NET.ImGui;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Render;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// A named set of view settings: what the toolbar shows (markers, water, land, grass, fog, collision, borders, navmesh,
/// FX, animation, NPC tint, disabled references), Show / Hide groups, shading, brightness, fly speed and the grid radius.
/// Kept in settings.json; the view as it was last is restored on the next start.
/// </summary>
internal sealed class ViewPreset
{
    public string Name { get; set; } = "";
    public ShadingMode Shading { get; set; } = ShadingMode.Lit;
    public bool Wireframe { get; set; }
    public bool Markers { get; set; } = true;
    public bool Disabled { get; set; } = true;
    public bool Water { get; set; } = true;
    public bool Land { get; set; } = true;
    public bool Grass { get; set; } = true;
    public bool Fog { get; set; } = true;
    public bool Collision { get; set; }
    public bool Borders { get; set; }
    public bool Navmesh { get; set; }
    public bool Particles { get; set; } = true;
    public bool Animate { get; set; } = true;
    public bool HighlightActors { get; set; } = true;
    public float Brightness { get; set; } = 1.6f;
    public List<string> HiddenGroups { get; set; } = [];
    public bool CurrentCellOnly { get; set; }
    public float FlySpeed { get; set; } = 800f;
    public int GridRadius { get; set; } = 2;

    /// <summary>Presets every user has (names are translated when shown; the user's own ones come after them).</summary>
    public static readonly ViewPreset[] BuiltIn =
    [
        new() { Name = "Default" },
        new() { Name = "Clean view (as in the game)", Markers = false, Disabled = false, HighlightActors = false, Borders = false },
        new() { Name = "Navmesh work", Navmesh = true, Grass = false, Particles = false, Fog = false, Markers = false },
        new() { Name = "Placing objects", Grass = false, Borders = true, Fog = false },
        new() { Name = "Weak PC", Grass = false, Particles = false, Animate = false, Fog = false, GridRadius = 1 },
    ];
}

internal sealed partial class RenderWindow
{
    private string _presetName = "";

    /// <summary>The view as it is now.</summary>
    public ViewPreset CaptureView(string name)
    {
        var o = _renderer?.Options ?? new RenderOptions();
        return new ViewPreset
        {
            Name = name, Shading = o.Shading, Wireframe = o.Wireframe, Markers = o.ShowMarkers, Disabled = o.ShowInitiallyDisabled,
            Water = o.ShowWater, Land = o.ShowTerrain, Grass = o.ShowGrass, Fog = o.Fog, Collision = o.ShowCollision, Borders = o.CellBorders,
            Navmesh = o.ShowNavmesh, Particles = o.ShowParticles, Animate = o.Animate, HighlightActors = o.HighlightActors,
            Brightness = o.Brightness, HiddenGroups = [.. o.HiddenGroups], CurrentCellOnly = o.CurrentCellOnly,
            FlySpeed = _camera.Speed, GridRadius = _gridRadius,
        };
    }

    /// <summary>Puts a preset's settings on the view; a changed grid radius reloads the open cell.</summary>
    public void ApplyView(EditorApp app, ViewPreset p)
    {
        if (_renderer?.Options is not { } o) return;
        o.Shading = p.Shading;
        o.Wireframe = p.Wireframe;
        o.ShowMarkers = p.Markers;
        o.ShowInitiallyDisabled = p.Disabled;
        o.ShowWater = p.Water;
        o.ShowTerrain = p.Land;
        o.ShowGrass = p.Grass;
        o.Fog = p.Fog;
        o.ShowCollision = p.Collision;
        o.CellBorders = p.Borders;
        o.ShowNavmesh = p.Navmesh;
        o.ShowParticles = p.Particles;
        o.Animate = p.Animate;
        o.HighlightActors = app.Settings.HighlightActors = p.HighlightActors;
        o.Brightness = Math.Clamp(p.Brightness, 0.3f, 4f);
        o.HiddenGroups.Clear();
        foreach (var g in p.HiddenGroups) o.HiddenGroups.Add(g);
        o.CurrentCellOnly = p.CurrentCellOnly;
        _camera.Speed = Math.Clamp(p.FlySpeed, 50f, 20000f);
        var radius = Math.Clamp(p.GridRadius, 0, 5);
        if (radius != _gridRadius)
        {
            _gridRadius = radius;
            if (_world is not null && _cell is { } cell) OpenCell(app, cell);
        }
    }

    /// <summary>On exit: the view as it was, for the next start.</summary>
    public void RememberView(EditorApp app)
    {
        if (_renderer is not null) app.Settings.LastView = CaptureView("");
    }

    /// <summary>Toolbar «Preset» button: built-in and saved presets, save the current view, delete one.</summary>
    private void DrawPresets(EditorApp app)
    {
        if (ImGui.Button(T("Preset") + "###viewpreset")) ImGui.OpenPopup("##presetpopup");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("View presets: what the toolbar shows, Show / Hide, light, fly speed, grid radius"));
        ImGui.SameLine();
        if (!ImGui.BeginPopup("##presetpopup")) return;
        foreach (var preset in ViewPreset.BuiltIn)
            if (ImGui.Selectable(T(preset.Name) + "##builtin" + preset.Name)) ApplyView(app, preset);
        var saved = app.Settings.ViewPresets;
        if (saved.Count > 0) ImGui.Separator();
        for (var i = 0; i < saved.Count; i++)
        {
            ImGui.PushID(i);
            if (ImGui.Selectable(saved[i].Name, false, ImGuiSelectableFlags.None, new System.Numerics.Vector2(ImGui.GetFontSize() * 12, 0))) ApplyView(app, saved[i]);
            ImGui.SameLine();
            if (ImGui.SmallButton("x"))
            {
                saved.RemoveAt(i);
                app.Settings.Save();
                ImGui.PopID();
                break;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Delete this preset"));
            ImGui.PopID();
        }
        ImGui.Separator();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
        ImGui.InputTextWithHint("##presetname", T("Name"), ref _presetName, 64);
        ImGui.SameLine();
        ImGui.BeginDisabled(_presetName.Trim().Length == 0);
        if (ImGui.Button(T("Save the current view")))
        {
            var name = _presetName.Trim();
            saved.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            saved.Add(CaptureView(name));
            app.Settings.Save();
            _presetName = "";
        }
        ImGui.EndDisabled();
        ImGui.EndPopup();
    }
}
