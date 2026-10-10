using System.Drawing;
using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data;
using StbEditor.Data.Catalog;

namespace StbEditor.App.Panels;

/// <summary>World → New Interior Cell / New Worldspace: fills a draft record and opens it.</summary>
internal sealed class NewRecordDialog(EditorApp app)
{
    private const string PopupId = "New record##newrecord";

    private enum Kind { InteriorCell, Worldspace }

    private bool _open;
    private Kind _kind;
    private string _editorId = string.Empty;
    private string _name = string.Empty;
    private string? _error;

    // Interior
    private int _template; // 0 = custom
    private Vector3 _ambient, _directional, _fog;
    private float _fogNear, _fogFar;
    private List<CatalogRecord> _templates = [];

    // Worldspace
    private int _climate, _water;
    private float _waterHeight, _landHeight = -2048f;
    private bool _smallWorld = true, _hasWater = true;
    private int _cells = 1;
    private List<CatalogRecord> _climates = [];
    private List<CatalogRecord> _waters = [];

    public void ShowInteriorCell() => Show(Kind.InteriorCell);
    public void ShowWorldspace() => Show(Kind.Worldspace);

    private void Show(Kind kind)
    {
        if (app.Session is not { } session) return;
        if (app.ActivePlugin is null)
        {
            app.ShowActiveFileDialog("New records go into the active file — choose it first.");
            return;
        }

        _kind = kind;
        _open = true;
        _error = null;
        _editorId = kind == Kind.InteriorCell ? "NewInteriorCell" : "NewWorldspace";
        _name = string.Empty;

        var catalog = session.Catalog;
        _templates = Sorted(catalog, "LGTM");
        _climates = Sorted(catalog, "CLMT");
        _waters = Sorted(catalog, "WATR");
        _template = 0;
        _climate = Math.Max(0, _climates.FindIndex(c => c.EditorId == "DefaultClimate"));
        _water = Math.Max(0, _waters.FindIndex(w => w.EditorId == "DefaultWater"));

        var d = LightingSpec.Default;
        _ambient = ToVector(d.Ambient);
        _directional = ToVector(d.Directional);
        _fog = ToVector(d.Fog);
        _fogNear = d.FogNear;
        _fogFar = d.FogFar;
    }

    private static List<CatalogRecord> Sorted(RecordCatalog catalog, string signature) =>
        (catalog.BySignature.GetValueOrDefault(signature) ?? []).OrderBy(r => r.EditorId, StringComparer.OrdinalIgnoreCase).ToList();

    public void Draw()
    {
        if (_open && !ImGui.IsPopupOpen(PopupId)) ImGui.OpenPopup(PopupId);

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(560, 0) * (ImGui.GetFontSize() / 16f), ImGuiCond.Appearing);

        var open = true;
        if (!ImGui.BeginPopupModal(PopupId, ref open, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!open) _open = false;
            return;
        }

        if (app.Session is not { } session || app.ActivePlugin is not { } active)
        {
            Close();
            ImGui.EndPopup();
            return;
        }

        ImGui.Text(_kind == Kind.InteriorCell ? "New interior cell" : "New worldspace");
        ImGui.TextColored(Theme.TextDim, $"Created in {active.Name} when you save (Ctrl+S); Ctrl+Z removes it.");
        ImGui.Separator();

        var labelWidth = ImGui.GetFontSize() * 9;
        Field("Editor ID", labelWidth);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##eid", ref _editorId, 200);
        Field("Name (shown in game)", labelWidth);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##name", ref _name, 200);

        if (_kind == Kind.InteriorCell) DrawInterior(labelWidth);
        else DrawWorldspace(labelWidth);

        if (_error is not null) ImGui.TextColored(Theme.Error, _error);
        ImGui.Spacing();

