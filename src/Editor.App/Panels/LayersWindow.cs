using System.Text.Json;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>A layer: a named group of references that can be hidden or frozen together.</summary>
internal sealed class Layer
{
    public string Name { get; set; } = "";
    public bool Hidden { get; set; }
    public bool Frozen { get; set; }
    public List<string> References { get; set; } = [];
}

/// <summary>
/// World → Layers (CK: Layers): groups of references to hide (not drawn, not picked) or freeze (drawn, not
/// selectable) while working on what is around them. Editor-only — kept in %LOCALAPPDATA%\STBEditor\layers.json,
/// nothing goes into the plugin.
/// </summary>
internal sealed class LayersWindow
{
    private bool _open;
    private string _newName = "";
    private List<Layer>? _layers;
    private object? _appliedTo;
    private bool _dirty = true;

    private static string FilePath => Path.Combine(AppPaths.Root, "layers.json");

    public IReadOnlyList<Layer> Layers => _layers ??= Load();

    public void Show() => _open = true;

    private static List<Layer> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Layer>>(File.ReadAllText(FilePath)) ?? [] : []; }
        catch (Exception ex) { Log.Warn($"layers: {ex.Message}"); return []; }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_layers, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Warn($"layers: {ex.Message}"); }
        _dirty = true;
    }

    /// <summary>The layers' hidden and frozen references into the render options (on change, and once per renderer).</summary>
    private void Apply(EditorApp app)
    {
        if (app.RenderWindow.Options is not { } options || (!_dirty && ReferenceEquals(_appliedTo, options))) return;
        options.HiddenReferences.Clear();
        options.FrozenReferences.Clear();
        foreach (var layer in Layers)
            foreach (var text in layer.References)
            {
                if (!FormKey.TryFactory(text, out var key)) continue;
                if (layer.Hidden) options.HiddenReferences.Add(key);
                if (layer.Frozen) options.FrozenReferences.Add(key);
            }
        _appliedTo = options;
        _dirty = false;
    }

    /// <summary>A new layer with the given references (autotests and the window).</summary>
    public Layer Add(string name, IEnumerable<FormKey> references)
    {
        _layers ??= Load();
        var layer = new Layer { Name = name, References = references.Select(k => k.ToString()).Distinct().ToList() };
        _layers.Add(layer);
        Save();
        return layer;
    }

    public void Remove(Layer layer)
    {
        _layers ??= Load();
        _layers.Remove(layer);
        Save();
    }

    public void Set(Layer layer, bool? hidden = null, bool? frozen = null)
    {
        if (hidden is { } h) layer.Hidden = h;
        if (frozen is { } f) layer.Frozen = f;
        Save();
    }

    public void Draw(EditorApp app)
    {
        Apply(app);
        if (!_open) return;
        _layers ??= Load();
        var window = app.RenderWindow;
        var selected = window.SelectedObjects.Select(o => o.Reference).ToList();
        ImGui.SetNextWindowSize(app.Scaled(520, 420), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Layers") + "###layers", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.TextColored(Theme.TextDim, T("Groups of references to hide or freeze while you work; kept by the editor, not in the plugin."));
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 14);
            ImGui.InputTextWithHint("##newlayer", T("new layer name"), ref _newName, 64);
            ImGui.SameLine();
            ImGui.BeginDisabled(_newName.Trim().Length == 0);
            if (ImGui.Button(selected.Count > 0 ? TF($"Create with {selected.Count} selected") : T("Create")))
            {
                Add(_newName.Trim(), selected);
                _newName = "";
            }
            ImGui.EndDisabled();

            if (ImGui.BeginTable("##layers", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn(T("Layer"), ImGuiTableColumnFlags.WidthStretch, 1.4f);
                ImGui.TableSetupColumn(T("Objects"), ImGuiTableColumnFlags.WidthStretch, 0.5f);
                ImGui.TableSetupColumn(T("Hidden"), ImGuiTableColumnFlags.WidthStretch, 0.5f);
                ImGui.TableSetupColumn(T("Frozen"), ImGuiTableColumnFlags.WidthStretch, 0.5f);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch, 2.2f);
                ImGui.TableHeadersRow();
                for (var i = 0; i < _layers.Count; i++)
                {
                    var layer = _layers[i];
                    ImGui.PushID(i);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text(layer.Name);
                    ImGui.TableNextColumn();
                    ImGui.Text(layer.References.Count.ToString());
                    ImGui.TableNextColumn();
                    var hidden = layer.Hidden;
                    if (ImGui.Checkbox("##hidden", ref hidden)) Set(layer, hidden: hidden);
                    ImGui.TableNextColumn();
                    var frozen = layer.Frozen;
                    if (ImGui.Checkbox("##frozen", ref frozen)) Set(layer, frozen: frozen);
                    ImGui.TableNextColumn();
                    ImGui.BeginDisabled(selected.Count == 0);
                    if (ImGui.SmallButton(T("+ selected"))) { layer.References = layer.References.Union(selected.Select(k => k.ToString())).ToList(); Save(); }
                    ImGui.SameLine();
                    if (ImGui.SmallButton(T("- selected"))) { layer.References = layer.References.Except(selected.Select(k => k.ToString())).ToList(); Save(); }
                    ImGui.EndDisabled();
                    ImGui.SameLine();
                    if (ImGui.SmallButton(T("select"))) window.SelectReferences(app, layer.References.Select(t => FormKey.TryFactory(t, out var k) ? k : FormKey.Null).Where(k => !k.IsNull));
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Select its references that are loaded"));
                    ImGui.SameLine();
                    if (ImGui.SmallButton("x")) { _layers.RemoveAt(i); Save(); ImGui.PopID(); break; }
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
            if (_layers.Count == 0) ImGui.TextColored(Theme.TextDim, T("No layers yet: select objects in the Render Window, name a layer and create it."));
        }
        ImGui.End();
    }
}
