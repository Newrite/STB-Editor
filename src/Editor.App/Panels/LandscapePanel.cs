using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data.Catalog;
using StbEditor.Render;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>CK's Landscape Edit dialog: brush mode and size, the texture to paint, flat land for new cells.</summary>
internal sealed class LandscapePanel : Panel
{
    private List<(CatalogRecord Record, string Texture)> _textures = [];
    private string _filter = string.Empty;
    private float _flatHeight;
    private SceneBuilder? _builder;
    private string _heightmapPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "STB Editor", "Heightmaps", "heightmap.r16");
    private string? _heightmapResult;

    public override string Name => "Landscape";

    public override void OnSessionChanged(EditorApp app)
    {
        var session = app.Session!;
        _builder = new SceneBuilder(session.Workspace, session.Catalog);
        _textures = (session.Catalog.BySignature.GetValueOrDefault("LTEX") ?? [])
            .OrderBy(r => r.EditorId, StringComparer.OrdinalIgnoreCase)
            .Select(r => (r, _builder.LandTexture(r.FormKey)))
            .ToList();
    }

    protected override unsafe void DrawContent(EditorApp app)
    {
        if (!RequireSession(app)) return;
        var window = app.RenderWindow;
        var brush = window.Brush;

        var editing = window.LandscapeMode;
        if (editing) ImGui.PushStyleColor(ImGuiCol.Button, Theme.ButtonActive);
        if (ImGui.Button(editing ? T("Editing landscape (H)") : T("Edit landscape (H)"))) window.SetLandscapeMode(!editing);
        if (editing) ImGui.PopStyleColor();

        var scene = window.Scene;
        if (scene is null || scene.IsInterior)
        {
            ImGui.TextColored(Theme.TextDim, T("Open an exterior cell (Cell View) to edit its land."));
            return;
        }

        ImGui.Separator();
        var mode = (int)brush.Mode;
        ImGui.RadioButton(T("Raise"), ref mode, (int)LandBrushMode.Raise);
        ImGui.SameLine();
        ImGui.RadioButton(T("Lower"), ref mode, (int)LandBrushMode.Lower);
        ImGui.SameLine();
        ImGui.RadioButton(T("Flatten"), ref mode, (int)LandBrushMode.Flatten);
        ImGui.SameLine();
        ImGui.RadioButton(T("Smooth"), ref mode, (int)LandBrushMode.Smooth);
        ImGui.SameLine();
        ImGui.RadioButton(T("Paint"), ref mode, (int)LandBrushMode.Paint);
        ImGui.SameLine();
        ImGui.RadioButton(T("Colour"), ref mode, (int)LandBrushMode.Color);
        brush.Mode = (LandBrushMode)mode;

        var radius = brush.Radius;
        ImGui.SetNextItemWidth(-ImGui.GetFontSize() * 5);
        if (ImGui.SliderFloat(T("Radius"), ref radius, 32f, 4096f, T("%.0f units"), ImGuiSliderFlags.Logarithmic)) brush.Radius = radius;
        var strength = brush.Strength;
        var heights = brush.Mode is LandBrushMode.Raise or LandBrushMode.Lower;
        ImGui.SetNextItemWidth(-ImGui.GetFontSize() * 5);
        if (ImGui.SliderFloat(T("Strength"), ref strength, heights ? 10f : 1f, heights ? 2000f : 100f, heights ? T("%.0f units/s") : "%.0f %%", ImGuiSliderFlags.Logarithmic))
            brush.Strength = strength;

        ImGui.TextColored(Theme.TextDim, brush.Mode switch
        {
            LandBrushMode.Raise or LandBrushMode.Lower => T("LMB drag; Shift swaps raise / lower."),
            LandBrushMode.Flatten => T("Levels to the height where the stroke started."),
            LandBrushMode.Smooth => T("Evens out bumps under the brush."),
            LandBrushMode.Color => T("Tints the land's vertex colours towards the colour below (white = no tint)."),
            _ => T("Paints the texture below; painting a quadrant's base texture erases the others."),
        });

        if (brush.Mode == LandBrushMode.Paint) DrawTextures(app, brush);
        if (brush.Mode == LandBrushMode.Color)
        {
            var c = brush.Color;
            var rgb = new Vector3((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f);
            ImGui.SetNextItemWidth(-ImGui.GetFontSize() * 5);
            if (ImGui.ColorEdit3(T("Colour") + "##vcolor", ref rgb))
                brush.Color = (uint)Math.Round(rgb.X * 255) | ((uint)Math.Round(rgb.Y * 255) << 8) | ((uint)Math.Round(rgb.Z * 255) << 16);
        }

        ImGui.Separator();
        ImGui.TextColored(Theme.TextDim, T("Loaded cells without land:"));
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7);
        ImGui.DragFloat("##flath", ref _flatHeight, 8f, -100000f, 100000f, "height %.0f");
        ImGui.SameLine();
        if (ImGui.Button(T("Create flat land")))
        {
            var count = window.CreateFlatLand(app, _flatHeight);
            Log.Info(count == 0 ? "landscape: every loaded cell already has land" : $"landscape: created flat land in {count} cell(s)");
        }
        ImGui.TextColored(Theme.TextDim, T("Edited land is saved into the active file (Ctrl+S); each stroke is one Ctrl+Z."));

        ImGui.Separator();
        ImGui.TextColored(Theme.TextDim, T("Heightmap of the loaded cells (.r16, 16-bit, north up; the .json beside it keeps the cells and the height range):"));
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##heightmap", ref _heightmapPath, 1024);
        if (ImGui.Button(T("Export heightmap"))) _heightmapResult = window.ExportHeightmap(app, _heightmapPath.Trim().Trim('"'));
        ImGui.SameLine();
        if (ImGui.Button(T("Import heightmap"))) _heightmapResult = window.ImportHeightmap(app, _heightmapPath.Trim().Trim('"'));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("The loaded cells inside the map take its heights (edit it in World Machine, Gaea or an image editor; keep its size); one Ctrl+Z"));
        if (_heightmapResult is not null) ImGui.TextWrapped(_heightmapResult);
    }

    private unsafe void DrawTextures(EditorApp app, LandscapeBrush brush)
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##ltexfilter", T("Filter landscape textures"), ref _filter, 128);

        var textures = app.RenderWindow.Textures;
        var icon = ImGui.GetFontSize() * 2.4f;
        if (!ImGui.BeginChild("##ltex", new Vector2(0, 0), ImGuiChildFlags.Borders)) { ImGui.EndChild(); return; }

        var rows = _textures.Where(t => _filter.Length == 0 || (t.Record.EditorId?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        var clipper = new ImGuiListClipper();
        clipper.Begin(rows.Count, icon + ImGui.GetStyle().ItemSpacing.Y);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var (record, texture) = rows[i];
                ImGui.PushID(i);
                var start = ImGui.GetCursorPos();
                if (ImGui.Selectable("##pick", brush.Texture == record.FormKey, ImGuiSelectableFlags.None, new Vector2(0, icon)))
                    brush.Texture = record.FormKey;
                ImGui.SetCursorPos(start);
                if (textures?.Get(texture) is { } view)
                    ImGui.Image(new ImTextureRef(null, new ImTextureID((ulong)view.NativePointer)), new Vector2(icon, icon));
                else
                    ImGui.Dummy(new Vector2(icon, icon));
                ImGui.SameLine();
                ImGui.BeginGroup();
                ImGui.Text(record.EditorId ?? "?");
                ImGui.TextColored(Theme.TextDim, $"{app.Session!.Workspace.FormIdText(record.FormKey)} · {Path.GetFileName(texture)}");
                ImGui.EndGroup();
                ImGui.PopID();
            }
        }
        clipper.End();
        ImGui.EndChild();
    }
}
