using System.Numerics;

namespace StbEditor.Nif;

/// <summary>How a shape under an NiBillboardNode turns towards the camera.</summary>
public enum BillboardMode
{
    /// <summary>Card plane = screen plane (ALWAYS/RIGID_FACE_CAMERA/CENTER).</summary>
    FaceCamera = 1,

    /// <summary>Turns about its up (Z) axis only (ROTATE_ABOUT_UP, BSROTATE_ABOUT_UP).</summary>
    RotateAboutUp = 2,
}

/// <summary>Shader values an NIF float controller can animate.</summary>
public enum AnimatedValue { UOffset, VOffset, UScale, VScale, Alpha, EmissiveMultiple }

/// <summary>NiFloatData / NiBoolData keys. Quadratic and TBC keys are evaluated linearly.</summary>
public sealed class NifKeys
{
    public required float[] Times { get; init; }
    public required float[] Values { get; init; }

    /// <summary>KeyType: 1 linear, 2 quadratic, 3 TBC, 5 constant (step).</summary>
    public uint Type { get; init; } = 1;

    public bool IsEmpty => Times.Length == 0;

    public float Evaluate(float time)
    {
        var n = Times.Length;
        if (n == 0) return 0;
        if (n == 1 || time <= Times[0]) return Values[0];
        if (time >= Times[n - 1]) return Values[n - 1];

        var hi = Array.BinarySearch(Times, time);
        if (hi >= 0) return Values[hi];
        hi = ~hi;
        var lo = hi - 1;
        if (Type == 5) return Values[lo];

        var span = Times[hi] - Times[lo];
        var t = span > 1e-6f ? (time - Times[lo]) / span : 0;
        return Values[lo] + (Values[hi] - Values[lo]) * t;
    }

    public float Max() => Values.Length == 0 ? 0 : Values.Max();
}

/// <summary>NiTimeController timing: frequency, phase, start/stop and cycle type.</summary>
public readonly record struct NifTiming(float Frequency, float Phase, float Start, float Stop, int Cycle)
{
    public static readonly NifTiming Default = new(1, 0, 0, 0, 0);

    /// <summary>Controller-local time for an absolute time in seconds.</summary>
    public float Local(double seconds)
    {
        var t = (float)(seconds * Frequency + Phase);
        var span = Stop - Start;
        if (span <= 1e-5f) return Start;

        switch (Cycle)
        {
            case 1: // reverse: ping-pong
            {
                var m = (t - Start) % (2 * span);
                if (m < 0) m += 2 * span;
                return Start + (m <= span ? m : 2 * span - m);
            }
            case 2: // clamp
                return Math.Clamp(t, Start, Stop);
            default: // loop
            {
                var m = (t - Start) % span;
                if (m < 0) m += span;
                return Start + m;
            }
        }
    }
}

/// <summary>A BSEffect/BSLightingShaderPropertyFloatController.</summary>
public sealed class NifFloatAnimation
{
    public required AnimatedValue Target { get; init; }
    public required NifTiming Timing { get; init; }
    public required NifKeys Keys { get; init; }

    public float Evaluate(double seconds) => Keys.Evaluate(Timing.Local(seconds));
}

public enum EmitterShape { Point, Box, Sphere, Cylinder, Mesh }

public enum GravityKind { Planar, Spherical }

public readonly record struct NifGravity(GravityKind Kind, Vector3 Axis, Vector3 Origin, float Strength, float Decay, bool WorldAligned);

/// <summary>BSPSysSimpleColorModifier: three colours over the normalised age plus alpha fades.</summary>
public readonly record struct NifSimpleColor(
    float FadeIn, float FadeOut,
    float Color1End, float Color1Start, float Color2End, float Color2Start,
    Vector4 Color0, Vector4 Color1, Vector4 Color2)
{
    public Vector4 Evaluate(float t)
    {
        Vector3 rgb;
        if (t <= Color1End) rgb = Rgb(Color0);
        else if (t < Color1Start) rgb = Vector3.Lerp(Rgb(Color0), Rgb(Color1), (t - Color1End) / Math.Max(Color1Start - Color1End, 1e-4f));
        else if (t <= Color2End) rgb = Rgb(Color1);
        else if (t < Color2Start) rgb = Vector3.Lerp(Rgb(Color1), Rgb(Color2), (t - Color2End) / Math.Max(Color2Start - Color2End, 1e-4f));
        else rgb = Rgb(Color2);

        var alpha = Color1.W;
        if (FadeIn > 0 && t < FadeIn) alpha *= t / FadeIn;
        // Fade out over the last FadeOut part of the life.
        if (FadeOut > 0 && t > 1 - FadeOut) alpha *= Math.Clamp((1 - t) / FadeOut, 0, 1);
        return new Vector4(rgb, alpha);
    }

    private static Vector3 Rgb(Vector4 c) => new(c.X, c.Y, c.Z);
}

