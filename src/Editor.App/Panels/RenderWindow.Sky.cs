using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Data;
using StbEditor.Render;
using static StbEditor.App.Ui.Loc;
using Scene = StbEditor.Render.Scene;

namespace StbEditor.App.Panels;

/// <summary>
/// Outdoors: the view lit as a chosen weather at an hour (CK: sky and time of day in the render window) — the worldspace
/// climate's weathers first, then every weather; «Default light» goes back to the editor's fixed sun.
/// </summary>
internal sealed partial class RenderWindow
{
    private FormKey? _weather;
    private float _hour = 12f;
    private (Scene? Scene, FormKey? Weather, float Hour) _skyApplied;
    private Vector4? _skyColour;
    private List<(FormKey Key, string Label, bool Climate)>? _weathers;
    private (object? Session, FormKey? World) _weathersFor;
    private string _weatherFilter = "";

    public FormKey? Weather => _weather;
    public float Hour => _hour;

    /// <summary>The weather to light the outdoors with (null: the default light) and the hour (0–24).</summary>
    public void SetSky(FormKey? weather, float hour)
    {
        _weather = weather;
        _hour = Math.Clamp(hour, 0, 24);
    }

    /// <summary>Each frame outdoors: the chosen weather's light on the scene when anything changed.</summary>
    private void ApplySky(EditorApp app)
    {
        if (_scene is not { IsInterior: false } scene || app.Session is not { } session) { _skyColour = null; return; }
        if (_skyApplied.Scene == scene && _skyApplied.Weather == _weather && _skyApplied.Hour == _hour) return;
        _skyApplied = (scene, _weather, _hour);
        var lookup = new RecordLookup(session.Workspace, session.Catalog);
        if (_weather is { } key && lookup.Resolve<IWeatherGetter>(key) is { } weather)
        {
            var climate = _world is { } w && lookup.Resolve<IWorldspaceGetter>(w)?.Climate is { IsNull: false } c ? lookup.Resolve<IClimateGetter>(c.FormKey) : null;
            var (lighting, sky) = WeatherLighting.Compute(weather, _hour, DayTimes.Of(climate));
            scene.Lighting = lighting;
            _skyColour = sky;
        }
        else
        {
            scene.Lighting = SceneLighting.DefaultExterior;
            _skyColour = null;
        }
    }

    private List<(FormKey Key, string Label, bool Climate)> Weathers(EditorApp app)
    {
        var session = app.Session!;
        if (_weathers is not null && ReferenceEquals(_weathersFor.Session, session) && _weathersFor.World == _world) return _weathers;
        var lookup = new RecordLookup(session.Workspace, session.Catalog);
        var climate = new List<FormKey>();
        if (_world is { } w && lookup.Resolve<IWorldspaceGetter>(w)?.Climate is { IsNull: false } c && lookup.Resolve<IClimateGetter>(c.FormKey) is { } cl)
            climate.AddRange(cl.WeatherTypes.Where(t => !t.Weather.IsNull).Select(t => t.Weather.FormKey));
        string Label(FormKey k) => session.Catalog.Get(k)?.EditorId ?? session.Workspace.FormIdText(k);
        _weathers = [.. climate.Distinct().Select(k => (k, Label(k), true)),
            .. (session.Catalog.BySignature.GetValueOrDefault("WTHR") ?? []).Where(r => !climate.Contains(r.FormKey))
                .OrderBy(r => r.EditorId, StringComparer.OrdinalIgnoreCase).Select(r => (r.FormKey, Label(r.FormKey), false))];
        _weathersFor = (session, _world);
        return _weathers;
    }

    /// <summary>Toolbar button and popup: weather and hour.</summary>
    private void DrawSky(EditorApp app)
    {
        if (_world is null || app.Session is not { } session) return;
        var label = _weather is { } k ? $"{session.Catalog.Get(k)?.EditorId ?? "?"} {_hour:0.#}h" : T("Sky");
        if (_weather is not null) ImGui.PushStyleColor(ImGuiCol.Button, Theme.ButtonActive);
        if (ImGui.Button(label + "###sky")) ImGui.OpenPopup("##skypopup");
        if (_weather is not null) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Light the view as a weather at an hour of the day (CK: sky / time of day)"));
        ImGui.SameLine();
        if (!ImGui.BeginPopup("##skypopup")) return;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 16);
        var hour = _hour;
        if (ImGui.SliderFloat("##hour", ref hour, 0f, 24f, T("hour %.1f"))) _hour = hour;
        if (ImGui.SmallButton(T("Default light"))) _weather = null;
        ImGui.SameLine();
        foreach (var (text, h) in new[] { ("6", 6f), ("9", 9f), ("12", 12f), ("18", 18f), ("21", 21f), ("0", 0f) })
        {
            if (ImGui.SmallButton(text + "##h" + text)) _hour = h;
            ImGui.SameLine();
        }
        ImGui.NewLine();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 16);
        ImGui.InputTextWithHint("##weatherfilter", T("Filter weathers"), ref _weatherFilter, 64);
        if (ImGui.BeginChild("##weathers", new Vector2(ImGui.GetFontSize() * 18, ImGui.GetFontSize() * 16), ImGuiChildFlags.Borders))
        {
            var climateShown = false;
            foreach (var (key, name, climate) in Weathers(app))
            {
                if (_weatherFilter.Length > 0 && !name.Contains(_weatherFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (climate && !climateShown) { ImGui.TextColored(Theme.TextDim, T("This worldspace's climate:")); climateShown = true; }
                if (!climate && climateShown) { ImGui.Separator(); climateShown = false; }
                if (ImGui.Selectable($"{name}##{key}", _weather == key)) _weather = key;
            }
        }
        ImGui.EndChild();
        ImGui.EndPopup();
    }
}
