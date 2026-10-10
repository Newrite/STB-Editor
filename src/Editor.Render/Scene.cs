using System.Numerics;
using Mutagen.Bethesda.Plugins;
using StbEditor.Nif;

namespace StbEditor.Render;

public enum SceneObjectKind
{
    /// <summary>Has a model.</summary>
    Mesh,

    /// <summary>No model of its own: drawn as an editor marker (sound marker, model-less light…).</summary>
    Marker,

    /// <summary>NPC or leveled NPC placement: its combined body/outfit/head model, or a box when that cannot be resolved.</summary>
    Actor,
}

public sealed class SceneLight
{
    public required Vector3 Color { get; init; }
    public required float Radius { get; init; }
    public required float Fade { get; init; }
}

/// <summary>A placed reference (or one part of a static collection) in the loaded area.</summary>
public sealed class SceneObject
{
    public required FormKey Reference { get; init; }

    /// <summary>The winning REFR/ACHR record (Mutagen getter).</summary>
    public Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter? Record { get; init; }

    /// <summary>Plugin the winning version of the reference comes from.</summary>
    public string? SourcePlugin { get; init; }
    public required FormKey Base { get; init; }
    public required string BaseSignature { get; init; }
    public string? BaseEditorId { get; init; }
    public required SceneObjectKind Kind { get; init; }
    public string? ModelPath { get; init; }

    /// <summary>Owning cell, for the inspector and later for saving.</summary>
    public FormKey Cell { get; init; }

    public required Vector3 Position { get; set; }

    /// <summary>Radians, as stored in the reference.</summary>
    public required Vector3 Rotation { get; set; }

    public required float Scale { get; set; }

    /// <summary>Extra model scale that is not part of the reference (NPC height × race height).</summary>
    public float ModelScale { get; init; } = 1f;
    public required NifTransform Transform { get; set; }
    public required Matrix4x4 World { get; set; }

    /// <summary>Hidden by a pending delete.</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Has unsaved edits.</summary>
    public bool IsModified { get; set; }

    /// <summary>State as loaded from the plugins (before any edit of this session).</summary>
    public Core.RefState LoadedState { get; set; }

    public Core.RefState State => new(Position, Rotation, Scale, IsDeleted);

    /// <summary>Moves the object to a state and recomputes its matrices and bounds.</summary>
    public void Apply(Core.RefState state)
    {
        Position = state.Position;
        Rotation = state.Rotation;
        Scale = state.Scale;
        IsDeleted = state.Deleted;
        Transform = SceneBuilder.ReferenceTransform(Position, Kind == SceneObjectKind.Actor ? new Vector3(0, 0, Rotation.Z) : Rotation, Scale * ModelScale);
        World = Transform.ToMatrix();
        UpdateBounds();
    }

    public SceneLight? Light { get; init; }
    public bool InitiallyDisabled { get; init; }
    public bool IsPersistent { get; init; }

    /// <summary>For static collection parts: index of the part placement, -1 otherwise.</summary>
    public int CollectionPart { get; init; } = -1;

    /// <summary>Resolved once the mesh is loaded.</summary>
    public GpuModel? Model { get; set; }

    public bool ModelFailed { get; set; }

    public Vector3 BoundsCenter { get; set; }
    public float BoundsRadius { get; set; } = 32f;

    public void UpdateBounds()
    {
        if (Model is { IsEmpty: false } m)
        {
            BoundsCenter = Transform.Apply(m.Center);
            BoundsRadius = Math.Max(m.Radius * Scale, 8f);
        }
        else
        {
            BoundsCenter = Position;
            BoundsRadius = Kind == SceneObjectKind.Actor ? 70f : 24f;
        }
    }
}

/// <summary>Height field of one exterior cell: 33×33 vertices, 128 units apart.</summary>
public sealed class TerrainCell
{
    public const int Size = 33;
    public const float CellSize = 4096f;
    public const float Spacing = CellSize / (Size - 1);

    public required FormKey Land { get; init; }

