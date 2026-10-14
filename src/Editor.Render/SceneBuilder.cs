using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Assets;
using StbEditor.Data;
using StbEditor.Data.Catalog;
using StbEditor.Nif;

namespace StbEditor.Render;

/// <summary>
/// Turns cells of the load order into a <see cref="Scene"/>. A cell's references are spread over
/// every plugin that touches the cell; each plugin's copy of the cell group is read and the
/// references merged by FormKey, the later plugin winning, deleted ones dropped.
/// </summary>
public sealed class SceneBuilder(Workspace workspace, RecordCatalog catalog)
{
    private readonly RecordLookup _lookup = new(workspace, catalog);

    /// <summary>Edits of the session: new cells and worldspaces are built from their drafts.</summary>
    public Core.EditDocument? Document { get; init; }
    private ActorAppearance? _appearance;

    /// <summary>NPC looks, cached per base form for the life of the builder.</summary>
    public ActorAppearance Appearance => _appearance ??= new ActorAppearance(_lookup);

    /// <summary>Worldspaces without water keep their default water far below the land; not drawn.</summary>
    private const float NoWaterBelow = -50000f;

    private const uint DeletedFlag = 0x20;
    private const uint PersistentFlag = 0x400;
    private const uint InitiallyDisabledFlag = 0x800;

    private readonly Dictionary<FormKey, (IMajorRecordGetter? Record, string Signature)> _bases = new();

    public Scene BuildInterior(FormKey cellKey)
    {
        var cell = Document?.Draft(cellKey) is Core.CellDraft draft ? draft.Cell : Resolve<ICellGetter>(cellKey);
        var scene = new Scene
        {
            Title = cell?.EditorID ?? workspace.FormIdText(cellKey),
            IsInterior = true,
            Lighting = InteriorLighting(HeaderOf(cellKey) ?? cell),
        };

        var interiorVersions = CellVersions(cellKey);
        foreach (var placed in MergeReferences(interiorVersions, _ => true))
            AddPlaced(scene, placed.Record, cellKey, placed.Persistent);
        AddNavmeshes(scene, interiorVersions, cellKey, null);

        if (cell is not null && cell.Flags.HasFlag(Cell.Flag.HasWater) && cell.WaterHeight is { } water && water > -1e6f && water < 1e6f)
        {
            var (min, max) = Extent(scene);
            scene.Water.Add(new WaterPlane(new Vector2(min.X, min.Y), new Vector2(max.X, max.Y), water));
        }

        FocusOnContent(scene);
        return scene;
    }

