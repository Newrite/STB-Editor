using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// Properties of an existing cell or worldspace (CK: Cell / World Spaces dialogs): lighting, water,
/// sound, owner, location, regions, flags; for a worldspace its parent, climate, water, map and land
/// defaults. Only the header is edited (references and land stay as they are); the lighting of an
/// open interior follows at once.
/// </summary>
internal sealed class PlacePropertiesWindows
{
    /// <summary>Tab → fields (insertion order is the tab order).</summary>
    private static readonly Dictionary<string, string[]> CellTabs = new()
    {
        ["Basic"] = ["EditorID", "Name", "Flags", "Owner", "FactionRank", "Location", "EncounterZone", "Regions", "LockList", "Grid"],
        ["Lighting"] = ["LightingTemplate", "Lighting", "ImageSpace"],
        ["Water"] = ["Water", "WaterHeight", "WaterNoiseTexture", "WaterEnvironmentMap", "WaterVelocity"],
        ["Audio and sky"] = ["AcousticSpace", "Music", "SkyAndWeatherFromRegion"],
    };

    private static readonly Dictionary<string, string[]> WorldTabs = new()
    {
        ["Basic"] = ["EditorID", "Name", "Flags", "Parent", "Location", "EncounterZone", "Music", "InteriorLighting", "FixedDimensionsCenterCell"],
        ["Climate and water"] = ["Climate", "Water", "LodWater", "LodWaterHeight", "WaterNoiseTexture", "WaterEnvironmentMap", "CanopyShadow"],
        ["Land and map"] = ["LandDefaults", "MapImage", "MapData", "WorldMapOffsetScale", "WorldMapCellOffset", "DistantLodMultiplier", "ObjectBoundsMin",
            "ObjectBoundsMax", "HdLodDiffuseTexture", "HdLodNormalTexture", "CloudModel"],
    };

    private readonly List<(int Id, FormKey Key, bool Focus)> _open = [];
    private int _next;

    public void Open(FormKey key)
    {
        var at = _open.FindIndex(w => w.Key == key);
        if (at >= 0) _open[at] = (_open[at].Id, key, true);
        else _open.Add((_next++, key, true));
    }

    public void CloseAll() => _open.Clear();

    /// <summary>The cell or worldspace as it is now: its header edit, else the winning version.</summary>
    private static IMajorRecordGetter? Current(EditorApp app, FormKey key)
    {
        if (app.Document.Override(key)?.Record is { } edited) return edited;
        var lookup = new RecordLookup(app.Session!.Workspace, app.Session.Catalog);
        return app.Session.Catalog.Get(key)?.Signature switch
        {
            "CELL" => lookup.CellVersions(key).LastOrDefault(),
            "WRLD" => lookup.Resolve<IWorldspaceGetter>(key),
            _ => null,
        };
    }

    /// <summary>Edits of a cell's or worldspace's header (autotests use the same target).</summary>
    public static EditTarget TargetFor(EditorApp app, FormKey key, IMajorRecordGetter current)
    {
        var world = app.Session!.Catalog.Get(key)?.Winner.Cell is { IsInterior: false, Worldspace: { } w } ? w : (FormKey?)null;
        return new EditTarget(() => app.Document.WorkingCopy(key, current),
            (name, edited) => app.Document.CommitEdit(name, edited, RecordPlace.Header, cell: key, world: world));
    }

    public void Draw(EditorApp app)
    {
        if (app.Session is not { } session) return;
        for (var i = 0; i < _open.Count; i++)
        {
            var (id, key, focus) = _open[i];
            var record = session.Catalog.Get(key);
            ImGui.SetNextWindowSize(app.Scaled(640, 560), ImGuiCond.FirstUseEver);
            if (focus) { ImGui.SetNextWindowFocus(); _open[i] = (id, key, false); }
            var open = true;
            var title = record?.Signature == "WRLD" ? TF($"Worldspace: {record.EditorId}") : TF($"Cell: {record?.EditorId ?? session.Workspace.FormIdText(key)}");
            if (ImGui.Begin($"{title}###place{id}", ref open, ImGuiWindowFlags.NoSavedSettings))
                DrawPlace(app, key);
            ImGui.End();
            if (!open) _open.RemoveAt(i--);
        }
    }

    private static void DrawPlace(EditorApp app, FormKey key)
    {
        if (app.Document.Draft(key) is CellDraft or WorldDraft)
        {
            ImGui.TextColored(Theme.TextDim, T("A cell or worldspace made in this session: its properties are set where it was created."));
            return;
        }
        if (Current(app, key) is not { } current)
        {
            ImGui.TextColored(Theme.Warning, T("Not found."));
            return;
        }
        var target = TargetFor(app, key, current);
        if (app.Document.Override(key) is not null) ImGui.TextColored(Theme.Warning, T("changed, not saved (Ctrl+S)"));
        if (app.ActivePlugin is null) ImGui.TextColored(Theme.TextDim, T("Changes are saved into the active file (Ctrl+S asks for one)."));
        if (!ImGui.BeginTabBar("##place")) return;
        foreach (var (tab, members) in current is ICellGetter ? CellTabs : WorldTabs)
        {
            if (!ImGui.BeginTabItem(T(tab) + "###" + tab)) continue;
            if (ImGui.BeginChild("##body", System.Numerics.Vector2.Zero))
                FormView.DrawObject(app, current, members, [], target);
            ImGui.EndChild();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }
}
