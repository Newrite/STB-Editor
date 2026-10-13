using System.Numerics;

namespace StbEditor.Nif;

public enum ShaderKind { None, Lighting, Effect }

/// <summary>BSLightingShaderProperty shader types (Skyrim).</summary>
public enum LightingShaderType : uint
{
    Default = 0,
    EnvironmentMap = 1,
    GlowShader = 2,
    Parallax = 3,
    FaceTint = 4,
    SkinTint = 5,
    HairTint = 6,
    ParallaxOcc = 7,
    MultitextureLandscape = 8,
    LODLandscape = 9,
    Snow = 10,
    MultiLayerParallax = 11,
    TreeAnim = 12,
    LODObjects = 13,
    SparkleSnow = 14,
    LODObjectsHD = 15,
    EyeEnvmap = 16,
    Cloud = 17,
    LODLandscapeNoise = 18,
    MultitextureLandscapeLODBlend = 19,
    FO4Dismemberment = 20,
}

[Flags]
public enum ShaderFlags1 : uint
{
    Specular = 1u << 0,
    Skinned = 1u << 1,
    VertexAlpha = 1u << 3,
    GreyscaleToPaletteColor = 1u << 4,
    GreyscaleToPaletteAlpha = 1u << 5,
    UseFalloff = 1u << 6,
    EnvironmentMapping = 1u << 7,
    Parallax = 1u << 11,
    ModelSpaceNormals = 1u << 12,
    Landscape = 1u << 14,
    Refraction = 1u << 15,
    EyeEnvironmentMapping = 1u << 17,
    OwnEmit = 1u << 22,
    ProjectedUV = 1u << 23,
    Decal = 1u << 26,
    DynamicDecal = 1u << 27,
    ParallaxOcclusion = 1u << 28,
    ExternalEmittance = 1u << 29,
    ZBufferTest = 1u << 31,
}

[Flags]
public enum ShaderFlags2 : uint
{
    ZBufferWrite = 1u << 0,
    LodLandscape = 1u << 1,
    LodObjects = 1u << 2,
    DoubleSided = 1u << 4,
    VertexColors = 1u << 5,
    GlowMap = 1u << 6,
    TreeAnim = 1u << 29,
    EffectLighting = 1u << 30,
}

/// <summary>Everything the renderer needs from the shader + alpha properties of one shape.</summary>
public sealed class NifMaterial
{
    public ShaderKind Kind { get; set; }
    public LightingShaderType LightingType { get; set; }
    public ShaderFlags1 Flags1 { get; set; }
    public ShaderFlags2 Flags2 { get; set; } = ShaderFlags2.ZBufferWrite;

    /// <summary>Texture set slots: 0 diffuse, 1 normal, 2 glow/tint, 3 height, 4 env cube, 5 env mask, 6 inner/tint mask, 7 specular/backlight.</summary>
    public string[] Textures { get; set; } = [];

    public Vector2 UvOffset { get; set; }
    public Vector2 UvScale { get; set; } = Vector2.One;
    public float Alpha { get; set; } = 1f;
    public float Glossiness { get; set; } = 80f;
    public Vector3 SpecularColor { get; set; } = Vector3.One;
    public float SpecularStrength { get; set; } = 1f;
    public Vector4 EmissiveColor { get; set; }
    public float EmissiveMultiple { get; set; } = 1f;
    public float EnvMapScale { get; set; }
    public Vector3 TintColor { get; set; } = Vector3.One;

    /// <summary>Effect shaders: palette the greyscale source texture is mapped through (fire, magic).</summary>
    public string? GreyscaleTexture { get; set; }

    /// <summary>NiAlphaProperty flags: bit 0 blend, bit 9 test.</summary>
    public ushort AlphaFlags { get; set; }

    public byte AlphaThreshold { get; set; }

    /// <summary>Shader property float controllers (UV scrolling, atlas stepping, alpha pulses).</summary>
    public List<NifFloatAnimation> Animations { get; } = [];

    public bool AlphaBlend => (AlphaFlags & 0x1) != 0;
    public bool AlphaTest => (AlphaFlags & 0x200) != 0;
    public bool DoubleSided => Flags2.HasFlag(ShaderFlags2.DoubleSided);
    public bool HasVertexColors => Flags2.HasFlag(ShaderFlags2.VertexColors);
    public bool ModelSpaceNormals => Flags1.HasFlag(ShaderFlags1.ModelSpaceNormals);
    public bool IsDecal => (Flags1 & (ShaderFlags1.Decal | ShaderFlags1.DynamicDecal)) != 0;

    public string? Diffuse => Textures.Length > 0 && Textures[0].Length > 0 ? Textures[0] : null;
    public string? Normal => Textures.Length > 1 && Textures[1].Length > 0 ? Textures[1] : null;
    public string? Glow => Textures.Length > 2 && Textures[2].Length > 0 ? Textures[2] : null;
    public string? Specular => Textures.Length > 7 && Textures[7].Length > 0 ? Textures[7] : null;
}

/// <summary>One drawable triangle list, already transformed into model space.</summary>
public sealed class NifShape
{
    public required string Name { get; init; }
    public required Vector3[] Positions { get; init; }
    public Vector3[]? Normals { get; init; }
    public Vector3[]? Tangents { get; init; }
    public Vector3[]? Bitangents { get; init; }
    public Vector2[]? UVs { get; init; }

    /// <summary>RGBA8, R in the low byte.</summary>
    public uint[]? Colors { get; init; }

    public required uint[] Indices { get; init; }
    public required NifMaterial Material { get; init; }

    /// <summary>Hidden by its own or a parent's APP_CULLED flag.</summary>
    public bool IsHidden { get; init; }

    /// <summary>Under an "EditorMarker" node: visible in editors, not in the game.</summary>
    public bool IsEditorMarker { get; init; }

    public bool IsSkinned { get; init; }

    /// <summary>
    /// Set under an NiBillboardNode: positions and normals are then in the billboard's own frame
    /// (scaled, not rotated) around <see cref="BillboardOrigin"/>, and the renderer turns them to the camera.
    /// </summary>
    public BillboardMode? Billboard { get; init; }
    public Vector3 BillboardOrigin { get; init; }

    /// <summary>Model-space axis a <see cref="BillboardMode.RotateAboutUp"/> card turns about.</summary>
    public Vector3 BillboardUp { get; init; } = Vector3.UnitZ;

    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }
}

public sealed class NifModel
{
    public required string Path { get; init; }
    public List<NifShape> Shapes { get; } = [];
    public List<NifParticleSystem> Particles { get; } = [];
    public List<string> Warnings { get; } = [];
    public uint Version { get; init; }
    public uint BsVersion { get; init; }

    /// <summary>The file has Havok collision (bhk… blocks): the game collides with it (navmesh generation, physics).</summary>
    public bool HasCollision { get; internal set; }

    public Vector3 BoundsMin { get; internal set; } = new(float.MaxValue);
    public Vector3 BoundsMax { get; internal set; } = new(float.MinValue);

    public bool IsEmpty => Shapes.Count == 0 && Particles.Count == 0;
    public Vector3 Center => (BoundsMin + BoundsMax) * 0.5f;
    public float Radius => IsEmpty ? 0 : Vector3.Distance(BoundsMin, BoundsMax) * 0.5f;
}