    public Scene BuildExterior(FormKey worldKey, int centerX, int centerY, int radius)
    {
        var world = Document?.Draft(worldKey) is Core.WorldDraft worldDraft ? worldDraft.World : Resolve<IWorldspaceGetter>(worldKey);
        var scene = new Scene
        {
            Title = $"{world?.EditorID ?? worldKey.ToString()} [{centerX}, {centerY}]",
            IsInterior = false,
            Worldspace = worldKey,
            CenterCell = (centerX, centerY),
            Lighting = SceneLighting.DefaultExterior,
            Grasses = GrassOf,
            GrassSpacing = GrassSetting("iMinGrassSize", 20),
            GrassDistance = GrassSetting("fGrassStartFadeDistance", 7000) + GrassSetting("fGrassFadeRange", 1000),
        };

        var cells = catalog.BySignature.GetValueOrDefault("CELL") ?? [];
        var grid = cells
            .Where(c => c.Winner.Cell is { IsInterior: false, IsPersistentCell: false } p && p.Worldspace == worldKey
                && Math.Abs(p.GridX - centerX) <= radius && Math.Abs(p.GridY - centerY) <= radius)
            .ToList();

        var defaultWater = world?.LandDefaults?.DefaultWaterHeight;
        var hasWorldWater = world?.Water.IsNull == false || defaultWater is not null;

        foreach (var cellRecord in grid)
        {
            var placement = cellRecord.Winner.Cell!;
            var versions = CellVersions(cellRecord.FormKey);
            var winner = versions.Count > 0 ? versions[^1] : null;

            foreach (var placed in MergeReferences(versions, _ => true))
                AddPlaced(scene, placed.Record, cellRecord.FormKey, placed.Persistent);
            AddNavmeshes(scene, versions, cellRecord.FormKey, worldKey);

            var landDraft = Document?.LandDrafts(worldKey).FirstOrDefault(l => l.GridX == placement.GridX && l.GridY == placement.GridY);
            if (landDraft is not null)
                scene.Terrain.Add(TerrainFrom(landDraft.Data, placement.GridX, placement.GridY, landDraft.Key, landDraft.Cell));
            else if (LandOf(versions) is { } land && BuildTerrain(land, placement.GridX, placement.GridY, cellRecord.FormKey) is { } terrain)
                scene.Terrain.Add(terrain);

            // Water: the cell's own height, else the worldspace default.
            if (winner is not null && (winner.Flags.HasFlag(Cell.Flag.HasWater) || hasWorldWater))
            {
                var height = winner.WaterHeight ?? defaultWater;
                if (height is { } h && h > NoWaterBelow && h < 1e6f && !winner.Flags.HasFlag(Cell.Flag.IsInteriorCell))
                {
                    var origin = new Vector2(placement.GridX * TerrainCell.CellSize, placement.GridY * TerrainCell.CellSize);
                    scene.Water.Add(new WaterPlane(origin, origin + new Vector2(TerrainCell.CellSize), h));
                }
            }
        }

        // Cells created in this session: no references from plugins, maybe new land.
        foreach (var draft in Document?.CellDrafts(worldKey) ?? [])
        {
            if (draft.Cell.Grid is not { } g || Math.Abs(g.Point.X - centerX) > radius || Math.Abs(g.Point.Y - centerY) > radius) continue;
            var newLand = Document?.LandDrafts(worldKey).FirstOrDefault(l => l.GridX == g.Point.X && l.GridY == g.Point.Y);
            if (newLand is not null)
                scene.Terrain.Add(TerrainFrom(newLand.Data, g.Point.X, g.Point.Y, newLand.Key, newLand.Cell));
            else if (draft.Cell.Landscape is { } draftLand && BuildTerrain(draftLand, g.Point.X, g.Point.Y, draft.Key) is { } draftTerrain)
                scene.Terrain.Add(draftTerrain);
            if (defaultWater is { } dw && hasWorldWater && dw > NoWaterBelow && dw < 1e6f)
            {
                var origin = new Vector2(g.Point.X * TerrainCell.CellSize, g.Point.Y * TerrainCell.CellSize);
                scene.Water.Add(new WaterPlane(origin, origin + new Vector2(TerrainCell.CellSize), draft.Cell.WaterHeight ?? dw));
            }
        }

        // Persistent references of a worldspace sit in its persistent cell, not in the grid cell
        // they stand in; take those that fall inside the loaded area.
        var minX = (centerX - radius) * TerrainCell.CellSize;
        var minY = (centerY - radius) * TerrainCell.CellSize;
        var maxX = (centerX + radius + 1) * TerrainCell.CellSize;
        var maxY = (centerY + radius + 1) * TerrainCell.CellSize;

        var persistentVersions = _lookup.PersistentCellVersions(worldKey);

        foreach (var placed in MergeReferences(persistentVersions, p =>
                 p.Placement is { } pl && pl.Position.X >= minX && pl.Position.X < maxX && pl.Position.Y >= minY && pl.Position.Y < maxY))
        {
            var cellKey = persistentVersions.Count > 0 ? persistentVersions[^1].FormKey : worldKey;
            AddPlaced(scene, placed.Record, cellKey, persistent: true);
        }

        var center = new Vector3((centerX + 0.5f) * TerrainCell.CellSize, (centerY + 0.5f) * TerrainCell.CellSize, 0);
        var terrainHere = scene.Terrain.FirstOrDefault(t => t.X == centerX && t.Y == centerY);
        center.Z = terrainHere?.HeightAt(16, 16) ?? 0;
        scene.Focus = center;
        scene.FocusRadius = 2500f;
        return scene;
    }

