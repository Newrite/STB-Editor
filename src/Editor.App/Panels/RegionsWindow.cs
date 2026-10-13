using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// World → Regions (CK: Region Editor): the open worldspace's regions — borders on the land, the Region tool for their
/// corners, new regions and areas around the view's centre, and what each region sets (weathers, map name, objects,
/// grass, sounds — edited in the region's form).
/// </summary>
internal sealed class RegionsWindow
{
    private bool _open;
    private string _filter = "";

    public void Show(EditorApp app)
    {
        _open = true;
        app.RenderWindow.RegionsShown = true;
    }

    public void Draw(EditorApp app)
    {
        if (!_open || app.Session is not { } session) return;
        ImGui.SetNextWindowSize(app.Scaled(520, 620), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Regions") + "###regions", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            var window = app.RenderWindow;
            if (window.Scene is not { IsInterior: false })
            {
                ImGui.TextColored(Theme.TextDim, T("Open an exterior cell: regions belong to a worldspace."));
                ImGui.End();
                return;
            }
            var shown = window.RegionsShown;
            if (ImGui.Checkbox(T("Show region borders"), ref shown)) window.RegionsShown = shown;
            ImGui.SameLine();
            var editing = window.RegionMode;
            if (editing) ImGui.PushStyleColor(ImGuiCol.Button, Theme.ButtonActive);
            if (ImGui.Button(editing ? T("Editing regions") : T("Edit regions"))) window.SetRegionMode(!editing);
            if (editing) ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Region tool: click inside a region to select it, drag its corners, Shift+click on its border adds a corner, Del removes the selected one"));
            if (ImGui.Button(T("New region here"))) window.AddRegionArea(app, window.ViewCentreOnGround(), true);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("A region of the active file with a square area one cell wide in the middle of the view"));
            ImGui.SameLine();
            ImGui.BeginDisabled(window.SelectedRegion is null);
            if (ImGui.Button(T("Add area here"))) window.AddRegionArea(app, window.ViewCentreOnGround(), false);
            ImGui.EndDisabled();

            var regions = window.Regions(app);
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##regionfilter", TF($"Filter {regions.Count} region(s)"), ref _filter, 128);
            var listHeight = ImGui.GetContentRegionAvail().Y * 0.55f;
            if (ImGui.BeginChild("##regionlist", new Vector2(0, listHeight), ImGuiChildFlags.Borders))
            {
                foreach (var (key, record) in regions)
                {
                    var name = record.EditorID ?? session.Workspace.FormIdText(key);
                    if (_filter.Length > 0 && !name.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;
                    if (record.MapColor is { } c)
                    {
                        var at = ImGui.GetCursorScreenPos();
                        var h = ImGui.GetTextLineHeight();
                        ImGui.GetWindowDrawList().AddRectFilled(at + new Vector2(0, 2), at + new Vector2(h, h - 2), ImGui.GetColorU32(new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, 1)));
                        ImGui.Dummy(new Vector2(h, h));
                        ImGui.SameLine();
                    }
                    var points = record.RegionAreas.Sum(a => a.RegionPointListData.Count);
                    if (ImGui.Selectable($"{name}   {TF($"{record.RegionAreas.Count} area(s), {points} corner(s)")}{(app.Document.Override(key) is not null ? "  *" : "")}##{key}", window.SelectedRegion == key))
                        window.SelectedRegion = key;
                    if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) app.FormWindows.Open(key);
                }
            }
            ImGui.EndChild();

            if (window.SelectedRegion is { } selected && regions.FirstOrDefault(r => r.Key == selected).Record is { } region)
            {
                ImGui.SeparatorText(region.EditorID ?? session.Workspace.FormIdText(selected));
                if (ImGui.SmallButton(T("Open form"))) app.FormWindows.Open(selected);
                ImGui.SameLine();
                ImGui.BeginDisabled(window.SelectedRegionPoint is null);
                if (ImGui.SmallButton(T("Delete corner (Del)"))) window.DeleteRegionPoint(app);
                ImGui.EndDisabled();
                var lookup = new RecordLookup(session.Workspace, session.Catalog);
                if (region.Map?.Name?.String is { Length: > 0 } map) ImGui.Text(TF($"Map name: {map}"));
                if (region.Weather is { Weathers.Count: > 0 } weather)
                    ImGui.TextWrapped(TF($"Weathers: {string.Join(", ", weather.Weathers.Select(w => $"{session.Catalog.Get(w.Weather.FormKey)?.EditorId ?? "?"} {w.Chance}%"))}"));
                if (region.Objects is { Objects.Count: > 0 } objects) ImGui.Text(TF($"Objects: {objects.Objects.Count}"));
                if (region.Grasses is { Grasses.Count: > 0 } grasses) ImGui.Text(TF($"Grasses: {grasses.Grasses.Count}"));
                if (region.Sounds is { } sounds && (sounds.Sounds.Count > 0 || !sounds.Music.IsNull))
                    ImGui.Text(TF($"Sounds: {sounds.Sounds.Count}") + (sounds.Music.IsNull ? "" : TF($", music {session.Catalog.Get(sounds.Music.FormKey)?.EditorId}")));
                for (var a = 0; a < region.RegionAreas.Count; a++)
                    ImGui.TextColored(Theme.TextDim, TF($"Area {a + 1}: {region.RegionAreas[a].RegionPointListData.Count} corner(s), edge fall-off {region.RegionAreas[a].EdgeFallOff ?? 0}"));
                ImGui.TextColored(Theme.TextDim, T("Weathers, map name, objects, grass and sounds are edited in the region's form. Generating objects and grass from a region (CK: Generate) is not done here."));
            }
        }
        ImGui.End();
    }
}
