using System.Numerics;
using System.Runtime.InteropServices;
using StbEditor.Nif;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace StbEditor.Render;

public enum ShadingMode
{
    Lit = 0,
    Unlit = 1,
    Normals = 2,
    Untextured = 3,
}

/// <summary>What to draw; toggled from the render window toolbar.</summary>
public sealed class RenderOptions
{
    /// <summary>Applied to actors when <see cref="HighlightActors"/> is on: light cyan, a third of the colour.</summary>
    public static readonly System.Numerics.Vector4 ActorTint = new(0.45f, 0.85f, 1f, 0.35f);

    public ShadingMode Shading { get; set; } = ShadingMode.Lit;
    public bool Wireframe { get; set; }
    public bool ShowMarkers { get; set; } = true;
    public bool ShowTerrain { get; set; } = true;
    public bool ShowWater { get; set; } = true;

    /// <summary>Grass on the land (LTEX grass types, placed like the game does).</summary>
    public bool ShowGrass { get; set; } = true;
    public bool ShowInitiallyDisabled { get; set; } = true;
    public bool ShowLightRadius { get; set; } = true;

    /// <summary>Kinds of objects not drawn or picked (CK: Show/Hide), by <see cref="Group"/>.</summary>
    public HashSet<string> HiddenGroups { get; } = [];

    /// <summary>References of hidden layers: neither drawn nor picked.</summary>
    public HashSet<Mutagen.Bethesda.Plugins.FormKey> HiddenReferences { get; } = [];

    /// <summary>References hidden by hand (key 1 on the selection): only in the view, never saved.</summary>
    public HashSet<Mutagen.Bethesda.Plugins.FormKey> HiddenByHand { get; } = [];

    /// <summary>References of frozen layers: drawn, but a click does not select them (they still catch placed objects).</summary>
    public HashSet<Mutagen.Bethesda.Plugins.FormKey> FrozenReferences { get; } = [];

    /// <summary>Outdoors: only the centre cell's objects (CK: Current Cell Only).</summary>
    public bool CurrentCellOnly { get; set; }
    public Mutagen.Bethesda.Plugins.FormKey? CurrentCell { get; set; }

    /// <summary>The Show/Hide group of a base record type.</summary>
    public static string Group(string signature) => signature switch
    {
        "STAT" or "MSTT" or "SCOL" or "TXST" => "Statics",
        "FURN" => "Furniture",
        "CONT" => "Containers",
        "DOOR" => "Doors",
        "LIGH" => "Lights",
        "NPC_" or "LVLN" or "ACHR" => "Actors",
        "ACTI" or "TACT" => "Activators",
        "FLOR" or "TREE" or "GRAS" => "Flora and trees",
        "MISC" or "WEAP" or "ARMO" or "AMMO" or "ALCH" or "BOOK" or "INGR" or "KEYM" or "SCRL" or "SLGM" or "LVLI" => "Items",
        "SOUN" => "Sounds",
        "HAZD" or "EXPL" or "PROJ" or "ARTO" or "EFSH" => "Effects",
        "IDLM" => "Idle markers",
        _ => "Other",
    };

    public static readonly string[] Groups = ["Statics", "Furniture", "Containers", "Doors", "Lights", "Actors", "Activators", "Flora and trees", "Items", "Sounds", "Effects", "Idle markers", "Other"];

    /// <summary>Whether an object is left out of drawing and picking by the view options.</summary>
    public bool Hides(SceneObject obj) =>
        (obj.InitiallyDisabled && !ShowInitiallyDisabled)
        || (HiddenGroups.Count > 0 && HiddenGroups.Contains(Group(obj.BaseSignature)))
        || (CurrentCellOnly && CurrentCell is { } cell && obj.Cell != cell)
        || (HiddenReferences.Count > 0 && HiddenReferences.Contains(obj.Reference))
        || (HiddenByHand.Count > 0 && HiddenByHand.Contains(obj.Reference));
    public bool Fog { get; set; } = true;

    /// <summary>Draw the Havok collision of objects near the camera as lines (CK: F4).</summary>
    public bool ShowCollision { get; set; }
    public bool CellBorders { get; set; }

    /// <summary>Play shader animations (flames, water flow) and simulate particles.</summary>
    public bool Animate { get; set; } = true;

    /// <summary>Tint NPCs and creatures so they stand out in dark interiors.</summary>
    public bool HighlightActors { get; set; } = true;
    public bool ShowParticles { get; set; } = true;

    /// <summary>Navmesh overlay (CK: the navmesh view of the render window).</summary>
    public bool ShowNavmesh { get; set; }

    /// <summary>Multiplier on ambient + directional light (CK users raise interiors to work in them).</summary>
    public float Brightness { get; set; } = 1.6f;

    /// <summary>Debug isolation of passes.</summary>
    public bool DrawOpaquePass { get; set; } = true;
    public bool DrawBlendedPass { get; set; } = true;
    public bool DrawMarkerPass { get; set; } = true;

    /// <summary>Debug: draw only particle systems whose name contains this.</summary>
    public string? ParticleFilter { get; set; }
}

public sealed class RenderStats
{
    public int Objects;
    public int Visible;
    public int DrawCalls;
    public int Triangles;
    public int Lights;
    public int Grass;
}

/// <summary>Colour + depth target the scene renders into; shown in ImGui as an image.</summary>
public sealed class ViewportTarget : IDisposable
{
    private readonly ID3D11Device _device;
    private ID3D11Texture2D? _color;
    private ID3D11Texture2D? _depth;

    public ID3D11RenderTargetView? ColorView { get; private set; }
    public ID3D11ShaderResourceView? ShaderView { get; private set; }
    public ID3D11DepthStencilView? DepthView { get; private set; }
    public ID3D11Texture2D? ColorTexture => _color;
    public int Width { get; private set; }
    public int Height { get; private set; }

    public ViewportTarget(ID3D11Device device) => _device = device;

    public void EnsureSize(int width, int height)
    {
        width = Math.Max(width, 8);
        height = Math.Max(height, 8);
        if (width == Width && height == Height && ColorView is not null) return;

        Release();
        Width = width;
        Height = height;

        _color = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        ColorView = _device.CreateRenderTargetView(_color);
        ShaderView = _device.CreateShaderResourceView(_color);

        _depth = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
        });
        DepthView = _device.CreateDepthStencilView(_depth);
    }

    private void Release()
    {
        ColorView?.Dispose();
        ShaderView?.Dispose();
        DepthView?.Dispose();
        _color?.Dispose();
        _depth?.Dispose();
        ColorView = null;
        ShaderView = null;
        DepthView = null;
    }

    public void Dispose() => Release();
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LineVertex(Vector3 position, uint color)
{
    public Vector3 Position = position;
    public uint Color = color;
}