    /// <summary>
    /// A scene object for a reference that exists only in the editor so far (placed from the
    /// Object Window, duplicated). Same rules as loaded references: model, light, actor marker.
    /// </summary>
    public SceneObject CreatePlaced(FormKey reference, FormKey baseKey, Vector3 position, Vector3 rotation, float scale, FormKey cell, bool persistent,
        IMajorRecordGetter? referenceRecord = null, string? sourcePlugin = null)
    {
        var refRecord = referenceRecord;
        var (record, signature) = Base(baseKey);
        var isActor = signature is "NPC_" or "LVLN";
        var look = isActor ? Appearance.Resolve(baseKey) : null;
        var modelScale = look?.Scale ?? 1f;
        var transform = ReferenceTransform(position, isActor ? new Vector3(0, 0, rotation.Z) : rotation, scale * modelScale);

        SceneLight? light = null;
        if (record is ILightGetter lightBase)
        {
            var c = lightBase.Color;
            light = new SceneLight { Color = new Vector3(c.R / 255f, c.G / 255f, c.B / 255f), Radius = Math.Max(lightBase.Radius, 1f), Fade = lightBase.FadeValue };
        }

        var model = isActor ? look?.CompositePath : ModelOf(record);
        var obj = new SceneObject
        {
            Reference = reference,
            Base = baseKey,
            BaseSignature = signature,
            BaseEditorId = record?.EditorID,
            Kind = isActor ? SceneObjectKind.Actor : model is null ? SceneObjectKind.Marker : SceneObjectKind.Mesh,
            ModelPath = model,
            Cell = cell,
            Position = position,
            Rotation = rotation,
            Scale = scale,
            ModelScale = modelScale,
            Transform = transform,
            World = transform.ToMatrix(),
            Light = light,
            IsPersistent = persistent,
            Record = refRecord,
            SourcePlugin = sourcePlugin,
        };
        obj.UpdateBounds();
        return obj;
    }

    /// <summary>Persistent cell of a worldspace (where persistent exterior references live).</summary>
    public FormKey? PersistentCellOf(FormKey world) =>
        Document?.Draft(world) is Core.WorldDraft draft ? draft.PersistentCell : _lookup.PersistentCellVersions(world).LastOrDefault()?.FormKey;

    /// <summary>The exterior CELL at a grid position, if any plugin defines one.</summary>
    public FormKey? CellAt(FormKey world, int x, int y) =>
        (catalog.BySignature.GetValueOrDefault("CELL") ?? [])
            .FirstOrDefault(c => c.Winner.Cell is { IsInterior: false, IsPersistentCell: false } p && p.Worldspace == world && p.GridX == x && p.GridY == y)
            ?.FormKey
        ?? Document?.CellDrafts(world).FirstOrDefault(d => d.Cell.Grid is { } g && g.Point.X == x && g.Point.Y == y)?.Key;

    // ---------------------------------------------------------------- references

    private readonly record struct Placed(IPlacedGetter Record, bool Persistent);

    /// <summary>Every plugin's version of a CELL record with its children, lowest priority first.</summary>
    private List<ICellGetter> CellVersions(FormKey cellKey) => _lookup.CellVersions(cellKey);

    private static List<Placed> MergeReferences(List<ICellGetter> versions, Func<IPlacedObjectGetter, bool> filter)
    {
        var merged = new Dictionary<FormKey, Placed>();

        foreach (var cell in versions)
        {
            foreach (var (list, persistent) in new[] { (cell.Persistent, true), (cell.Temporary, false) })
            {
                foreach (var placed in list)
                {
                    if (placed is IPlacedObjectGetter obj && !filter(obj)) continue;
                    if (((uint)placed.MajorRecordFlagsRaw & DeletedFlag) != 0)
                    {
                        merged.Remove(placed.FormKey);
                        continue;
                    }
                    merged[placed.FormKey] = new Placed(placed, persistent);
                }
            }
        }

        return [.. merged.Values];
    }

    private static ILandscapeGetter? LandOf(List<ICellGetter> versions)
    {
        for (var i = versions.Count - 1; i >= 0; i--)
            if (versions[i].Landscape is { } land) return land;
        return null;
    }

    private void AddPlaced(Scene scene, IPlacedGetter placed, FormKey cell, bool persistent)
    {
        try
        {
            switch (placed)
            {
                case IPlacedObjectGetter obj when obj.Placement is { } placement:
                    AddObject(scene, obj, obj.Base.FormKey, placement, obj.Scale ?? 1f, cell, persistent);
                    break;
                case IPlacedNpcGetter npc when npc.Placement is { } placement:
                    AddActor(scene, npc, npc.Base.FormKey, placement, npc.Scale ?? 1f, cell, persistent);
                    break;
                case IAPlacedTrapGetter trap when trap.Placement is { } placement:
                    AddObject(scene, trap, TrapBase(trap), placement, trap.Scale ?? 1f, cell, persistent);
                    break;
            }
        }
        catch (Exception ex)
        {
            scene.Warnings.Add($"{workspace.FormIdText(placed.FormKey)}: {ex.Message}");
        }
    }