    /// <summary>Exterior CELL the land belongs to.</summary>
    public FormKey Cell { get; init; }

    /// <summary>Editable source of the heights/layers (shared arrays: edits show on rebuild).</summary>
    public Core.LandscapeData? Data { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required float[] Heights { get; init; }
    public required Vector3[] Normals { get; init; }
    public required uint[] Colors { get; init; }

    /// <summary>Per quadrant (0 SW, 1 SE, 2 NW, 3 NE): base texture then alpha layers in order.</summary>
    public required List<TerrainLayer>[] Quadrants { get; init; }

    public Vector3 Origin => new(X * CellSize, Y * CellSize, 0);

    public float HeightAt(int vx, int vy) => Heights[vy * Size + vx];
}

/// <summary>A texture on a quadrant. <see cref="Opacity"/> is null for the base layer (opaque).</summary>
public sealed record TerrainLayer(string TexturePath, float[]? Opacity);

public sealed record WaterPlane(Vector2 Min, Vector2 Max, float Height);

/// <summary>Navmesh parts selected in the editor (one navmesh at a time).</summary>
public sealed class NavmeshSelection
{
    public required FormKey Mesh { get; init; }
    public HashSet<int> Vertices { get; } = [];
    public HashSet<int> Triangles { get; } = [];
    public bool IsEmpty => Vertices.Count == 0 && Triangles.Count == 0;
}

/// <summary>A navmesh of a loaded cell: the winning NAVM, or the session's edited copy.</summary>
public sealed class NavmeshView
{
    public required FormKey Key { get; init; }
    public required FormKey Cell { get; init; }
    public FormKey? World { get; init; }
    public required Mutagen.Bethesda.Skyrim.INavigationMeshGetter Record { get; set; }

    /// <summary>Bumped when <see cref="Record"/> changes, so cached overlay geometry is rebuilt.</summary>
    public int Version { get; set; }
}

/// <summary>Ambient + directional + fog of an interior (XCLL / lighting template) or the default sun.</summary>
public sealed record SceneLighting(
    Vector3 Ambient,
    Vector3 DirectionalColor,
    Vector3 DirectionTowardsLight,
    Vector3 FogColor,
    float FogNear,
    float FogFar)
{
    public static readonly SceneLighting DefaultExterior = new(
        new Vector3(0.42f, 0.44f, 0.48f),
        new Vector3(0.95f, 0.90f, 0.80f),
        Vector3.Normalize(new Vector3(0.45f, -0.35f, 0.82f)),
        new Vector3(0.62f, 0.68f, 0.76f),
        12000f,
        150000f);

    public static readonly SceneLighting DefaultInterior = new(
        new Vector3(0.30f, 0.30f, 0.32f),
        new Vector3(0.35f, 0.33f, 0.30f),
        Vector3.Normalize(new Vector3(0.3f, 0.2f, 0.9f)),
        Vector3.Zero,
        0,
        0);
}

public sealed class Scene
{
    public required string Title { get; init; }
    public required bool IsInterior { get; init; }
    public FormKey? Worldspace { get; init; }
    public (int X, int Y) CenterCell { get; init; }
    public List<SceneObject> Objects { get; } = [];
    public List<TerrainCell> Terrain { get; } = [];
    public List<WaterPlane> Water { get; } = [];
    public List<NavmeshView> Navmeshes { get; } = [];
    public required SceneLighting Lighting { get; init; }
    public List<string> Warnings { get; } = [];

    /// <summary>Where to put the camera first.</summary>
    public Vector3 Focus { get; set; }

    public float FocusRadius { get; set; } = 1000f;

    /// <summary>Remembers the loaded state of every object and re-applies pending edits.</summary>
    public void ApplyDocument(Core.EditDocument document)
    {
        foreach (var obj in Objects)
        {
            obj.LoadedState = obj.State;
            if (document.TryGet(obj.Reference, out var state))
            {
                obj.Apply(state);
                obj.IsModified = true;
            }
        }
    }
}