/// <summary>
/// Forward renderer for one scene. Opaque shapes are grouped by GPU shape and drawn instanced;
/// blended ones are sorted back to front; terrain is base layer plus blended alpha layers.
/// </summary>
public sealed unsafe class SceneRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct FrameConstants
    {
        public Matrix4x4 ViewProj;
        public Vector4 CameraPos;
        public Vector4 SunDir;
        public Vector4 SunColor;
        public Vector4 Ambient;
        public Vector4 FogColor;
        public Vector4 FogRange;
        public Vector4 Mode;
        public Vector4 CameraRight;
        public Vector4 CameraUp;
        public Vector4 CameraForward;
        public fixed float LightPos[Shaders.MaxLights * 4];
        public fixed float LightColor[Shaders.MaxLights * 4];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct MaterialConstants
    {
        public Vector4 UvTransform;
        public Vector4 Params;
        public Vector4 SpecColor;
        public Vector4 Emissive;
        public Vector4 TintColor;
        public Vector4 Billboard;
        public Vector4 BillboardUp;
        public uint Flags;
        public uint Pad0, Pad1, Pad2;
    }

    [Flags]
    private enum MaterialFlags : uint
    {
        VertexColor = 1,
        VertexAlpha = 2,
        ModelSpaceNormals = 4,
        Effect = 8,
        Specular = 16,
        GlowMap = 32,
        SpecularMap = 64,
        OwnEmit = 128,
        Tint = 256,
        Flat = 512,
        PaletteColor = 1024,
        PaletteAlpha = 2048,
        FaceGen = 4096,
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly MeshCache _meshes;
    private readonly TextureCache _textures;

    private readonly ID3D11VertexShader _meshVs;
    private readonly ID3D11PixelShader _meshPs;
    private readonly ID3D11InputLayout _meshLayout;
    private readonly ID3D11VertexShader _terrainVs;
    private readonly ID3D11PixelShader _terrainPs;
    private readonly ID3D11InputLayout _terrainLayout;
    private readonly ID3D11VertexShader _lineVs;
    private readonly ID3D11PixelShader _linePs;
    private readonly ID3D11InputLayout _lineLayout;
    private readonly ID3D11VertexShader _particleVs;
    private readonly ID3D11InputLayout _particleLayout;
    private ID3D11Buffer? _particleBuffer;
    private ID3D11Buffer? _particleIndices;
    private int _particleCapacity;
    private readonly Dictionary<NifParticleSystem, ParticleRuntime> _particleRuntimes = new();
    private readonly List<ParticleVertex> _particleVertices = [];
    private long _frameNumber;
    private double _time;
    private long _lastTick = System.Diagnostics.Stopwatch.GetTimestamp();

    private readonly ID3D11Buffer _frameBuffer;
    private readonly ID3D11Buffer _materialBuffer;
    private ID3D11Buffer _instanceBuffer;
    private int _instanceCapacity = 16384;
    private ID3D11Buffer _lineBuffer;
    private int _lineCapacity = 65536;
    private readonly ID3D11Buffer _terrainIndices;
    private readonly int _terrainIndexCount;

    private readonly ID3D11RasterizerState _cullBack;
    private readonly ID3D11RasterizerState _cullNone;
    private readonly ID3D11RasterizerState _wireframe;
    private readonly ID3D11RasterizerState _overlayWire;
    private readonly ID3D11DepthStencilState _depthWrite;
    private readonly ID3D11DepthStencilState _depthRead;
    private readonly ID3D11DepthStencilState _depthOverlay;
    private readonly ID3D11BlendState _opaque;
    private readonly ID3D11BlendState _alphaBlend;
    private readonly Dictionary<int, ID3D11BlendState> _niBlends = new();
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11SamplerState _clampSampler;

    private readonly GpuModel _markerBox;
    private readonly GpuModel _actorCapsule;

    private readonly Dictionary<TerrainCell, TerrainGpu> _terrain = new();
    private readonly List<LineVertex> _lines = [];

    public RenderOptions Options { get; } = new();

    /// <summary>Set to log the blended shapes of one frame (debugging).</summary>
    public Action<string>? DebugLog { get; set; }
    private bool _loggedBlended;
    public void ResetDebugLog() => _loggedBlended = false;
    public RenderStats Stats { get; } = new();

    /// <summary>Selected navmesh parts (drawn highlighted).</summary>
    public NavmeshSelection? NavSelection { get; set; }

    /// <summary>Editor overlay (landscape brush outline…), drawn on top as line pairs.</summary>
    public List<LineVertex> OverlayLines { get; } = [];

    /// <summary>Seconds of animation time (advances only while frames are drawn with Animate on).</summary>
    public double Time => _time;

    /// <summary>Last frame showed something animated: keep frames coming.</summary>
    public bool HasAnimation { get; private set; }
    public MeshCache Meshes => _meshes;
    public TextureCache Textures => _textures;

    public SceneRenderer(ID3D11Device device, ID3D11DeviceContext context, MeshCache meshes, TextureCache textures)
    {
        _device = device;
        _context = context;
        _meshes = meshes;
        _textures = textures;

        (_meshVs, _meshPs, _meshLayout) = CreateProgram(Shaders.Mesh, "mesh",
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("TANGENT", 0, Format.R32G32B32_Float, 24, 0),
            new InputElementDescription("BINORMAL", 0, Format.R32G32B32_Float, 36, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 48, 0),
            new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 56, 0),
            new InputElementDescription("WORLD", 0, Format.R32G32B32A32_Float, 0, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("WORLD", 1, Format.R32G32B32A32_Float, 16, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("WORLD", 2, Format.R32G32B32A32_Float, 32, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("WORLD", 3, Format.R32G32B32A32_Float, 48, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("HIGHLIGHT", 0, Format.R32G32B32A32_Float, 64, 1, InputClassification.PerInstanceData, 1),
        ]);

        (_terrainVs, _terrainPs, _terrainLayout) = CreateProgram(Shaders.Terrain, "terrain",
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 24, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 28, 0),
            new InputElementDescription("TEXCOORD", 1, Format.R32_Float, 36, 0),
        ]);

        var particleCode = Compile(Shaders.Mesh, "VSParticle", "vs_5_0", "particles");
        _particleVs = device.CreateVertexShader(particleCode.Span);
        _particleLayout = device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 1, Format.R32G32_Float, 12, 0),
            new InputElementDescription("TEXCOORD", 2, Format.R32G32_Float, 20, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 28, 0),
            new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 36, 0),
            new InputElementDescription("TEXCOORD", 3, Format.R32G32B32A32_Float, 40, 0),
            new InputElementDescription("WORLD", 0, Format.R32G32B32A32_Float, 0, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("WORLD", 1, Format.R32G32B32A32_Float, 16, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("WORLD", 2, Format.R32G32B32A32_Float, 32, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("WORLD", 3, Format.R32G32B32A32_Float, 48, 1, InputClassification.PerInstanceData, 1),
            new InputElementDescription("HIGHLIGHT", 0, Format.R32G32B32A32_Float, 64, 1, InputClassification.PerInstanceData, 1),
        ], particleCode.Span);

        (_lineVs, _linePs, _lineLayout) = CreateProgram(Shaders.Lines, "lines",
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 12, 0),
        ]);

        _frameBuffer = device.CreateBuffer(new BufferDescription((uint)sizeof(FrameConstants), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _materialBuffer = device.CreateBuffer(new BufferDescription((uint)sizeof(MaterialConstants), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _instanceBuffer = CreateDynamicVertexBuffer(_instanceCapacity * InstanceData.Size);
        _lineBuffer = CreateDynamicVertexBuffer(_lineCapacity * sizeof(LineVertex));

        var terrainIndices = TerrainGpu.QuadrantIndices();
        _terrainIndices = device.CreateBuffer(terrainIndices, BindFlags.IndexBuffer, ResourceUsage.Immutable);
        _terrainIndexCount = terrainIndices.Length;

        // NIF geometry (and our terrain) is counter-clockwise in front, like OpenGL.
        _cullBack = device.CreateRasterizerState(new RasterizerDescription(CullMode.Back, FillMode.Solid) { DepthClipEnable = true, FrontCounterClockwise = true });
        _cullNone = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { DepthClipEnable = true, FrontCounterClockwise = true });
        _wireframe = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Wireframe) { DepthClipEnable = true, FrontCounterClockwise = true });
        _overlayWire = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Wireframe)
        {
            DepthClipEnable = true,
            FrontCounterClockwise = true,
            DepthBias = 0,
            SlopeScaledDepthBias = 0,
        });

        // Reversed Z: nearer is greater.
        _depthWrite = device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.Greater));
        _depthRead = device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.GreaterEqual));
        _depthOverlay = device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.GreaterEqual));

        _opaque = device.CreateBlendState(BlendDescription.Opaque);
        _alphaBlend = device.CreateBlendState(BlendDescription.NonPremultiplied);

        _sampler = device.CreateSamplerState(new SamplerDescription(Filter.Anisotropic, TextureAddressMode.Wrap, TextureAddressMode.Wrap, TextureAddressMode.Wrap)
        {
            MaxAnisotropy = 8,
            MaxLOD = float.MaxValue,
        });

        _clampSampler = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp)
        {
            MaxLOD = float.MaxValue,
        });

        _markerBox = GpuModel.Create(device, "<marker>", [BuiltinShapes.Octahedron(16f)]);
        _actorCapsule = GpuModel.Create(device, "<actor>", [BuiltinShapes.Box(new Vector3(-22, -22, 0), new Vector3(22, 22, 128))]);
    }

    private (ID3D11VertexShader, ID3D11PixelShader, ID3D11InputLayout) CreateProgram(string source, string name, InputElementDescription[] layout)
    {
        var vs = Compile(source, "VSMain", "vs_5_0", name);
        var ps = Compile(source, "PSMain", "ps_5_0", name);
        var vertexShader = _device.CreateVertexShader(vs.Span);
        var pixelShader = _device.CreatePixelShader(ps.Span);
        var inputLayout = _device.CreateInputLayout(layout, vs.Span);
        return (vertexShader, pixelShader, inputLayout);
    }

    private static ReadOnlyMemory<byte> Compile(string source, string entry, string profile, string name)
    {
        var result = Compiler.Compile(source, entry, name + ".hlsl", profile, out var blob, out var errors);
        try
        {
            if (result.Failure || blob is null)
                throw new InvalidOperationException($"{name} {entry}: {errors?.AsString() ?? result.ToString()}");
            return blob.AsBytes();
        }
        finally
        {
            blob?.Dispose();
            errors?.Dispose();
        }
    }

    private ID3D11Buffer CreateDynamicVertexBuffer(int bytes) =>
        _device.CreateBuffer(new BufferDescription((uint)bytes, BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

    // ------------------------------------------------------------------ frame

    private readonly record struct Batch(GpuShape Shape, int FirstInstance, int Count);
    private readonly record struct Blended(GpuShape? Shape, InstanceData Instance, float Distance, ParticleRuntime? Particles = null);

    private Camera? _camera;
    private readonly Dictionary<GpuShape, List<InstanceData>> _buckets = new();
    private readonly List<Blended> _blended = [];
    private readonly List<InstanceData> _markers = [];
    private readonly List<InstanceData> _actors = [];
    private readonly List<(GpuShape Shape, InstanceData Instance)> _selectedShapes = [];

    public void Render(Scene? scene, Camera camera, ViewportTarget target, IReadOnlySet<SceneObject> selection, Vector4 background)
    {
        if (target.ColorView is null || target.DepthView is null) return;
        RenderTo(scene, camera, target.ColorView, target.DepthView, target.Width, target.Height, selection, background);
    }

    /// <summary>Renders into any colour + depth pair (viewport, preview, thumbnails).</summary>
    public void RenderTo(Scene? scene, Camera camera, ID3D11RenderTargetView colorView, ID3D11DepthStencilView depthView,
        int width, int height, IReadOnlySet<SceneObject> selection, Vector4 background)
    {
        var ctx = _context;
        ctx.OMSetRenderTargets(colorView, depthView);
        ctx.ClearRenderTargetView(colorView, new Color4(background.X, background.Y, background.Z, background.W));
        ctx.ClearDepthStencilView(depthView, DepthStencilClearFlags.Depth, 0f, 0);
        ctx.RSSetViewport(0, 0, width, height);
        var target = (Width: width, Height: height);

        var stats = Stats;
        stats.Objects = stats.Visible = stats.DrawCalls = stats.Triangles = stats.Lights = stats.Grass = 0;

        var tick = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = (tick - _lastTick) / (double)System.Diagnostics.Stopwatch.Frequency;
        _lastTick = tick;
        if (Options.Animate) _time += Math.Clamp(elapsed, 0, 0.1);
        _frameNumber++;
        HasAnimation = false;

        if (scene is null) return;

        var aspect = target.Width / (float)target.Height;
        var viewProj = camera.View * camera.Projection(aspect);
        var frustum = new Frustum(viewProj);

        UpdateFrame(scene, camera, viewProj, frustum);
        _camera = camera;

        ctx.PSSetSamplers(0, [_sampler, _clampSampler]);
        ctx.VSSetConstantBuffers(0, [_frameBuffer]);
        ctx.PSSetConstantBuffers(0, [_frameBuffer]);

        if (Options.ShowTerrain) DrawTerrain(scene, frustum);

        CollectObjects(scene, camera, frustum, selection);
        if (Options.ShowGrass && Options.ShowTerrain && scene.Grasses is { } grassOf) CollectGrass(scene, camera, frustum, grassOf);
        if (Options.DrawOpaquePass) DrawOpaque();
        if (Options.ShowWater) DrawWater(scene);
        if (Options.ShowNavmesh) DrawNavmeshes(scene);
        if (Options.DrawBlendedPass) DrawBlended();
        if (Options.ShowMarkers) DrawPrimitives(scene, frustum);
        if (Options.DrawMarkerPass) DrawMarkers();
        DrawSelection(selection);
        DrawLines(scene, selection);
    }

    private void UpdateFrame(Scene scene, Camera camera, Matrix4x4 viewProj, Frustum frustum)
    {
        var lighting = scene.Lighting;
        var frame = new FrameConstants
        {
            ViewProj = viewProj,
            CameraPos = new Vector4(camera.Position, 1),
            SunDir = new Vector4(Vector3.Normalize(lighting.DirectionTowardsLight), 0),
            SunColor = new Vector4(lighting.DirectionalColor * Options.Brightness, 1),
            Ambient = new Vector4(lighting.Ambient * Options.Brightness, 1),
            FogColor = new Vector4(lighting.FogColor, Options.Fog && lighting.FogFar > lighting.FogNear && lighting.FogFar > 0 ? 1 : 0),
            FogRange = new Vector4(lighting.FogNear, lighting.FogFar, 0, 0),
            Mode = new Vector4((float)Options.Shading, 0, (float)_time, 0),
            CameraRight = new Vector4(camera.Right, 0),
            CameraUp = new Vector4(camera.Up, 0),
            CameraForward = new Vector4(camera.Forward, 0),
        };

        // Point lights: the nearest visible ones to the camera.
        var lights = scene.Objects
            .Where(o => o.Light is not null && !Options.Hides(o) && frustum.Intersects(o.Position, o.Light!.Radius))
            .OrderBy(o => Vector3.DistanceSquared(o.Position, camera.Position))
            .Take(Shaders.MaxLights)
            .ToList();

        for (var i = 0; i < lights.Count; i++)
        {
            var light = lights[i].Light!;
            var p = lights[i].Position;
            frame.LightPos[i * 4 + 0] = p.X;
            frame.LightPos[i * 4 + 1] = p.Y;
            frame.LightPos[i * 4 + 2] = p.Z;
            frame.LightPos[i * 4 + 3] = light.Radius;
            var c = light.Color * Math.Clamp(light.Fade, 0, 4);
            frame.LightColor[i * 4 + 0] = c.X;
            frame.LightColor[i * 4 + 1] = c.Y;
            frame.LightColor[i * 4 + 2] = c.Z;
        }

        frame.Mode.Y = scene.IsInterior || lights.Count > 0 ? lights.Count : 0;
        Stats.Lights = lights.Count;
        Upload(_frameBuffer, frame);
    }

    private void CollectObjects(Scene scene, Camera camera, Frustum frustum, IReadOnlySet<SceneObject> selection)
    {
        foreach (var list in _buckets.Values) list.Clear();
        _blended.Clear();
        _markers.Clear();
        _actors.Clear();
        _selectedShapes.Clear();

        foreach (var obj in scene.Objects)
        {
            Stats.Objects++;
            // Keep this scene's models marked as used; one the cache evicted meanwhile is asked for again.
            if (obj.Model is { } held)
            {
                if (held.IsDisposed) obj.Model = null;
                else _meshes.Get(held.Path);
            }
            if (obj.IsDeleted) continue;
            if (Options.Hides(obj)) continue;

            if (obj.Kind is SceneObjectKind.Mesh or SceneObjectKind.Actor && obj.ModelPath is not null && obj.Model is null && !obj.ModelFailed)
            {
                var model = _meshes.Get(obj.ModelPath!, out var state);
                if (state == LoadState.Ready && model is not null)
                {
                    obj.Model = model;
                    obj.UpdateBounds();
                }
                else if (state == LoadState.Failed)
                {
                    obj.ModelFailed = true;
                }
            }

            if (!frustum.Intersects(obj.BoundsCenter, obj.BoundsRadius)) continue;
            Stats.Visible++;

            var selected = selection.Contains(obj);
            var highlight = obj.IsModified ? new Vector4(1f, 0.85f, 0.35f, 0.12f)
                : obj.InitiallyDisabled ? new Vector4(0.6f, 0.2f, 0.9f, 0.25f)
                : obj.Kind == SceneObjectKind.Actor && Options.HighlightActors ? RenderOptions.ActorTint : Vector4.Zero;
            var instance = new InstanceData { World = obj.World, Highlight = highlight };

            // Actors whose look could not be put together stay boxes.
            if (obj.Kind == SceneObjectKind.Actor && obj.Model is not { Shapes.Count: > 0 })
            {
                _actors.Add(instance with { Highlight = new Vector4(0.25f, 0.75f, 0.35f, 1f) });
                if (selected) _selectedShapes.Add((_actorCapsule.Shapes[0], instance));
                continue;
            }

            var model2 = obj.Model;
            var drewSomething = false;
            if (model2 is not null)
            {
                foreach (var shape in model2.Shapes)
                {
                    if (!IsShapeVisible(shape.Source, Options)) continue;
                    if (shape.Source.IsSkinned && shape.Source.Positions.Length == 0) continue;
                    drewSomething = true;
                    if (shape.Material.Animations.Count > 0) HasAnimation = true;

                    if (selected) _selectedShapes.Add((shape, instance));

                    // Effect shaders without an alpha property draw opaque in the game too.
                    if (shape.Material.AlphaBlend)
                    {
                        var distance = Vector3.DistanceSquared(camera.Position, obj.BoundsCenter);
                        _blended.Add(new Blended(shape, instance, distance));
                        continue;
                    }

                    if (!_buckets.TryGetValue(shape, out var bucket))
                    {
                        bucket = [];
                        _buckets[shape] = bucket;
                    }
                    bucket.Add(instance);
                }

                if (model2.Particles.Count > 0 && Options.ShowParticles)
                {
                    drewSomething = true;
                    var distance = Vector3.DistanceSquared(camera.Position, obj.BoundsCenter);
                    // Far-away smoke is a few pixels: not worth simulating.
                    if (distance < 9000f * 9000f)
                    {
                        foreach (var system in model2.Particles)
                        {
                            if (system.IsHidden || (system.IsEditorMarker && !Options.ShowMarkers)) continue;
                            if (Options.ParticleFilter is { } filter && !system.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!_particleRuntimes.TryGetValue(system, out var runtime))
                            {
                                runtime = new ParticleRuntime(system);
                                _particleRuntimes[system] = runtime;
                            }
                            _blended.Add(new Blended(null, instance, distance, runtime));
                            HasAnimation = true;
                        }
                    }
                }
            }

            // Lights without a model, sound markers, broken models: a coloured marker.
            if (!drewSomething && Options.ShowMarkers)
            {
                var color = obj.Light is not null ? new Vector4(1f, 0.85f, 0.3f, 1f)
                    : obj.ModelFailed ? new Vector4(0.95f, 0.25f, 0.25f, 1f)
                    : new Vector4(0.35f, 0.6f, 1f, 1f);
                var marker = new InstanceData { World = Matrix4x4.CreateTranslation(obj.Position), Highlight = color };
                _markers.Add(marker);
                if (selected) _selectedShapes.Add((_markerBox.Shapes[0], marker));
            }
        }
    }

    /// <summary>
    /// The game culls "EditorMarker" nodes by name, whatever their flags; editors show them on
    /// request. Other hidden shapes (APP_CULLED) stay hidden.
    /// </summary>
    public static bool IsShapeVisible(NifShape shape, RenderOptions options) =>
        shape.IsEditorMarker ? options.ShowMarkers
        // Heat-haze / refraction cards only distort what is behind them; their "diffuse" is a
        // normal map, which drawn as colour is a rainbow sheet.
        : !shape.IsHidden && !shape.Material.Flags1.HasFlag(ShaderFlags1.Refraction);

    private void DrawOpaque()
    {
        var ctx = _context;
        var instances = new List<InstanceData>();
        var batches = new List<Batch>();

        foreach (var (shape, list) in _buckets)
        {
            if (list.Count == 0) continue;
            batches.Add(new Batch(shape, instances.Count, list.Count));
            instances.AddRange(list);
        }

        if (instances.Count == 0) return;
        UploadInstances(instances);

        ctx.IASetInputLayout(_meshLayout);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_meshVs);
        ctx.PSSetShader(_meshPs);
        ctx.OMSetBlendState(_opaque);
        ctx.OMSetDepthStencilState(_depthWrite);

        // Group by material order is not worth it here; sort by texture to cut state changes.
        batches.Sort((a, b) => string.CompareOrdinal(a.Shape.Material.Diffuse, b.Shape.Material.Diffuse));

        foreach (var batch in batches)
        {
            ctx.RSSetState(Options.Wireframe ? _wireframe : batch.Shape.Material.DoubleSided || batch.Shape.Source.Billboard is not null ? _cullNone : _cullBack);
            BindMaterial(batch.Shape.Material, flat: false, batch.Shape.Source);
            DrawShape(batch.Shape, batch.FirstInstance, batch.Count);
        }
    }

    private void DrawBlended()
    {
        if (_blended.Count == 0) return;
        var ctx = _context;

        // Back to front; equal distances (parts of one object) keep their NIF order.
        var order = Enumerable.Range(0, _blended.Count).ToArray();
        var keys = _blended.ToArray();
        Array.Sort(order, (a, b) => keys[a].Distance != keys[b].Distance ? keys[b].Distance.CompareTo(keys[a].Distance) : a.CompareTo(b));
        _blended.Clear();
        foreach (var index in order) _blended.Add(keys[index]);
        UploadInstances(_blended.Select(b => b.Instance).ToList());
        PrepareParticles();

        ctx.IASetInputLayout(_meshLayout);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_meshVs);
        ctx.PSSetShader(_meshPs);
        ctx.OMSetDepthStencilState(_depthRead);

        if (DebugLog is { } log && !_loggedBlended && _blended.Count > 0)
        {
            _loggedBlended = true;
            foreach (var shape in _blended.Select(b => b.Shape).OfType<GpuShape>().Distinct())
                log($"blended: {shape.Source.Name} {shape.Material.Kind} tex={shape.Material.Diffuse} alpha=0x{shape.Material.AlphaFlags:X} f1={(uint)shape.Material.Flags1:X} f2={(uint)shape.Material.Flags2:X} emis={shape.Material.EmissiveColor}");
        }

        for (var i = 0; i < _blended.Count; i++)
        {
            if (_blended[i].Particles is { } particles)
            {
                if (particles.Count == 0 || _particleBuffer is null) continue;
                var material = particles.Definition.Material;
                ctx.IASetInputLayout(_particleLayout);
                ctx.VSSetShader(_particleVs);
                ctx.OMSetBlendState(BlendFor(material));
                ctx.RSSetState(Options.Wireframe ? _wireframe : _cullNone);
                BindMaterial(material, flat: false, particles: true);
                ctx.IASetVertexBuffers(0, 2, [_particleBuffer, _instanceBuffer], [(uint)ParticleVertex.Stride, (uint)InstanceData.Size], [0u, 0u]);
                ctx.IASetIndexBuffer(_particleIndices, Format.R32_UInt, 0);
                ctx.DrawIndexedInstanced((uint)(particles.Count * 6), 1, 0, particles.BaseVertex, (uint)i);
                Stats.DrawCalls++;
                Stats.Triangles += particles.Count * 2;
                ctx.IASetInputLayout(_meshLayout);
                ctx.VSSetShader(_meshVs);
                continue;
            }

            var shape = _blended[i].Shape!;
            ctx.OMSetBlendState(BlendFor(shape.Material));
            ctx.RSSetState(Options.Wireframe ? _wireframe : _cullNone);
            BindMaterial(shape.Material, flat: false, shape.Source);
            DrawShape(shape, i, 1);
        }

        ctx.OMSetBlendState(_opaque);
    }

    private void DrawMarkers()
    {
        var ctx = _context;
        ctx.IASetInputLayout(_meshLayout);
        ctx.VSSetShader(_meshVs);
        ctx.PSSetShader(_meshPs);
        ctx.OMSetBlendState(_opaque);
        ctx.OMSetDepthStencilState(_depthWrite);
        ctx.RSSetState(_cullNone);

        foreach (var (model, list) in new[] { (_markerBox, _markers), (_actorCapsule, _actors) })
        {
            if (list.Count == 0) continue;
            UploadInstances(list);
            BindMaterial(model.Shapes[0].Material, flat: true);
            DrawShape(model.Shapes[0], 0, list.Count);
        }
    }

    private void DrawSelection(IReadOnlySet<SceneObject> selection)
    {
        if (_selectedShapes.Count == 0) return;
        var ctx = _context;

        var accent = new Vector4(1f, 0.55f, 0.1f, 1f);
        UploadInstances(_selectedShapes.Select(s => s.Instance with { Highlight = accent }).ToList());

        ctx.IASetInputLayout(_meshLayout);
        ctx.VSSetShader(_meshVs);
        ctx.PSSetShader(_meshPs);
        ctx.OMSetBlendState(_opaque);
        ctx.OMSetDepthStencilState(_depthOverlay);
        ctx.RSSetState(_overlayWire);

        for (var i = 0; i < _selectedShapes.Count; i++)
        {
            BindMaterial(_selectedShapes[i].Shape.Material, flat: true, _selectedShapes[i].Shape.Source);
            DrawShape(_selectedShapes[i].Shape, i, 1);
        }
    }

    private void DrawShape(GpuShape shape, int firstInstance, int count)
    {
        var ctx = _context;
        ctx.IASetVertexBuffers(0, 2, [shape.VertexBuffer, _instanceBuffer], [(uint)MeshVertex.Size, (uint)InstanceData.Size], [0u, 0u]);
        ctx.IASetIndexBuffer(shape.IndexBuffer, Format.R32_UInt, 0);
        ctx.DrawIndexedInstanced((uint)shape.IndexCount, (uint)count, 0, 0, (uint)firstInstance);
        Stats.DrawCalls++;
        Stats.Triangles += shape.IndexCount / 3 * count;
    }

    /// <summary>
    /// Advances every particle system seen this frame (once, however many references share it)
    /// and writes their quads into one dynamic buffer.
    /// </summary>
    private void PrepareParticles()
    {
        _particleVertices.Clear();
        foreach (var entry in _blended)
        {
            if (entry.Particles is not { } runtime || runtime.LastFrame == _frameNumber) continue;
            runtime.LastFrame = _frameNumber;
            runtime.Advance(_time);
            runtime.BaseVertex = _particleVertices.Count;
            var before = _particleVertices.Count;
            runtime.Write(_particleVertices);
            runtime.VisibleCount = (_particleVertices.Count - before) / 4;
        }

        if (_particleVertices.Count == 0) return;
        var quads = _particleVertices.Count / 4;
        if (quads > _particleCapacity || _particleBuffer is null)
        {
            _particleBuffer?.Dispose();
            _particleIndices?.Dispose();
            _particleCapacity = Math.Max(quads, Math.Max(_particleCapacity * 2, 4096));
            _particleBuffer = CreateDynamicVertexBuffer(_particleCapacity * 4 * ParticleVertex.Stride);
            var indices = new uint[_particleCapacity * 6];
            for (uint q = 0; q < _particleCapacity; q++)
            {
                var v = q * 4;
                var k = q * 6;
                indices[k] = v;
                indices[k + 1] = v + 1;
                indices[k + 2] = v + 2;
                indices[k + 3] = v;
                indices[k + 4] = v + 2;
                indices[k + 5] = v + 3;
            }
            _particleIndices = _device.CreateBuffer(indices, BindFlags.IndexBuffer, ResourceUsage.Immutable);
        }

        var mapped = _context.Map(_particleBuffer, 0, MapMode.WriteDiscard);
        CollectionsMarshal.AsSpan(_particleVertices).CopyTo(new Span<ParticleVertex>((void*)mapped.DataPointer, _particleVertices.Count));
        _context.Unmap(_particleBuffer, 0);
    }

    /// <summary>Forget particle state of models that are gone (call when the scene changes).</summary>
    public void ResetParticles() => _particleRuntimes.Clear();

    private void BindMaterial(NifMaterial m, bool flat, NifShape? shape = null, bool particles = false)
    {
        var flags = flat ? MaterialFlags.Flat : 0;
        ID3D11ShaderResourceView diffuse = _textures.White, normal = _textures.FlatNormal, glow = _textures.Black, spec = _textures.Black;
        ID3D11ShaderResourceView faceTint = _textures.Black, faceDetail = _textures.Black;

        if (!flat)
        {
            if (m.Diffuse is { } d) diffuse = Texture(d, out var state) ?? (state == LoadState.Failed ? _textures.Missing : _textures.White);
            if (m.Kind == ShaderKind.Lighting)
            {
                if (m.Normal is { } n) normal = Texture(n) ?? _textures.FlatNormal;
                if (m.ModelSpaceNormals) flags |= MaterialFlags.ModelSpaceNormals;
                if (m.ModelSpaceNormals && m.Specular is { } s && Texture(s) is { } specView)
                {
                    spec = specView;
                    flags |= MaterialFlags.SpecularMap;
                }
                if (m.Flags1.HasFlag(ShaderFlags1.Specular)) flags |= MaterialFlags.Specular;
                if (m.Flags2.HasFlag(ShaderFlags2.GlowMap) && m.Glow is { } g && Texture(g) is { } glowView)
                {
                    glow = glowView;
                    flags |= MaterialFlags.GlowMap;
                }
                else if (m.Flags1.HasFlag(ShaderFlags1.OwnEmit))
                {
                    flags |= MaterialFlags.OwnEmit;
                }
                if (m.LightingType is LightingShaderType.HairTint) flags |= MaterialFlags.Tint;
                // FaceGen tint (slot 6) and detail (slot 3): both must be there, or the head stays plain.
                if (m.LightingType is LightingShaderType.FaceTint && m.Textures.Length > 6
                    && m.Textures[6].Length > 0 && m.Textures[3].Length > 0
                    && Texture(m.Textures[6]) is { } tintView && Texture(m.Textures[3]) is { } detailView)
                {
                    faceTint = tintView;
                    faceDetail = detailView;
                    flags |= MaterialFlags.FaceGen;
                }
            }
            else if (m.Kind == ShaderKind.Effect)
            {
                flags |= MaterialFlags.Effect;
                var palette = (m.Flags1 & (ShaderFlags1.GreyscaleToPaletteColor | ShaderFlags1.GreyscaleToPaletteAlpha)) != 0;
                if (palette && m.GreyscaleTexture is { } grey && Texture(grey) is { } paletteView)
                {
                    spec = paletteView;
                    if (m.Flags1.HasFlag(ShaderFlags1.GreyscaleToPaletteColor)) flags |= MaterialFlags.PaletteColor;
                    if (m.Flags1.HasFlag(ShaderFlags1.GreyscaleToPaletteAlpha)) flags |= MaterialFlags.PaletteAlpha;
                }
            }

            if (m.HasVertexColors) flags |= MaterialFlags.VertexColor;
            if (m.Flags1.HasFlag(ShaderFlags1.VertexAlpha)) flags |= MaterialFlags.VertexAlpha;
            // Particle colour and fades always reach the shader as vertex colour.
            if (particles) flags |= MaterialFlags.VertexColor | MaterialFlags.VertexAlpha;
        }

        // Shader controllers: scrolling and atlas-stepping UVs, pulsing alpha/emissive.
        var uvOffset = m.UvOffset;
        var uvScale = m.UvScale;
        var alpha = m.Alpha;
        var emissiveMultiple = m.EmissiveMultiple;
        var emissiveAlpha = m.Kind == ShaderKind.Effect ? m.EmissiveColor.W : 1f;
        foreach (var animation in m.Animations)
        {
            var value = animation.Evaluate(_time);
            switch (animation.Target)
            {
                case AnimatedValue.UOffset: uvOffset.X = value; break;
                case AnimatedValue.VOffset: uvOffset.Y = value; break;
                case AnimatedValue.UScale: uvScale.X = value; break;
                case AnimatedValue.VScale: uvScale.Y = value; break;
                case AnimatedValue.EmissiveMultiple: emissiveMultiple = value; break;
                case AnimatedValue.Alpha when m.Kind == ShaderKind.Effect: emissiveAlpha = value; break;
                case AnimatedValue.Alpha: alpha = value; break;
            }
        }

        var constants = new MaterialConstants
        {
            UvTransform = new Vector4(uvOffset, uvScale.X, uvScale.Y),
            Params = new Vector4(m.AlphaTest ? m.AlphaThreshold / 255f : -1f, alpha, m.Glossiness, m.SpecularStrength),
            // Effect shaders get the base colour raw and the multiple apart (the palette row comes from base red).
            SpecColor = new Vector4(m.SpecularColor, m.Kind == ShaderKind.Effect ? emissiveMultiple : 1),
            Emissive = m.Kind == ShaderKind.Effect
                ? new Vector4(m.EmissiveColor.X, m.EmissiveColor.Y, m.EmissiveColor.Z, emissiveAlpha)
                : new Vector4(new Vector3(m.EmissiveColor.X, m.EmissiveColor.Y, m.EmissiveColor.Z) * emissiveMultiple, emissiveAlpha),
            TintColor = new Vector4(m.TintColor, 1),
            Billboard = shape?.Billboard is { } mode ? new Vector4(shape.BillboardOrigin, mode == BillboardMode.FaceCamera ? 1 : 2) : Vector4.Zero,
            BillboardUp = new Vector4(shape?.BillboardUp ?? Vector3.UnitZ, 0),
            Flags = (uint)flags,
        };

        Upload(_materialBuffer, constants);
        _context.PSSetConstantBuffers(1, [_materialBuffer]);
        _context.VSSetConstantBuffers(1, [_materialBuffer]);
        _context.PSSetShaderResources(0, [diffuse, normal, glow, spec, faceTint, faceDetail]);
    }

    private ID3D11ShaderResourceView? Texture(string path) => Texture(path, out _);

    private ID3D11ShaderResourceView? Texture(string path, out LoadState state)
    {
        var normalized = Assets.AssetFileSystem.TexturePath(path);
        return _textures.Get(normalized, out state);
    }

    /// <summary>NiAlphaProperty source/destination factors (bits 1–4 and 5–8).</summary>
    private ID3D11BlendState BlendFor(NifMaterial m)
    {
        if (!m.AlphaBlend) return m.Kind == ShaderKind.Effect ? _alphaBlend : _opaque;

        var key = (m.AlphaFlags >> 1) & 0xFF;
        if (_niBlends.TryGetValue(key, out var state)) return state;

        static Blend Factor(int value) => value switch
        {
            0 => Blend.One,
            1 => Blend.Zero,
            2 => Blend.SourceColor,
            3 => Blend.InverseSourceColor,
            4 => Blend.DestinationColor,
            5 => Blend.InverseDestinationColor,
            6 => Blend.SourceAlpha,
            7 => Blend.InverseSourceAlpha,
            8 => Blend.DestinationAlpha,
            9 => Blend.InverseDestinationAlpha,
            10 => Blend.SourceAlphaSaturate,
            _ => Blend.One,
        };

        // D3D11 forbids *_COLOR factors on the alpha channel (E_INVALIDARG) and SRC_ALPHA_SAT as a
        // destination; NIFs use both, so the alpha channel gets the alpha equivalents.
        static Blend AlphaFactor(Blend b) => b switch
        {
            Blend.SourceColor => Blend.SourceAlpha,
            Blend.InverseSourceColor => Blend.InverseSourceAlpha,
            Blend.DestinationColor => Blend.DestinationAlpha,
            Blend.InverseDestinationColor => Blend.InverseDestinationAlpha,
            Blend.SourceAlphaSaturate => Blend.One,
            _ => b,
        };

        var src = Factor(key & 0xF);
        var dst = Factor((key >> 4) & 0xF);
        if (dst == Blend.SourceAlphaSaturate) dst = Blend.One;

        var description = new BlendDescription(src, dst);
        description.RenderTarget[0].SourceBlendAlpha = AlphaFactor(src);
        description.RenderTarget[0].DestinationBlendAlpha = AlphaFactor(dst);

        try
        {
            state = _device.CreateBlendState(description);
        }
        catch
        {
            state = _alphaBlend;
        }

        _niBlends[key] = state;
        return state;
    }

    // ------------------------------------------------------------------ terrain & water

    private void DrawTerrain(Scene scene, Frustum frustum)
    {
        var ctx = _context;
        ctx.IASetInputLayout(_terrainLayout);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetIndexBuffer(_terrainIndices, Format.R32_UInt, 0);
        ctx.VSSetShader(_terrainVs);
        ctx.PSSetShader(_terrainPs);
        ctx.RSSetState(Options.Wireframe ? _wireframe : _cullBack);

        foreach (var cell in scene.Terrain)
        {
            if (!_terrain.TryGetValue(cell, out var gpu))
            {
                gpu = TerrainGpu.Create(_device, cell);
                _terrain[cell] = gpu;
            }

            if (!frustum.Intersects(gpu.BoundsCenter, gpu.BoundsRadius)) continue;

            foreach (var pass in gpu.Passes)
            {
                ctx.OMSetBlendState(pass.IsBase ? _opaque : _alphaBlend);
                ctx.OMSetDepthStencilState(pass.IsBase ? _depthWrite : _depthRead);
                var texture = _textures.Get(pass.Texture, out var state) ?? (state == LoadState.Failed ? _textures.Missing : _textures.White);
                ctx.PSSetShaderResources(0, [texture]);
                ctx.IASetVertexBuffers(0, 1, [pass.Vertices], [(uint)TerrainVertex.Size], [0u]);
                ctx.DrawIndexed((uint)_terrainIndexCount, 0, 0);
                Stats.DrawCalls++;
                Stats.Triangles += _terrainIndexCount / 3;
            }
        }

        ctx.OMSetBlendState(_opaque);
    }

    /// <summary>Drops GPU terrain of cells that are no longer in the scene.</summary>
    private readonly Dictionary<TerrainCell, List<GrassPatch>> _grass = new();

    /// <summary>
    /// Grass of the land cells near the camera into the opaque buckets (alpha-tested tufts). Cells get their grass made
    /// two a frame (a few milliseconds each) and keep it until their terrain is rebuilt.
    /// </summary>
    private void CollectGrass(Scene scene, Camera camera, Frustum frustum, Func<Mutagen.Bethesda.Plugins.FormKey, IReadOnlyList<GrassKind>> grassOf)
    {
        var made = 0;
        var reach = scene.GrassDistance;
        var eye = new Vector2(camera.Position.X, camera.Position.Y);
        foreach (var terrain in scene.Terrain)
        {
            var centre = new Vector2(terrain.Origin.X + TerrainCell.CellSize / 2, terrain.Origin.Y + TerrainCell.CellSize / 2);
            if (Vector2.Distance(eye, centre) > reach + TerrainCell.CellSize * 0.71f) continue;
            if (!_grass.TryGetValue(terrain, out var patches))
            {
                if (made >= 2) { HasAnimation = true; continue; }
                patches = GrassField.Generate(terrain, grassOf, scene.GrassSpacing, scene.Water);
                _grass[terrain] = patches;
                made++;
            }
            foreach (var patch in patches)
            {
                if (!frustum.Intersects((patch.Min + patch.Max) / 2, Vector3.Distance(patch.Min, patch.Max) / 2)) continue;
                var model = _meshes.Get(MeshCache.GrassPrefix + patch.Model, out var state);
                if (state != LoadState.Ready || model is null) { if (state != LoadState.Failed) HasAnimation = true; continue; }
                foreach (var shape in model.Shapes)
                {
                    if (!IsShapeVisible(shape.Source, Options) || shape.Material.AlphaBlend) continue;
                    if (!_buckets.TryGetValue(shape, out var bucket)) _buckets[shape] = bucket = [];
                    foreach (var world in patch.Worlds)
                    {
                        if (Vector2.DistanceSquared(eye, new Vector2(world.M41, world.M42)) > reach * reach) continue;
                        bucket.Add(new InstanceData { World = world, Highlight = Vector4.Zero });
                        Stats.Grass++;
                    }
                }
            }
        }
    }

    public void Forget(Scene? keep)
    {
        var alive = keep?.Terrain.ToHashSet() ?? [];
        foreach (var cell in _grass.Keys.Where(c => !alive.Contains(c)).ToList()) _grass.Remove(cell);
        foreach (var cell in _terrain.Keys.Where(c => !alive.Contains(c)).ToList())
        {
            _terrain[cell].Dispose();
            _terrain.Remove(cell);
        }
    }

    private void DrawWater(Scene scene)
    {
        if (scene.Water.Count == 0) return;
        _lines.Clear();

        var vertices = new List<LineVertex>();
        const uint water = 0x8C6E4A26; // ABGR: deep blue-green, ~55% alpha
        foreach (var plane in scene.Water)
        {
            var a = new Vector3(plane.Min.X, plane.Min.Y, plane.Height);
            var b = new Vector3(plane.Max.X, plane.Min.Y, plane.Height);
            var c = new Vector3(plane.Max.X, plane.Max.Y, plane.Height);
            var d = new Vector3(plane.Min.X, plane.Max.Y, plane.Height);
            vertices.AddRange([new(a, water), new(b, water), new(c, water), new(a, water), new(c, water), new(d, water)]);
        }

        var ctx = _context;
        UploadLines(vertices);
        ctx.IASetInputLayout(_lineLayout);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_lineVs);
        ctx.PSSetShader(_linePs);
        ctx.OMSetBlendState(_alphaBlend);
        ctx.OMSetDepthStencilState(_depthRead);
        ctx.RSSetState(_cullNone);
        ctx.IASetVertexBuffers(0, 1, [_lineBuffer], [(uint)sizeof(LineVertex)], [0u]);
        ctx.Draw((uint)vertices.Count, 0);
        ctx.OMSetBlendState(_opaque);
        Stats.DrawCalls++;
    }

    // ------------------------------------------------------------------ navmesh

    private readonly List<LineVertex> _navFill = [];
    private readonly List<LineVertex> _navEdges = [];

    private (Scene? Scene, int Versions, int Count, string Selection) _navCacheKey;

    /// <summary>
    /// Primitives of references (CK shows them with markers): trigger and room boxes as translucent boxes with
    /// their edges, activation spheres as three rings, in the reference's own transform.
    /// </summary>
    private void DrawPrimitives(Scene scene, Frustum frustum)
    {
        var fill = new List<LineVertex>();
        var edges = new List<LineVertex>();
        foreach (var obj in scene.Objects)
        {
            if (obj.Primitive is not { } primitive || obj.IsDeleted || Options.Hides(obj)) continue;
            var reach = primitive.Type == 2 ? primitive.Bounds.X : primitive.Bounds.Length();
            if (!frustum.Intersects(obj.Position, reach * Math.Max(obj.Scale, 0.01f))) continue;
            var line = primitive.Color;
            var tint = (line & 0x00FFFFFF) | 0x22000000u;
            // Rooms, portals, multibounds and occlusion planes enclose whole areas: their edges only.
            var outline = primitive.Type == 3 || obj.BaseEditorId is { } baseId
                && (baseId.Contains("Room", StringComparison.OrdinalIgnoreCase) || baseId.Contains("Portal", StringComparison.OrdinalIgnoreCase)
                    || baseId.Contains("MultiBound", StringComparison.OrdinalIgnoreCase) || baseId.Contains("Occlusion", StringComparison.OrdinalIgnoreCase));
            if (primitive.Type == 2)
            {
                // Sphere: three great circles.
                const int segments = 32;
                var r = primitive.Bounds.X;
                for (var axis = 0; axis < 3; axis++)
                    for (var i = 0; i < segments; i++)
                    {
                        Vector3 Point(int k)
                        {
                            var t = k * MathF.Tau / segments;
                            var (s, c) = (MathF.Sin(t) * r, MathF.Cos(t) * r);
                            return axis switch { 0 => new Vector3(c, s, 0), 1 => new Vector3(c, 0, s), _ => new Vector3(0, c, s) };
                        }
                        edges.Add(new LineVertex(Vector3.Transform(Point(i), obj.World), line));
                        edges.Add(new LineVertex(Vector3.Transform(Point(i + 1), obj.World), line));
                    }
                continue;
            }
            var e = primitive.Bounds;
            // From inside a box its faces would cover the whole view: edges only then.
            if (_camera is { } eye && Matrix4x4.Invert(obj.World, out var toLocal))
            {
                var local = Vector3.Transform(eye.Position, toLocal);
                if (Math.Abs(local.X) <= e.X && Math.Abs(local.Y) <= e.Y && Math.Abs(local.Z) <= e.Z) outline = true;
            }
            Vector3 Corner(int i) => Vector3.Transform(new Vector3((i & 1) != 0 ? e.X : -e.X, (i & 2) != 0 ? e.Y : -e.Y, (i & 4) != 0 ? e.Z : -e.Z), obj.World);
            var corners = Enumerable.Range(0, 8).Select(Corner).ToArray();
            int[][] faces = [[0, 1, 3, 2], [4, 6, 7, 5], [0, 4, 5, 1], [2, 3, 7, 6], [0, 2, 6, 4], [1, 5, 7, 3]];
            foreach (var f in outline ? [] : faces)
            {
                fill.Add(new LineVertex(corners[f[0]], tint)); fill.Add(new LineVertex(corners[f[1]], tint)); fill.Add(new LineVertex(corners[f[2]], tint));
                fill.Add(new LineVertex(corners[f[0]], tint)); fill.Add(new LineVertex(corners[f[2]], tint)); fill.Add(new LineVertex(corners[f[3]], tint));
            }
            int[][] sides = [[0, 1], [1, 3], [3, 2], [2, 0], [4, 5], [5, 7], [7, 6], [6, 4], [0, 4], [1, 5], [2, 6], [3, 7]];
            foreach (var s in sides)
            {
                edges.Add(new LineVertex(corners[s[0]], line));
                edges.Add(new LineVertex(corners[s[1]], line));
            }
        }
        PrimitiveCount = scene.Objects.Count(o => o.Primitive is not null);
        if (fill.Count == 0 && edges.Count == 0) return;

        var ctx = _context;
        ctx.IASetInputLayout(_lineLayout);
        ctx.VSSetShader(_lineVs);
        ctx.PSSetShader(_linePs);
        ctx.RSSetState(_cullNone);
        ctx.OMSetDepthStencilState(_depthRead);
        ctx.OMSetBlendState(_alphaBlend);
        if (fill.Count > 0)
        {
            UploadLines(fill);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.IASetVertexBuffers(0, 1, [_lineBuffer], [(uint)sizeof(LineVertex)], [0u]);
            ctx.Draw((uint)fill.Count, 0);
        }
        if (edges.Count > 0)
        {
            UploadLines(edges);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.LineList);
            ctx.IASetVertexBuffers(0, 1, [_lineBuffer], [(uint)sizeof(LineVertex)], [0u]);
            ctx.Draw((uint)edges.Count, 0);
        }
        ctx.OMSetBlendState(_opaque);
        Stats.DrawCalls += 2;
    }

    /// <summary>References with a primitive in the last frame's scene (autotests).</summary>
    public int PrimitiveCount { get; private set; }

    private void DrawNavmeshes(Scene scene)
    {
        // Rebuild the overlay only when a navmesh or the selection changed.
        var key = (scene, scene.Navmeshes.Sum(n => n.Version + n.Record.GetHashCode() % 1000), scene.Navmeshes.Count,
            NavSelection is { } picked ? $"{picked.Mesh}|{string.Join(',', picked.Vertices)}|{string.Join(',', picked.Triangles)}" : "");
        if (key == _navCacheKey && _navFill.Count > 0)
        {
            SubmitNavmesh();
            return;
        }
        _navCacheKey = key;
        _navUploaded = false;
        _navFill.Clear();
        _navEdges.Clear();
        var lift = new Vector3(0, 0, 3f);

        foreach (var view in scene.Navmeshes)
        {
            if (view.Record.Data is not { } data) continue;
            var vertices = data.Vertices;
            var selected = NavSelection is { } sel && sel.Mesh == view.Key ? sel : null;

            for (var t = 0; t < data.Triangles.Count; t++)
            {
                var tri = data.Triangles[t];
                var v = tri.Vertices;
                if (v.X >= vertices.Count || v.Y >= vertices.Count || v.Z >= vertices.Count) continue;
                var a = P(vertices[v.X]) + lift;
                var b = P(vertices[v.Y]) + lift;
                var c = P(vertices[v.Z]) + lift;

                var flags = tri.Flags;
                uint fill = selected?.Triangles.Contains(t) == true ? 0x90E040E0u
                    : flags.HasFlag(Mutagen.Bethesda.Skyrim.NavmeshTriangle.Flag.Door) ? 0x6030A0FFu
                    : flags.HasFlag(Mutagen.Bethesda.Skyrim.NavmeshTriangle.Flag.Water) ? 0x60FF8030u
                    : flags.HasFlag(Mutagen.Bethesda.Skyrim.NavmeshTriangle.Flag.Preferred) ? 0x6030E0E0u
                    : flags.HasFlag(Mutagen.Bethesda.Skyrim.NavmeshTriangle.Flag.NoLargeCreatures) ? 0x60C040A0u
                    : 0x5040C040u;
                _navFill.Add(new LineVertex(a, fill));
                _navFill.Add(new LineVertex(b, fill));
                _navFill.Add(new LineVertex(c, fill));

                Edge(a, b, tri.EdgeLink_0_1, flags.HasFlag(Mutagen.Bethesda.Skyrim.NavmeshTriangle.Flag.EdgeLink_0_1));
                Edge(b, c, tri.EdgeLink_1_2, flags.HasFlag(Mutagen.Bethesda.Skyrim.NavmeshTriangle.Flag.EdgeLink_1_2));
                Edge(c, a, tri.EdgeLink_2_0, flags.HasFlag(Mutagen.Bethesda.Skyrim.NavmeshTriangle.Flag.EdgeLink_2_0));
            }

            if (selected is not null)
            {
                foreach (var i in selected.Vertices)
                {
                    if (i >= vertices.Count) continue;
                    var p = P(vertices[i]) + lift;
                    const float s = 12f;
                    const uint color = 0xFFFF40FF;
                    _navEdges.Add(new LineVertex(p - new Vector3(s, 0, 0), color));
                    _navEdges.Add(new LineVertex(p + new Vector3(s, 0, 0), color));
                    _navEdges.Add(new LineVertex(p - new Vector3(0, s, 0), color));
                    _navEdges.Add(new LineVertex(p + new Vector3(0, s, 0), color));
                    _navEdges.Add(new LineVertex(p, color));
                    _navEdges.Add(new LineVertex(p + new Vector3(0, 0, s * 2), color));
                }
            }
        }

        void Edge(Vector3 a, Vector3 b, short link, bool external)
        {
            var color = external ? 0xFFFFD040u : link < 0 ? 0xFF3030FFu : 0x90D0D0D0u;
            _navEdges.Add(new LineVertex(a, color));
            _navEdges.Add(new LineVertex(b, color));
        }

        SubmitNavmesh();

        static Vector3 P(Noggog.P3Float p) => new(p.X, p.Y, p.Z);
    }

    private ID3D11Buffer? _navFillBuffer;
    private ID3D11Buffer? _navEdgeBuffer;
    private bool _navUploaded;

    private void SubmitNavmesh()
    {
        if (_navFill.Count == 0) return;
        var ctx = _context;
        if (!_navUploaded)
        {
            // Static until the next change: immutable buffers, no per-frame upload.
            _navFillBuffer?.Dispose();
            _navEdgeBuffer?.Dispose();
            _navFillBuffer = _device.CreateBuffer(CollectionsMarshal.AsSpan(_navFill), BindFlags.VertexBuffer, ResourceUsage.Immutable);
            _navEdgeBuffer = _navEdges.Count > 0 ? _device.CreateBuffer(CollectionsMarshal.AsSpan(_navEdges), BindFlags.VertexBuffer, ResourceUsage.Immutable) : null;
            _navUploaded = true;
        }

        ctx.IASetInputLayout(_lineLayout);
        ctx.VSSetShader(_lineVs);
        ctx.PSSetShader(_linePs);
        ctx.RSSetState(_cullNone);
        ctx.OMSetDepthStencilState(_depthRead);

        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.OMSetBlendState(_alphaBlend);
        ctx.IASetVertexBuffers(0, 1, [_navFillBuffer!], [(uint)sizeof(LineVertex)], [0u]);
        ctx.Draw((uint)_navFill.Count, 0);

        if (_navEdgeBuffer is not null)
        {
            ctx.IASetPrimitiveTopology(PrimitiveTopology.LineList);
            ctx.IASetVertexBuffers(0, 1, [_navEdgeBuffer], [(uint)sizeof(LineVertex)], [0u]);
            ctx.Draw((uint)_navEdges.Count, 0);
        }
        ctx.OMSetBlendState(_opaque);
        Stats.DrawCalls += 2;
    }

    // ------------------------------------------------------------------ lines

    private void DrawLines(Scene scene, IReadOnlySet<SceneObject> selection)
    {
        var lines = new List<LineVertex>(OverlayLines);

        if (Options.ShowLightRadius)
            foreach (var obj in selection)
                if (obj.Light is { } light) Circles(lines, obj.Position, light.Radius, 0xFF40D8FF);

        if (Options.CellBorders && !scene.IsInterior)
        {
            foreach (var cell in scene.Terrain)
            {
                var o = cell.Origin;
                const uint color = 0xFF30C0FF;
                for (var i = 0; i < 32; i++)
                {
                    var z0 = cell.HeightAt(i, 0) + 8;
                    var z1 = cell.HeightAt(i + 1, 0) + 8;
                    lines.Add(new(o + new Vector3(i * TerrainCell.Spacing, 0, z0), color));
                    lines.Add(new(o + new Vector3((i + 1) * TerrainCell.Spacing, 0, z1), color));
                    z0 = cell.HeightAt(0, i) + 8;
                    z1 = cell.HeightAt(0, i + 1) + 8;
                    lines.Add(new(o + new Vector3(0, i * TerrainCell.Spacing, z0), color));
                    lines.Add(new(o + new Vector3(0, (i + 1) * TerrainCell.Spacing, z1), color));
                }
            }
        }

        if (lines.Count == 0) return;

        var ctx = _context;
        UploadLines(lines);
        ctx.IASetInputLayout(_lineLayout);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.LineList);
        ctx.VSSetShader(_lineVs);
        ctx.PSSetShader(_linePs);
        ctx.OMSetBlendState(_opaque);
        ctx.OMSetDepthStencilState(_depthOverlay);
        ctx.RSSetState(_cullNone);
        ctx.IASetVertexBuffers(0, 1, [_lineBuffer], [(uint)sizeof(LineVertex)], [0u]);
        ctx.Draw((uint)lines.Count, 0);
        Stats.DrawCalls++;
    }

    private static void Circles(List<LineVertex> lines, Vector3 center, float radius, uint color)
    {
        const int segments = 48;
        for (var axis = 0; axis < 3; axis++)
        {
            for (var i = 0; i < segments; i++)
            {
                var a0 = i * MathF.Tau / segments;
                var a1 = (i + 1) * MathF.Tau / segments;
                Vector3 P(float a) => axis switch
                {
                    0 => new Vector3(MathF.Cos(a), MathF.Sin(a), 0),
                    1 => new Vector3(MathF.Cos(a), 0, MathF.Sin(a)),
                    _ => new Vector3(0, MathF.Cos(a), MathF.Sin(a)),
                } * radius + center;
                lines.Add(new(P(a0), color));
                lines.Add(new(P(a1), color));
            }
        }
    }

    // ------------------------------------------------------------------ uploads

    private void Upload<T>(ID3D11Buffer buffer, T value) where T : unmanaged
    {
        var mapped = _context.Map(buffer, 0, MapMode.WriteDiscard);
        *(T*)mapped.DataPointer = value;
        _context.Unmap(buffer, 0);
    }

    private void UploadInstances(List<InstanceData> instances)
    {
        if (instances.Count > _instanceCapacity)
        {
            _instanceBuffer.Dispose();
            _instanceCapacity = Math.Max(instances.Count, _instanceCapacity * 2);
            _instanceBuffer = CreateDynamicVertexBuffer(_instanceCapacity * InstanceData.Size);
        }

        var mapped = _context.Map(_instanceBuffer, 0, MapMode.WriteDiscard);
        var span = new Span<InstanceData>((void*)mapped.DataPointer, instances.Count);
        CollectionsMarshal.AsSpan(instances).CopyTo(span);
        _context.Unmap(_instanceBuffer, 0);
    }

    private void UploadLines(List<LineVertex> vertices)
    {
        if (vertices.Count > _lineCapacity)
        {
            _lineBuffer.Dispose();
            _lineCapacity = Math.Max(vertices.Count, _lineCapacity * 2);
            _lineBuffer = CreateDynamicVertexBuffer(_lineCapacity * sizeof(LineVertex));
        }

        var mapped = _context.Map(_lineBuffer, 0, MapMode.WriteDiscard);
        CollectionsMarshal.AsSpan(vertices).CopyTo(new Span<LineVertex>((void*)mapped.DataPointer, vertices.Count));
        _context.Unmap(_lineBuffer, 0);
    }

    public void Dispose()
    {
        foreach (var t in _terrain.Values) t.Dispose();
        foreach (var b in _niBlends.Values) if (b != _alphaBlend) b.Dispose();
        _markerBox.Dispose();
        _actorCapsule.Dispose();
        _meshVs.Dispose(); _meshPs.Dispose(); _meshLayout.Dispose();
        _terrainVs.Dispose(); _terrainPs.Dispose(); _terrainLayout.Dispose();
        _lineVs.Dispose(); _linePs.Dispose(); _lineLayout.Dispose();
        _navFillBuffer?.Dispose(); _navEdgeBuffer?.Dispose();
        _particleVs.Dispose(); _particleLayout.Dispose(); _particleBuffer?.Dispose(); _particleIndices?.Dispose();
        _frameBuffer.Dispose(); _materialBuffer.Dispose(); _instanceBuffer.Dispose(); _lineBuffer.Dispose(); _terrainIndices.Dispose();
        _cullBack.Dispose(); _cullNone.Dispose(); _wireframe.Dispose(); _overlayWire.Dispose();
        _depthWrite.Dispose(); _depthRead.Dispose(); _depthOverlay.Dispose();
        _opaque.Dispose(); _alphaBlend.Dispose(); _sampler.Dispose(); _clampSampler.Dispose();
    }
}

