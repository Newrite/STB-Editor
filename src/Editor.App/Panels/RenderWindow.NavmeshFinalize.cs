using System.Numerics;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using StbEditor.App.Services;
using StbEditor.Core;
using StbEditor.Data;
using StbEditor.Render;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// Navmesh Finalize (CK: Finalize Cell NavMeshes): door triangles and the doors' navmesh links, edge
/// links across cell borders and between navmeshes, and the navmeshes' entries in the plugin's navmesh
/// info map (NAVI) — one undo step. Checked against the CK's own data by <c>stbedit-cli finalizetest</c>.
/// </summary>
internal sealed partial class RenderWindow
{
    /// <summary>Finalizes the edited navmeshes (or every navmesh of the open cell); returns a summary for the panel.</summary>
    public string? FinalizeNavmeshes(EditorApp app, bool editedOnly)
    {
        if (_scene is null || app.Session is not { } session || _document is null) return null;
        if (!session.Doors.IsCompletedSuccessfully)
        {
            Log.Warn(T("The door index is still being built: try again in a moment"));
            return null;
        }
        var doorIndex = session.Doors.Result;
        var lookup = new RecordLookup(session.Workspace, session.Catalog);
        var copies = _scene.Navmeshes.Where(v => v.Record.Data is not null)
            .ToDictionary(v => v.Key, v => (NavigationMesh)_document.WorkingCopy(v.Key, v.Record));
        var finalize = (editedOnly
                ? copies.Keys.Where(k => _document.Override(k) is not null)
                : _scene.Navmeshes.Where(v => _scene.IsInterior || v.Cell == _cell).Select(v => v.Key))
            .Where(copies.ContainsKey).ToHashSet();
        if (finalize.Count == 0)
        {
            Log.Info(editedOnly ? T("Finalize: no edited navmesh") : T("Finalize: the cell has no navmesh"));
            return null;
        }

        var name = TF($"Finalize {finalize.Count} navmesh(es)");
        var steps = new List<IEditCommand>();
        var doorsByMesh = new Dictionary<FormKey, List<FormKey>>();
        int doorTriangles = 0, doorLinks = 0;
        foreach (var key in finalize)
        {
            var view = View(key)!;
            var cellDoors = _scene.Objects.Where(o => o.Cell == view.Cell && !o.IsDeleted && TeleportOf(app, o) is not null).ToList();
            var doors = new List<NavmeshFinalize.Door>();
            foreach (var door in cellDoors)
                if (ArrivalOf(app, lookup, doorIndex, door) is { } at && NavmeshFinalize.TriangleAt(copies[key].Data!, at, 128) >= 0)
                    doors.Add(new NavmeshFinalize.Door(door.Reference, at));
            var linked = NavmeshFinalize.LinkDoors(copies[key], doors);
            doorTriangles += linked.Count;
            doorsByMesh[key] = [.. linked.Keys];

            // Each door's XNDP: the navmesh and triangle under its arrival point.
            foreach (var (doorKey, triangle) in linked)
            {
                var door = cellDoors.First(o => o.Reference == doorKey);
                var record = _document.Override(doorKey)?.Record as IPlacedObjectGetter ?? door.Record as IPlacedObjectGetter;
                if (record is null) continue;
                if (record.NavigationDoorLink is { } current && current.NavMesh.FormKey == key && current.TeleportMarkerTriangle == triangle) continue;
                var copy = (PlacedObject)_document.WorkingCopy(doorKey, record);
                copy.NavigationDoorLink = new NavigationDoorLink { TeleportMarkerTriangle = (short)triangle };
                copy.NavigationDoorLink.NavMesh.SetTo(key);
                var context = RefContextOf(door);
                _document.RegisterContext(doorKey, context);
                steps.Add(Step(name, doorKey, copy, () => new OverrideDraft(copy, RecordPlace.Reference, context)));
                doorLinks++;
            }
        }

        var touched = NavmeshFinalize.LinkEdges(copies, finalize);
        foreach (var key in finalize.Union(touched))
        {
            var view = View(key);
            var mesh = copies[key];
            steps.Add(Step(name, key, mesh, () => new OverrideDraft(mesh, RecordPlace.Navmesh, null, view?.Cell, view?.World)));
        }

        var infos = 0;
        if (NaviStep(app, session, name, finalize, copies, doorsByMesh) is { } navi)
        {
            steps.Add(navi.Step);
            infos = navi.Count;
        }
        _document.Record(new CompositeCommand(name, steps));
        var summary = TF($"Finalized {finalize.Count} navmesh(es): {doorTriangles} door triangle(s), {doorLinks} door link(s) set, {touched.Count} navmesh(es) with new edge links, {infos} NAVI entr(ies)");
        Log.Info(summary);
        return summary;
    }