        var width = ImGui.GetFontSize() * 8;
        if (ImGui.Button("Create", new Vector2(width, 0))) Create(session, active);
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(width, 0)) || !open) Close();

        if (!_open) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private static void Field(string label, float width)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.SameLine(width);
    }

    private void DrawInterior(float labelWidth)
    {
        Field("Lighting template", labelWidth);
        ImGui.SetNextItemWidth(-1);
        var current = _template == 0 ? "None — values below" : _templates[_template - 1].EditorId ?? "?";
        if (ImGui.BeginCombo("##lgtm", current, ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.Selectable("None — values below", _template == 0)) _template = 0;
            for (var i = 0; i < _templates.Count; i++)
                if (ImGui.Selectable($"{_templates[i].EditorId}##{i}", _template == i + 1)) _template = i + 1;
            ImGui.EndCombo();
        }

        if (_template != 0)
        {
            ImGui.TextColored(Theme.TextDim, "The cell inherits everything from the template.");
            return;
        }

        Field("Ambient", labelWidth);
        ImGui.ColorEdit3("##amb", ref _ambient);
        Field("Directional", labelWidth);
        ImGui.ColorEdit3("##dir", ref _directional);
        Field("Fog colour", labelWidth);
        ImGui.ColorEdit3("##fog", ref _fog);
        Field("Fog near / far", labelWidth);
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6);
        ImGui.DragFloat("##fn", ref _fogNear, 8f, 0f, 100000f, "%.0f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6);
        ImGui.DragFloat("##ff", ref _fogFar, 32f, 0f, 1000000f, "%.0f");
    }

    private void DrawWorldspace(float labelWidth)
    {
        Field("Climate", labelWidth);
        Combo("##clmt", _climates, ref _climate);
        ImGui.Checkbox("Water", ref _hasWater);
        if (_hasWater)
        {
            ImGui.SameLine(labelWidth);
            Combo("##watr", _waters, ref _water);
            Field("Water height", labelWidth);
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7);
            ImGui.DragFloat("##wh", ref _waterHeight, 8f, -100000f, 100000f, "%.0f");
        }
        Field("Default land height", labelWidth);
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7);
        ImGui.DragFloat("##lh", ref _landHeight, 8f, -100000f, 100000f, "%.0f");
        ImGui.Checkbox("Small world (no LOD, like an interior-sized exterior)", ref _smallWorld);
        Field("Starting cells", labelWidth);
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7);
        ImGui.SliderInt("##cells", ref _cells, 1, 9, "%d per side");
        ImGui.TextColored(Theme.TextDim, "Cells around (0, 0); more are made wherever you place objects.");
    }

    private static void Combo(string id, List<CatalogRecord> items, ref int index)
    {
        ImGui.SetNextItemWidth(-1);
        if (items.Count == 0)
        {
            ImGui.TextColored(Theme.TextDim, "(none in the load order)");
            return;
        }
        index = Math.Clamp(index, 0, items.Count - 1);
        if (!ImGui.BeginCombo(id, items[index].EditorId ?? "?", ImGuiComboFlags.HeightLarge)) return;
        for (var i = 0; i < items.Count; i++)
            if (ImGui.Selectable($"{items[i].EditorId}##{i}", i == index)) index = i;
        ImGui.EndCombo();
    }

    private void Create(EditorSession session, ActivePlugin active)
    {
        try
        {
            var id = _editorId.Trim();
            if (!NewRecords.IsValidEditorId(id)) throw new InvalidOperationException("Editor ID: letters, digits and _, starting with a letter");
            if (session.Catalog.ByKey.Values.Any(r => string.Equals(r.EditorId, id, StringComparison.OrdinalIgnoreCase))
                || app.Document.Drafts.Values.Any(d => string.Equals(d.EditorId, id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"{id} is already used");

            var release = session.Game.Release;
            var name = _name.Trim();
            if (_kind == Kind.InteriorCell)
            {
                var lookup = new RecordLookup(session.Workspace, session.Catalog);
                var template = _template == 0 ? null : lookup.Resolve<ILightingTemplateGetter>(_templates[_template - 1].FormKey);
                var spec = new LightingSpec(ToColor(_ambient), ToColor(_directional), ToColor(_fog), _fogNear, _fogFar, 45, 45);
                var draft = NewRecords.InteriorCell(active.Allocate(), release, id, name, spec, template);
                app.Document.AddDraft($"New cell {id}", draft);
                Log.Info($"new interior cell {id} {draft.Key}");
                Close();
                app.RenderWindow.OpenCell(app, draft.Key);
            }
            else
            {
                FormKey? climate = _climates.Count > 0 ? _climates[_climate].FormKey : null;
                FormKey? water = _hasWater && _waters.Count > 0 ? _waters[_water].FormKey : null;
                var world = NewRecords.Worldspace(active.Allocate(), active.Allocate(), release, id, name, climate, water,
                    _hasWater ? _waterHeight : -1e5f, _landHeight, _smallWorld);

                var drafts = new List<Draft> { world };
                var half = (_cells - 1) / 2;
                for (var y = -half; y < _cells - half; y++)
                    for (var x = -half; x < _cells - half; x++)
                        drafts.Add(NewRecords.ExteriorCell(active.Allocate(), release, world.Key, x, y));

                app.Document.Execute($"New worldspace {id}", [], drafts);
                Log.Info($"new worldspace {id} {world.Key} with {drafts.Count - 1} cell(s)");
                Close();
                app.RenderWindow.OpenExterior(app, world.Key, 0, 0);
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private void Close()
    {
        _open = false;
        ImGui.CloseCurrentPopup();
    }

    private static Vector3 ToVector(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    private static Color ToColor(Vector3 v) =>
        Color.FromArgb((int)Math.Clamp(v.X * 255f + 0.5f, 0, 255), (int)Math.Clamp(v.Y * 255f + 0.5f, 0, 255), (int)Math.Clamp(v.Z * 255f + 0.5f, 0, 255));
}