/// <summary>Procedural shapes for markers.</summary>
internal static class BuiltinShapes
{
    public static NifShape Octahedron(float r)
    {
        Vector3[] p = [new(r, 0, 0), new(-r, 0, 0), new(0, r, 0), new(0, -r, 0), new(0, 0, r), new(0, 0, -r)];
        uint[] faces = [0, 2, 4, 2, 1, 4, 1, 3, 4, 3, 0, 4, 2, 0, 5, 1, 2, 5, 3, 1, 5, 0, 3, 5];
        return Flat("marker", p, faces);
    }

    public static NifShape Box(Vector3 min, Vector3 max)
    {
        Vector3[] c =
        [
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z),
        ];
        uint[] faces =
        [
            0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
            1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7,
        ];
        return Flat("box", c, faces);
    }

    /// <summary>Unshared vertices so each face gets its own normal.</summary>
    private static NifShape Flat(string name, Vector3[] points, uint[] faces)
    {
        var positions = new Vector3[faces.Length];
        var normals = new Vector3[faces.Length];
        var indices = new uint[faces.Length];
        for (var i = 0; i < faces.Length; i += 3)
        {
            var a = points[faces[i]];
            var b = points[faces[i + 1]];
            var c = points[faces[i + 2]];
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            positions[i] = a;
            positions[i + 1] = b;
            positions[i + 2] = c;
            normals[i] = normals[i + 1] = normals[i + 2] = n;
            indices[i] = (uint)i;
            indices[i + 1] = (uint)i + 1;
            indices[i + 2] = (uint)i + 2;
        }

        var min = points.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var max = points.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        return new NifShape
        {
            Name = name,
            Positions = positions,
            Normals = normals,
            Indices = indices,
            Material = new NifMaterial(),
            BoundsMin = min,
            BoundsMax = max,
        };
    }
}