/// <summary>NiColorData keys over the normalised particle age.</summary>
public sealed class NifColorKeys
{
    public required float[] Times { get; init; }
    public required Vector4[] Values { get; init; }

    public Vector4 Evaluate(float t)
    {
        var n = Times.Length;
        if (n == 0) return Vector4.One;
        if (n == 1 || t <= Times[0]) return Values[0];
        if (t >= Times[n - 1]) return Values[n - 1];
        var hi = 1;
        while (hi < n - 1 && Times[hi] < t) hi++;
        var lo = hi - 1;
        var span = Times[hi] - Times[lo];
        return Vector4.Lerp(Values[lo], Values[hi], span > 1e-6f ? (t - Times[lo]) / span : 0);
    }
}

/// <summary>
/// Everything needed to simulate one NiParticleSystem in model space: emitter (shape, rates,
/// initial values) and the modifiers editors care about (gravity, grow/fade, colour, scale,
/// rotation, sub-texture atlas). Spawn-on-death, colliders, wind and fields are not simulated.
/// </summary>
public sealed class NifParticleSystem
{
    public required string Name { get; init; }
    public required NifMaterial Material { get; init; }
    public int MaxParticles { get; set; } = 64;
    public bool WorldSpace { get; set; } = true;

    // Emitter: object transform in model space and the volume.
    public EmitterShape Shape { get; set; } = EmitterShape.Point;
    public NifTransform Emitter { get; set; } = NifTransform.Identity;
    public Vector3 Size { get; set; }
    public Vector3[]? EmitPoints { get; set; }

    public float Speed { get; set; }
    public float SpeedVariation { get; set; }
    public float Declination { get; set; }
    public float DeclinationVariation { get; set; }
    public float PlanarAngle { get; set; }
    public float PlanarAngleVariation { get; set; }
    public Vector4 InitialColor { get; set; } = Vector4.One;
    public float Radius { get; set; } = 1;
    public float RadiusVariation { get; set; }
    public float LifeSpan { get; set; } = 1;
    public float LifeSpanVariation { get; set; }

    /// <summary>Particles per second; keyed rate (if any) evaluated over <see cref="EmitTiming"/>.</summary>
    public float BirthRate { get; set; }
    public NifKeys? BirthRateKeys { get; set; }
    public NifKeys? ActiveKeys { get; set; }
    public NifTiming EmitTiming { get; set; } = NifTiming.Default;

    public List<NifGravity> Gravity { get; } = [];
    public float Drag { get; set; }

    public float GrowTime { get; set; }
    public float FadeTime { get; set; }
    public float BaseScale { get; set; } = 1;
    public float[]? Scales { get; set; }

    public NifColorKeys? ColorKeys { get; set; }
    public NifSimpleColor? SimpleColor { get; set; }

    public bool Rotates { get; set; }
    public float RotationSpeed { get; set; }
    public float RotationSpeedVariation { get; set; }
    public float RotationAngle { get; set; }
    public float RotationAngleVariation { get; set; }
    public bool RandomRotationSign { get; set; }

    /// <summary>Atlas cells (u, width, v, height) from NiPSysData.</summary>
    public Vector4[] SubTextures { get; set; } = [];
    public bool AnimatesSubTexture { get; set; }
    public float StartFrame { get; set; }
    public float StartFrameFudge { get; set; }
    public float EndFrame { get; set; }
    public float LoopStartFrame { get; set; }
    public float FrameCount { get; set; }

    /// <summary>Quad width / height (NiPSysData aspect ratio).</summary>
    public float Aspect { get; set; } = 1;

    /// <summary>Quads stretch along the particle's motion (flames, sparks).</summary>
    public bool VelocityOrientation { get; set; }

    /// <summary>Speed-to-aspect: aspect goes from <see cref="Aspect"/> at Speed1 to Aspect2 at Speed2.</summary>
    public bool SpeedToAspect { get; set; }
    public float Aspect2 { get; set; } = 1;
    public float AspectSpeed1 { get; set; }
    public float AspectSpeed2 { get; set; }

    /// <summary>Rough extent of all particles in model space (for culling and framing).</summary>
    public Vector3 BoundsMin { get; set; }
    public Vector3 BoundsMax { get; set; }

    public bool IsEditorMarker { get; set; }
    public bool IsHidden { get; set; }
}