    private IEditCommand Step(string name, FormKey key, IMajorRecord edited, Func<OverrideDraft> fresh)
    {
        var before = _document!.Override(key);
        var after = before?.With(edited) ?? fresh();
        return new RecordEditCommand(name, before?.With((IMajorRecord)before.Record.DeepCopy()), after);
    }

    /// <summary>Where one arrives through a door: the other door's teleport marker (in this door's cell).</summary>
    private Vector3? ArrivalOf(EditorApp app, RecordLookup lookup, DoorIndex doorIndex, SceneObject door)
    {
        var other = TeleportOf(app, door)!.Door.FormKey;
        if (_scene!.Objects.FirstOrDefault(o => o.Reference == other) is { } loaded && TeleportOf(app, loaded) is { } teleport)
            return new Vector3(teleport.Position.X, teleport.Position.Y, teleport.Position.Z);
        if (_document!.Override(other)?.Record is IPlacedObjectGetter { TeleportDestination: { } edited })
            return new Vector3(edited.Position.X, edited.Position.Y, edited.Position.Z);
        if (doorIndex.CellOf(other) is not { } cell) return null;
        var record = lookup.CellVersions(cell).SelectMany(v => v.Persistent.Concat(v.Temporary)).OfType<IPlacedObjectGetter>().LastOrDefault(p => p.FormKey == other);
        return record?.TeleportDestination?.Position is { } p ? new Vector3(p.X, p.Y, p.Z) : null;
    }

    /// <summary>
    /// The plugin's navmesh info map with an entry for each finalized navmesh: the plugin's own NAVI (or a new
    /// one with only these entries — the game merges every plugin's NAVI), entries taken over from the version
    /// that has them (merges, island) and updated.
    /// </summary>
    private (IEditCommand Step, int Count)? NaviStep(EditorApp app, EditorSession session, string name, HashSet<FormKey> finalize,
        Dictionary<FormKey, NavigationMesh> copies, Dictionary<FormKey, List<FormKey>> doorsByMesh)
    {
        var naviRecord = (session.Catalog.BySignature.GetValueOrDefault("NAVI") ?? []).OrderBy(r => r.FormKey.ModKey.FileName.String == "Skyrim.esm" ? 0 : 1).FirstOrDefault();
        if (naviRecord is null)
        {
            Log.Warn(T("Finalize: the load order has no navmesh info map (NAVI); its entries were not written"));
            return null;
        }
        var key = naviRecord.FormKey;
        NavigationMeshInfoMap navi;
        if (_document!.Override(key)?.Record is INavigationMeshInfoMapGetter edited) navi = edited.DeepCopy();
        else if (app.ActivePlugin is { } active && naviRecord.Plugins.FirstOrDefault(p => p.Name == active.Name)?.Mod?.NavigationMeshInfoMaps.FirstOrDefault(n => n.FormKey == key) is { } own)
            navi = own.DeepCopy();
        else navi = new NavigationMeshInfoMap(key, session.Game.Release) { NavMeshVersion = 12 };

        List<INavigationMeshInfoMapGetter> versions;
        try { versions = session.Workspace.LinkCache.ResolveAllSimpleContexts<INavigationMeshInfoMapGetter>(key).Select(c => c.Record).ToList(); }
        catch { versions = []; }
        foreach (var mesh in finalize)
        {
            var view = View(mesh)!;
            var existing = (INavigationMapInfoGetter?)navi.MapInfos.FirstOrDefault(i => i.NavigationMesh.FormKey == mesh)
                ?? versions.SelectMany(v => v.MapInfos).FirstOrDefault(i => i.NavigationMesh.FormKey == mesh);
            var grid = session.Catalog.Get(view.Cell)?.Winner.Cell is { IsInterior: false, IsPersistentCell: false } p ? new P2Int(p.GridX, p.GridY) : (P2Int?)null;
            var info = NavmeshFinalize.Info(copies[mesh], existing, view.Cell, view.World, grid, doorsByMesh.GetValueOrDefault(mesh) ?? []);
            var at = navi.MapInfos.FindIndex(i => i.NavigationMesh.FormKey == mesh);
            if (at >= 0) navi.MapInfos[at] = info;
            else navi.MapInfos.Add(info);
        }
        return (Step(name, key, navi, () => new OverrideDraft(navi, RecordPlace.TopLevel)), finalize.Count);
    }
}