    private void AddObject(Scene scene, IPlacedGetter placed, FormKey baseKey, IPlacementGetter placement, float scale, FormKey cell, bool persistent)
    {
        var (record, signature) = Base(baseKey);
        var position = new Vector3(placement.Position.X, placement.Position.Y, placement.Position.Z);
        var rotation = new Vector3(placement.Rotation.X, placement.Rotation.Y, placement.Rotation.Z);
        var transform = ReferenceTransform(position, rotation, scale);
        var flags = (uint)placed.MajorRecordFlagsRaw;

        SceneLight? light = null;
        if (record is ILightGetter lightBase)
        {
            var radius = placed is IPlacedObjectGetter { Radius: { } r } ? r : lightBase.Radius;
            var fade = lightBase.FadeValue + (placed is IPlacedObjectGetter { LightData: { } ld } ? ld.FadeOffset : 0);
            var c = lightBase.Color;
            light = new SceneLight
            {
                Color = new Vector3(c.R / 255f, c.G / 255f, c.B / 255f),
                Radius = Math.Max(radius, 1f),
                Fade = fade,
            };
        }

        var made = Make(placed.FormKey, baseKey, signature, record?.EditorID, ModelOf(record), cell,
            position, rotation, scale, transform, light, flags, persistent, -1, placed);
        scene.Objects.Add(made);
    }

    private void AddActor(Scene scene, IPlacedNpcGetter npc, FormKey baseKey, IPlacementGetter placement, float scale, FormKey cell, bool persistent)
    {
        var (record, signature) = Base(baseKey);
        var position = new Vector3(placement.Position.X, placement.Position.Y, placement.Position.Z);
        var rotation = new Vector3(0, 0, placement.Rotation.Z);
        var look = Appearance.Resolve(baseKey);
        var modelScale = look?.Scale ?? 1f;
        var transform = ReferenceTransform(position, rotation, scale * modelScale);
        var flags = (uint)npc.MajorRecordFlagsRaw;

        var obj = new SceneObject
        {
            Reference = npc.FormKey,
            Record = npc,
            Base = baseKey,
            BaseSignature = signature,
            BaseEditorId = record?.EditorID,
            Kind = SceneObjectKind.Actor,
            ModelPath = look?.CompositePath,
            Cell = cell,
            Position = position,
            Rotation = rotation,
            Scale = scale,
            ModelScale = modelScale,
            Transform = transform,
            World = transform.ToMatrix(),
            InitiallyDisabled = (flags & InitiallyDisabledFlag) != 0,
            IsPersistent = persistent || (flags & PersistentFlag) != 0,
        };
        obj.UpdateBounds();
        scene.Objects.Add(obj);
    }

    /// <summary>Placed arrows/beams/hazards/… each name their base field differently (Projectile, Hazard).</summary>
    private static FormKey TrapBase(IAPlacedTrapGetter trap)
    {
        foreach (var name in new[] { "Projectile", "Hazard" })
        {
            if (trap.GetType().GetProperty(name)?.GetValue(trap) is IFormLinkGetter link) return link.FormKey;
        }
        return FormKey.Null;
    }

    private static SceneObject Make(FormKey reference, FormKey baseKey, string signature, string? editorId, string? model, FormKey cell,
        Vector3 position, Vector3 rotation, float scale, NifTransform transform, SceneLight? light, uint flags, bool persistent, int part,
        IPlacedGetter? placed = null)
    {
        var obj = new SceneObject
        {
            Reference = reference,
            Record = placed,
            SourcePlugin = placed?.FormKey.ModKey.FileName.String,
            Base = baseKey,
            BaseSignature = signature,
            BaseEditorId = editorId,
            Kind = model is null ? SceneObjectKind.Marker : SceneObjectKind.Mesh,
            ModelPath = model,
            Cell = cell,
            Position = position,
            Rotation = rotation,
            Scale = scale,
            Transform = transform,
            World = transform.ToMatrix(),
            Light = light,
            InitiallyDisabled = (flags & InitiallyDisabledFlag) != 0,
            IsPersistent = persistent || (flags & PersistentFlag) != 0,
            CollectionPart = part,
        };
        if (placed is IPlacedObjectGetter { Primitive: { } primitive } && primitive.Type != PlacedPrimitive.TypeEnum.None)
        {
            var c = primitive.Color;
            obj.Primitive = (new Vector3(primitive.Bounds.X, primitive.Bounds.Y, primitive.Bounds.Z), (int)primitive.Type,
                0xFF000000u | ((uint)c.B << 16) | ((uint)c.G << 8) | c.R);
        }
        obj.UpdateBounds();
        return obj;
    }

