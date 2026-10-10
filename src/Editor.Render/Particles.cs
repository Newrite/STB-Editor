using System.Numerics;
using System.Runtime.InteropServices;
using StbEditor.Nif;

namespace StbEditor.Render;

/// <summary>One corner of a camera-facing particle quad; expanded in the particle vertex shader.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ParticleVertex
{
    public Vector3 Center;
    public Vector2 Corner;
    public float Size;
    public float Angle;
    public Vector2 UV;
    public uint Color;

    /// <summary>Model-space velocity when the quad aligns with motion, else zero.</summary>
    public Vector3 Velocity;

    /// <summary>Width / height of the quad.</summary>
    public float Aspect;

    public static readonly int Stride = Marshal.SizeOf<ParticleVertex>();
}

/// <summary>
/// CPU simulation of one <see cref="NifParticleSystem"/> in model space. Every placed copy of a
/// model shares it (all torches flicker alike), which keeps the cost per system, not per reference.
/// New systems are pre-warmed so fires and smoke are already full when they come into view.
/// </summary>
public sealed class ParticleRuntime
{
    private struct Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Age;
        public float Life;
        public float Radius;
        public float Angle;
        public float Spin;
        public float Frame;
    }

    private const float Step = 1f / 30f;
    private const float MaxCatchUp = 0.25f;

    private readonly Particle[] _particles;
    private readonly Random _random;
    private int _count;
    private float _emitDebt;
    private double _time = double.NaN;

    public NifParticleSystem Definition { get; }

    /// <summary>Renderer frame this runtime was last advanced and written in.</summary>
    public long LastFrame { get; set; } = -1;

    /// <summary>Where this frame's vertices start in the shared particle buffer.</summary>
    public int BaseVertex { get; set; }

    public int Count => VisibleCount;

    /// <summary>Quads written this frame (live particles that are not fully faded).</summary>
    public int VisibleCount { get; set; }

    public ParticleRuntime(NifParticleSystem definition)
    {
        Definition = definition;
        _particles = new Particle[Math.Clamp(definition.MaxParticles, 1, 2048)];
        _random = new Random(definition.Name.GetHashCode() ^ definition.MaxParticles);
    }

    /// <summary>Simulates up to <paramref name="time"/> (seconds); the first call pre-warms.</summary>
    public void Advance(double time)
    {
        if (double.IsNaN(_time))
        {
            var warm = Math.Min(Definition.LifeSpan + Math.Abs(Definition.LifeSpanVariation), 6f);
            _time = time - warm;
        }

        var elapsed = time - _time;
        if (elapsed < 0)
        {
            // Time went backwards (frozen and resumed): just restart the clock.
            _time = time;
            return;
        }

        // A long pause (window hidden) is not replayed: the system just carries on.
        if (elapsed > 8) _time = time - MaxCatchUp;

        while (time - _time >= Step)
        {
            _time += Step;
            Update(Step, _time);
        }
    }

    private float Rand() => (float)_random.NextDouble();
    private float Signed() => (float)_random.NextDouble() * 2f - 1f;

    private void Update(float dt, double time)
    {
        var def = Definition;

        // Age and move.
        for (var i = 0; i < _count; i++)
        {
            ref var p = ref _particles[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                _particles[i] = _particles[--_count];
                i--;
                continue;
            }

            foreach (var g in def.Gravity)
            {
                var accel = g.Kind == GravityKind.Spherical
                    ? SafeNormalize(g.Origin - p.Position) * g.Strength
                    : g.Axis * g.Strength;
                p.Velocity += accel * dt;
            }

            if (def.Drag > 0) p.Velocity *= MathF.Pow(1f - def.Drag, dt * 30f);
            p.Position += p.Velocity * dt;
            p.Angle += p.Spin * dt;
        }

        // Emit.
        var active = def.ActiveKeys is not { IsEmpty: false } keys || keys.Evaluate(def.EmitTiming.Local(time)) > 0.5f;
        if (!active) return;

        var rate = def.BirthRateKeys is { IsEmpty: false } rateKeys ? rateKeys.Evaluate(def.EmitTiming.Local(time)) : def.BirthRate;
        _emitDebt += Math.Max(rate, 0) * dt;
        while (_emitDebt >= 1f)
        {
            _emitDebt -= 1f;
            if (_count < _particles.Length) Emit(ref _particles[_count++]);
        }
    }

    private void Emit(ref Particle p)
    {
        var def = Definition;
        var local = def.Shape switch
        {
            EmitterShape.Box => new Vector3(Signed() * def.Size.X, Signed() * def.Size.Y, Signed() * def.Size.Z) * 0.5f,
            EmitterShape.Sphere => RandomInSphere() * def.Size.X,
            EmitterShape.Cylinder => Cylinder(def.Size.X, def.Size.Y),
            _ => Vector3.Zero,
        };

        Vector3 position;
        if (def.Shape == EmitterShape.Mesh && def.EmitPoints is { Length: > 0 } points)
            position = points[_random.Next(points.Length)];
        else
            position = def.Emitter.Apply(local);

        // Gamebryo: declination from the emitter's Z axis, planar angle around it.
        var declination = def.Declination + Signed() * def.DeclinationVariation;
        var planar = def.PlanarAngle + Signed() * def.PlanarAngleVariation;
        var direction = new Vector3(MathF.Sin(declination) * MathF.Cos(planar), MathF.Sin(declination) * MathF.Sin(planar), MathF.Cos(declination));
        direction = def.Emitter.Rotation.Transform(direction);
        var speed = def.Speed + Signed() * def.SpeedVariation * 0.5f;

        p.Position = position;
        p.Velocity = direction * speed;
        p.Age = 0;
        p.Life = Math.Max(def.LifeSpan + Signed() * def.LifeSpanVariation * 0.5f, 0.05f);
        p.Radius = Math.Max(def.Radius + Signed() * def.RadiusVariation * 0.5f, 0.01f) * def.Emitter.Scale;
        p.Angle = def.Rotates ? def.RotationAngle + Signed() * def.RotationAngleVariation : 0;
        var spin = def.Rotates ? def.RotationSpeed + Signed() * def.RotationSpeedVariation * 0.5f : 0;
        if (def.RandomRotationSign && _random.Next(2) == 0) spin = -spin;
        p.Spin = spin;
        p.Frame = def.AnimatesSubTexture ? def.StartFrame + Rand() * Math.Max(def.StartFrameFudge, 0)
            : def.SubTextures.Length > 1 ? _random.Next(def.SubTextures.Length) : 0;
    }

    private Vector3 RandomInSphere()
    {
        while (true)
        {
            var v = new Vector3(Signed(), Signed(), Signed());
            if (v.LengthSquared() <= 1f) return v;
        }
    }

    private Vector3 Cylinder(float radius, float height)
    {
        var a = Rand() * MathF.Tau;
        var r = MathF.Sqrt(Rand()) * radius;
        return new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, Signed() * height * 0.5f);
    }

    private static Vector3 SafeNormalize(Vector3 v) => v.LengthSquared() > 1e-8f ? Vector3.Normalize(v) : Vector3.Zero;

    /// <summary>Four corners per live particle into <paramref name="output"/>.</summary>
    public void Write(List<ParticleVertex> output)
    {
        var def = Definition;
        for (var i = 0; i < _count; i++)
        {
            ref var p = ref _particles[i];
            var t = Math.Clamp(p.Age / p.Life, 0f, 1f);

            var size = p.Radius * def.BaseScale;
            if (def.GrowTime > 0 && p.Age < def.GrowTime) size *= p.Age / def.GrowTime;
            if (def.FadeTime > 0 && p.Life - p.Age < def.FadeTime) size *= (p.Life - p.Age) / def.FadeTime;
            if (def.Scales is { Length: > 0 } scales) size *= Sample(scales, t);
            if (size <= 0.001f) continue;

            var color = def.InitialColor;
            if (def.SimpleColor is { } simple) color *= simple.Evaluate(t);
            else if (def.ColorKeys is { } keys) color *= keys.Evaluate(t);
            if (color.W <= 0.002f) continue;

            var uv = new Vector4(0, 1, 0, 1);
            if (def.SubTextures.Length > 0)
            {
                var frame = (int)p.Frame;
                if (def.AnimatesSubTexture && def.FrameCount > 0)
                {
                    frame = (int)(p.Frame + t * def.FrameCount);
                    var end = (int)Math.Max(def.EndFrame, def.StartFrame);
                    var loop = (int)Math.Clamp(def.LoopStartFrame, def.StartFrame, end);
                    if (frame > end) frame = end > loop ? loop + (frame - loop) % (end - loop + 1) : end;
                }
                uv = def.SubTextures[Math.Clamp(frame, 0, def.SubTextures.Length - 1)];
            }

            var aspect = def.Aspect;
            if (def.SpeedToAspect && def.AspectSpeed2 > def.AspectSpeed1)
            {
                var k = Math.Clamp((p.Velocity.Length() - def.AspectSpeed1) / (def.AspectSpeed2 - def.AspectSpeed1), 0f, 1f);
                aspect = def.Aspect + (def.Aspect2 - def.Aspect) * k;
            }
            var velocity = def.VelocityOrientation ? p.Velocity : Vector3.Zero;

            var packed = Pack(color);
            Add(output, p.Position, new Vector2(-1, -1), size, p.Angle, new Vector2(uv.X, uv.Z + uv.W), packed, velocity, aspect);
            Add(output, p.Position, new Vector2(1, -1), size, p.Angle, new Vector2(uv.X + uv.Y, uv.Z + uv.W), packed, velocity, aspect);
            Add(output, p.Position, new Vector2(1, 1), size, p.Angle, new Vector2(uv.X + uv.Y, uv.Z), packed, velocity, aspect);
            Add(output, p.Position, new Vector2(-1, 1), size, p.Angle, new Vector2(uv.X, uv.Z), packed, velocity, aspect);
        }
    }

    private static void Add(List<ParticleVertex> output, Vector3 center, Vector2 corner, float size, float angle, Vector2 uv, uint color, Vector3 velocity, float aspect) =>
        output.Add(new ParticleVertex { Center = center, Corner = corner, Size = size, Angle = angle, UV = uv, Color = color, Velocity = velocity, Aspect = aspect });

    private static float Sample(float[] values, float t)
    {
        var x = t * (values.Length - 1);
        var i = Math.Clamp((int)x, 0, values.Length - 1);
        var j = Math.Min(i + 1, values.Length - 1);
        return values[i] + (values[j] - values[i]) * (x - i);
    }

    private static uint Pack(Vector4 c)
    {
        static uint B(float v) => (uint)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
        return B(c.X) | (B(c.Y) << 8) | (B(c.Z) << 16) | (B(c.W) << 24);
    }
}
