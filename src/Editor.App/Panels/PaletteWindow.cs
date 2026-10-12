using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>World → Object palette: what the palette places and how (CK: Object Palette window).</summary>
internal sealed class PaletteWindow
{
    private bool _open;

    public void Show() => _open = true;

    public void Draw(EditorApp app)
    {
        if (!_open || app.Session is not { } session) return;
        var window = app.RenderWindow;
        var palette = window.Palette;
        ImGui.SetNextWindowSize(app.Scaled(460, 480), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Object palette") + "###palette", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            var on = window.PaletteMode;
            if (on) ImGui.PushStyleColor(ImGuiCol.Button, Theme.ButtonActive);
            if (ImGui.Button(on ? T("Placing with the palette: click in the Render Window") : T("Place with the palette"))) window.SetPaletteMode(!on);
            if (on) ImGui.PopStyleColor();
            ImGui.TextColored(Theme.TextDim, T("Each click places one of these objects at random, turned and sized at random; each is one Ctrl+Z."));

            ImGui.SeparatorText(TF($"Objects ({palette.Objects.Count})"));
            FormKey? added = null;
            if (FormField.Draw(app, "##paletteadd", ref added, null) && added is { } key)
            {
                var signature = session.Catalog.Get(key)?.Signature;
                if (signature is null || !StbEditor.Data.Catalog.RecordTypes.IsPlaceable(signature)) Log.Warn(TF($"{session.Workspace.FormIdText(key)} [{signature}] cannot be placed in the world"));
                else if (!palette.Objects.Contains(key)) palette.Objects.Add(key);
            }
            if (app.Selected is { } selected && session.Catalog.Get(selected) is { } record && StbEditor.Data.Catalog.RecordTypes.IsPlaceable(record.Signature)
                && !palette.Objects.Contains(selected) && ImGui.SmallButton(TF($"+ {record.EditorId ?? selected.ToString()} (selected)")))
                palette.Objects.Add(selected);
            for (var i = 0; i < palette.Objects.Count; i++)
            {
                ImGui.PushID(i);
                if (ImGui.SmallButton("x")) { palette.Objects.RemoveAt(i); ImGui.PopID(); break; }
                ImGui.SameLine();
                ImGui.Text(FormField.Describe(app, palette.Objects[i]));
                ImGui.PopID();
            }
            if (palette.Objects.Count == 0) ImGui.TextColored(Theme.TextDim, T("Type a name above, or select a base object in the Object Window and add it."));

            ImGui.SeparatorText(T("Randomness"));
            var min = palette.ScaleMin;
            var max = palette.ScaleMax;
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12);
            if (ImGui.DragFloatRange2(T("Scale"), ref min, ref max, 0.01f, 0.05f, 5f, "%.2f", "%.2f")) { palette.ScaleMin = min; palette.ScaleMax = max; }
            var yaw = palette.RandomYaw;
            if (ImGui.Checkbox(T("Random turn"), ref yaw)) palette.RandomYaw = yaw;
            var tilt = palette.MaxTilt;
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12);
            if (ImGui.SliderFloat(T("Random tilt"), ref tilt, 0f, 45f, T("%.0f°"))) palette.MaxTilt = tilt;
            var sink = palette.Sink;
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12);
            if (ImGui.DragFloat(T("Sink into the ground"), ref sink, 1f, 0f, 512f, T("%.0f units"))) palette.Sink = sink;
        }
        ImGui.End();
    }
}