    /// <summary>
    /// Reference placement → transform, with the engine's own Euler convention
    /// (NiMatrix3::SetEulerAnglesXYZ): positive Z turns clockwise seen from above, like a heading.
    /// </summary>
    public static NifTransform ReferenceTransform(Vector3 position, Vector3 rotation, float scale)
    {
        float sx = MathF.Sin(rotation.X), cx = MathF.Cos(rotation.X);
        float sy = MathF.Sin(rotation.Y), cy = MathF.Cos(rotation.Y);
        float sz = MathF.Sin(rotation.Z), cz = MathF.Cos(rotation.Z);

        // Rows as the engine stores them; NiMatrix3 multiplies column vectors (v' = R·v), so with
        // Z = 90° the forward axis (0,1,0) turns to (1,0,0): east, clockwise from north.
        var applied = new Matrix3(
            new Vector3(cy * cz, cy * sz, -sy),
            new Vector3(sx * sy * cz - cx * sz, sx * sy * sz + cx * cz, sx * cy),
            new Vector3(cx * sy * cz + sx * sz, cx * sy * sz - sx * cz, cx * cy));

        return new NifTransform(applied, position, scale);
    }

    private (IMajorRecordGetter? Record, string Signature) Base(FormKey key)
    {
        // Edited or new base records of the session win (not cached: they can change).
        if (Document?.Draft(key) is Core.OverrideDraft { Place: Core.RecordPlace.TopLevel } draft)
            return (draft.Record, RecordCatalog.SignatureOf(draft.Record));

        if (_bases.TryGetValue(key, out var cached)) return cached;

        // Unresolvable base: the reference still shows as a marker.
        var record = _lookup.Resolve(key);

        var signature = record is null ? catalog.Get(key)?.Signature ?? "????" : RecordCatalog.SignatureOf(record);
        _bases[key] = (record, signature);
        return (record, signature);
    }

    /// <summary>Model of a placeable base record, as an asset path under meshes\.</summary>
    public static string? ModelOf(IMajorRecordGetter? record)
    {
        string? file = record switch
        {
            IArmorGetter armor => armor.WorldModel?.Male?.Model?.File.GivenPath ?? armor.WorldModel?.Female?.Model?.File.GivenPath,
            IModeledGetter modeled => modeled.Model?.File.GivenPath,
            _ => null,
        };

        return string.IsNullOrWhiteSpace(file) ? null : AssetFileSystem.MeshPath(file);
    }

    private T? Resolve<T>(FormKey key) where T : class, IMajorRecordGetter => _lookup.Resolve<T>(key);

    // ---------------------------------------------------------------- lighting

    /// <summary>An edited cell header (lighting, water…), if there is one.</summary>
    private ICellGetter? HeaderOf(FormKey cellKey) =>
        Document?.Draft(cellKey) is Core.OverrideDraft { Place: Core.RecordPlace.Header, Record: ICellGetter header } ? header : null;

    /// <summary>The interior's lighting as it is now (its edited header, else the plugins' cell).</summary>
    public SceneLighting InteriorLightingOf(FormKey cellKey) =>
        InteriorLighting(HeaderOf(cellKey) ?? (Document?.Draft(cellKey) is Core.CellDraft draft ? draft.Cell : Resolve<ICellGetter>(cellKey)));

    private SceneLighting InteriorLighting(ICellGetter? cell)
    {
        if (cell is null) return SceneLighting.DefaultInterior;

        var lighting = cell.Lighting;
        if (lighting is not null)
        {
            return new SceneLighting(
                Color(lighting.AmbientColor),
                Color(lighting.DirectionalColor),
                Direction(lighting.DirectionalRotationXY, lighting.DirectionalRotationZ),
                Color(lighting.FogNearColor),
                lighting.FogNear,
                lighting.FogFar);
        }

        var template = cell.LightingTemplate.IsNull ? null : Resolve<ILightingTemplateGetter>(cell.LightingTemplate.FormKey);
        if (template is not null)
        {
            return new SceneLighting(
                Color(template.AmbientColor),
                Color(template.DirectionalColor),
                Direction(template.DirectionalRotationXY, template.DirectionalRotationZ),
                Color(template.FogNearColor),
                template.FogNear,
                template.FogFar);
        }

        return SceneLighting.DefaultInterior;
    }

    private static Vector3 Color(System.Drawing.Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    private static Vector3 Direction(int rotationXY, int rotationZ)
    {
        var xy = rotationXY * MathF.PI / 180f;
        var z = rotationZ * MathF.PI / 180f;
        return Vector3.Normalize(new Vector3(MathF.Sin(z) * MathF.Cos(xy), MathF.Cos(z) * MathF.Cos(xy), MathF.Abs(MathF.Sin(xy)) + 0.3f));
    }

    // ---------------------------------------------------------------- terrain

    private TerrainCell? BuildTerrain(ILandscapeGetter land, int gridX, int gridY, FormKey cell)
    {
        if (land.VertexHeightMap is null) return null;
        return TerrainFrom(Core.LandscapeData.From(land), gridX, gridY, land.FormKey, cell);
    }

    /// <summary>Render form of land data (loaded, or being edited).</summary>
    public TerrainCell TerrainFrom(Core.LandscapeData data, int gridX, int gridY, FormKey land, FormKey cell)
    {
        const int n = TerrainCell.Size;
        var colors = new uint[n * n];
        for (var i = 0; i < colors.Length; i++)
            colors[i] = data.Colors is { } c ? c[i] | 0xFF000000u : 0xFFFFFFFFu;

        var quadrants = new List<TerrainLayer>[4];
        for (var q = 0; q < 4; q++)
        {
            var quadrant = data.Quadrants[q];
            quadrants[q] = [new TerrainLayer(LandTexture(quadrant.BaseTexture), null)];
            foreach (var layer in quadrant.Layers)
                quadrants[q].Add(new TerrainLayer(LandTexture(layer.Texture), layer.Opacity));
        }

        return new TerrainCell
        {
            Land = land,
            Cell = cell,
            X = gridX,
            Y = gridY,
            Heights = data.Heights,
            Normals = data.Normals,
            Colors = colors,
            Quadrants = quadrants,
            Data = data,
        };
    }

    /// <summary>Every navmesh of a cell (last plugin wins per NAVM), with the session's edited/new ones.</summary>
    private void AddNavmeshes(Scene scene, List<ICellGetter> versions, FormKey cell, FormKey? world)
    {
        var meshes = new Dictionary<FormKey, INavigationMeshGetter>();
        foreach (var version in versions)
            foreach (var nav in version.NavigationMeshes)
                meshes[nav.FormKey] = nav;

        foreach (var draft in Document?.Drafts.Values.OfType<Core.OverrideDraft>().Where(d => d.Place == Core.RecordPlace.Navmesh && d.Cell == cell) ?? [])
            meshes[draft.Key] = (INavigationMeshGetter)draft.Record;

        foreach (var (key, nav) in meshes)
        {
            if (nav.IsDeleted || nav.Data is null) continue;
            scene.Navmeshes.Add(new NavmeshView { Key = key, Cell = cell, World = world, Record = nav });
        }
    }

    /// <summary>Navmeshes of a cell as they are now (after an edit or undo).</summary>
    public List<NavmeshView> NavmeshesOf(FormKey cell, FormKey? world)
    {
        var scene = new Scene { Title = "", IsInterior = world is null, Lighting = SceneLighting.DefaultInterior };
        AddNavmeshes(scene, Document?.Draft(cell) is Core.CellDraft ? [] : CellVersions(cell), cell, world);
        return scene.Navmeshes;
    }

    /// <summary>Winning LAND of an exterior cell (for editing it).</summary>
    public ILandscapeGetter? LandRecord(FormKey cell) => LandOf(CellVersions(cell));

    /// <summary>Terrain of one grid cell as it is now: an edited/new land draft, else the plugins' land.</summary>
    public TerrainCell? TerrainAt(FormKey world, int x, int y)
    {
        if (Document?.LandDrafts(world).FirstOrDefault(l => l.GridX == x && l.GridY == y) is { } draft)
            return TerrainFrom(draft.Data, x, y, draft.Key, draft.Cell);

        if (CellAt(world, x, y) is not { } cell) return null;
        if (Document?.Draft(cell) is Core.CellDraft cellDraft)
            return cellDraft.Cell.Landscape is { } own ? BuildTerrain(own, x, y, cell) : null;
        return LandOf(CellVersions(cell)) is { } land ? BuildTerrain(land, x, y, cell) : null;
    }

    private readonly Dictionary<FormKey, IReadOnlyList<GrassKind>> _grasses = new();

    /// <summary>The grass types of a landscape texture (LTEX GNAM → GRAS), cached.</summary>
    public IReadOnlyList<GrassKind> GrassOf(FormKey texture)
    {
        lock (_grasses)
        {
            if (_grasses.TryGetValue(texture, out var cached)) return cached;
            var kinds = new List<GrassKind>();
            foreach (var link in Resolve<ILandscapeTextureGetter>(texture)?.Grasses ?? [])
            {
                if (link.IsNull || Resolve<IGrassGetter>(link.FormKey) is not { } grass || grass.Model?.File.GivenPath is not { Length: > 0 } file) continue;
                kinds.Add(new GrassKind(AssetFileSystem.MeshPath(file), grass.Density, grass.MinSlope, grass.MaxSlope, grass.UnitsFromWater,
                    grass.UnitsFromWaterType.ToString(), grass.PositionRange, grass.HeightRange,
                    grass.Flags.HasFlag(Grass.Flag.UniformScaling), grass.Flags.HasFlag(Grass.Flag.FitToSlope)));
            }
            return _grasses[texture] = kinds;
        }
    }

    /// <summary>A [Grass] INI setting: SkyrimPrefs.ini over Skyrim.ini, as the engine reads them.</summary>
    private float GrassSetting(string key, float fallback)
    {
        foreach (var file in new[] { Path.Combine(workspace.Game.IniFolder, "SkyrimPrefs.ini"), workspace.Game.SkyrimIni })
            if (File.Exists(file) && TextFiles.ReadIniValue(file, key) is { } text
                && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
                return value;
        return fallback;
    }

    private const string DefaultLandTexture = @"textures\landscape\dirt02.dds";
    private readonly Dictionary<FormKey, string> _landTextures = new();

    /// <summary>Diffuse texture of a landscape texture (LTEX → TXST), for painting UIs too.</summary>
    public string LandTexture(FormKey? key)
    {
        if (key is not { IsNull: false } k) return DefaultLandTexture;
        if (_landTextures.TryGetValue(k, out var cached)) return cached;

        var path = DefaultLandTexture;
        if (Resolve<ILandscapeTextureGetter>(k) is { } ltex && !ltex.TextureSet.IsNull
            && Resolve<ITextureSetGetter>(ltex.TextureSet.FormKey) is { Diffuse: { IsNull: false } diffuse })
        {
            path = AssetFileSystem.TexturePath(diffuse.GivenPath);
        }

        _landTextures[k] = path;
        return path;
    }

    // ---------------------------------------------------------------- framing

    private static (Vector3 Min, Vector3 Max) Extent(Scene scene)
    {
        if (scene.Objects.Count == 0) return (new Vector3(-2048), new Vector3(2048));
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var o in scene.Objects)
        {
            min = Vector3.Min(min, o.Position);
            max = Vector3.Max(max, o.Position);
        }
        return (min, max);
    }

    private static void FocusOnContent(Scene scene)
    {
        if (scene.Objects.Count == 0) return;

        // Median position: robust against a stray marker kilometres away.
        var xs = scene.Objects.Select(o => o.Position.X).Order().ToList();
        var ys = scene.Objects.Select(o => o.Position.Y).Order().ToList();
        var zs = scene.Objects.Select(o => o.Position.Z).Order().ToList();
        scene.Focus = new Vector3(xs[xs.Count / 2], ys[ys.Count / 2], zs[zs.Count / 2]);

        var spread = scene.Objects.Select(o => Vector3.Distance(o.Position, scene.Focus)).Order().ToList();
        scene.FocusRadius = Math.Clamp(spread[(int)(spread.Count * 0.7)], 300f, 6000f);
    }
}
