using System.Numerics;

namespace StbEditor.Nif;

/// <summary>
/// Shader controllers (UV scrolling, atlas stepping, alpha) and particle systems
/// (NiParticleSystem + its NiPSys* modifiers and emitter controller).
/// Field layouts follow niftools nif.xml for 20.2.0.7, BS 83 (LE) and 100 (SSE).
/// </summary>
public static partial class NifFile
{
    // ---------------------------------------------------------------- animation

    /// <summary>NiTimeController fields; returns the timing, next controller and flags.</summary>
    private static NifTiming ReadTimeController(ref BlockReader r, out int next, out ushort flags)
    {
        next = r.Int();
        flags = r.UShort();
        var frequency = r.Float();
        var phase = r.Float();
        var start = r.Float();
        var stop = r.Float();
        r.Int(); // target
        return new NifTiming(frequency, phase, start, stop, (flags >> 1) & 3);
    }

    private static void ReadShaderControllers(Context ctx, int index, NifMaterial material)
    {
        for (var guard = 0; guard < 32 && ctx.IsBlock(index); guard++)
        {
            var type = ctx.Header.TypeOf(index);
            var r = new BlockReader(ctx.Block(index));
            if (!type.EndsWith("Controller", StringComparison.Ordinal) || r.Remaining < 26) return;
            var timing = ReadTimeController(ref r, out var next, out var flags);

            var effect = type == "BSEffectShaderPropertyFloatController";
            if ((effect || type == "BSLightingShaderPropertyFloatController") && (flags & 0x8) != 0 && r.Remaining >= 8)
            {
                var interpolator = r.Int();
                var variable = r.UInt();
                AnimatedValue? target = effect
                    ? variable switch
                    {
                        0 => AnimatedValue.EmissiveMultiple,
                        5 => AnimatedValue.Alpha,
                        6 => AnimatedValue.UOffset,
                        7 => AnimatedValue.UScale,
                        8 => AnimatedValue.VOffset,
                        9 => AnimatedValue.VScale,
                        _ => null,
                    }
                    : variable switch
                    {
                        11 => AnimatedValue.EmissiveMultiple,
                        12 => AnimatedValue.Alpha,
                        20 => AnimatedValue.UOffset,
                        21 => AnimatedValue.UScale,
                        22 => AnimatedValue.VOffset,
                        23 => AnimatedValue.VScale,
                        _ => null,
                    };

                if (target is { } t && ReadFloatInterpolator(ctx, interpolator) is { IsEmpty: false } keys)
                    material.Animations.Add(new NifFloatAnimation { Target = t, Timing = timing, Keys = keys });
            }

            index = next;
        }
    }

    /// <summary>NiFloatInterpolator keys (or its pose value as one key); blend interpolators give their pose value.</summary>
    private static NifKeys? ReadFloatInterpolator(Context ctx, int index)
    {
        if (!ctx.IsBlock(index)) return null;
        var type = ctx.Header.TypeOf(index);
        var block = ctx.Block(index);

        if (type == "NiFloatInterpolator")
        {
            var r = new BlockReader(block);
            var value = r.Float();
            var data = r.Int();
            if (ctx.IsBlock(data) && ctx.Header.TypeOf(data) == "NiFloatData" && ReadKeys(ctx, data, 4) is { IsEmpty: false } keys) return keys;
            return Constant(value);
        }

        if (type == "NiBlendFloatInterpolator" && block.Length >= 4)
            return Constant(new BlockReader(block[^4..]).Float());

        return null;
    }

    private static NifKeys? ReadBoolInterpolator(Context ctx, int index)
    {
        if (!ctx.IsBlock(index) || ctx.Header.TypeOf(index) != "NiBoolInterpolator") return null;
        var r = new BlockReader(ctx.Block(index));
        var value = r.Byte();
        var data = r.Int();
        if (ctx.IsBlock(data) && ctx.Header.TypeOf(data) == "NiBoolData" && ReadKeys(ctx, data, 1) is { IsEmpty: false } keys) return keys;
        return value <= 1 ? Constant(value) : null;
    }

    private static NifKeys? Constant(float value) =>
        float.IsFinite(value) && Math.Abs(value) < 1e30f ? new NifKeys { Times = [0f], Values = [value] } : null;

    /// <summary>KeyGroup of floats (valueSize 4) or bytes (1).</summary>
    private static NifKeys ReadKeys(Context ctx, int index, int valueSize)
    {
        var r = new BlockReader(ctx.Block(index));
        var count = r.Int();
        if (count <= 0) return new NifKeys { Times = [], Values = [] };
        if (count > 100_000) throw new InvalidDataException("bad key count");
        var interpolation = r.UInt();

        var times = new float[count];
        var values = new float[count];
        for (var i = 0; i < count; i++)
        {
            times[i] = r.Float();
            values[i] = valueSize == 4 ? r.Float() : r.Byte();
            if (interpolation == 2) r.Skip(valueSize * 2);
            else if (interpolation == 3) r.Skip(12);
        }

        // Keys are stored in time order; guard against the odd broken file anyway.
        for (var i = 1; i < count; i++)
        {
            if (times[i] >= times[i - 1]) continue;
            Array.Sort(times, values);
            break;
        }

        return new NifKeys { Times = times, Values = values, Type = interpolation };
    }

    // ---------------------------------------------------------------- particles

    private static void ReadParticleSystem(Context ctx, int index, NifTransform parent, bool hidden, bool editorMarker)
    {
        var r = new BlockReader(ctx.Block(index));
        var av = ReadAvObject(ctx, ref r);
        var world = parent.Then(av.Transform);

        int shaderRef, alphaRef, dataRef;
        if (ctx.Header.BsVersion >= 100)
        {
            r.Skip(16); // bounding sphere
            r.Int(); // skin
            shaderRef = r.Int();
            alphaRef = r.Int();
            r.Skip(8); // vertex desc
            r.Skip(8); // far/near begin/end
            dataRef = r.Int();
        }
        else
        {
            dataRef = r.Int();
            r.Int(); // skin instance
            var materials = r.Int();
            r.Skip(materials * 8);
            r.Int();
            r.Byte();
            shaderRef = r.Int();
            alphaRef = r.Int();
            r.Skip(8); // far/near begin/end
        }

        var worldSpace = r.Bool();
        var modifiers = r.Refs();

        var material = new NifMaterial();
        if (ctx.IsBlock(shaderRef)) ReadShader(ctx, shaderRef, material);
        if (ctx.IsBlock(alphaRef) && ctx.Header.TypeOf(alphaRef) == "NiAlphaProperty") ReadAlpha(ctx, alphaRef, material);

        var ps = new NifParticleSystem
        {
            Name = av.Name ?? $"particles{index}",
            Material = material,
            WorldSpace = worldSpace,
            Emitter = world,
            IsHidden = hidden || av.Hidden,
            IsEditorMarker = editorMarker,
        };

        if (ctx.IsBlock(dataRef)) ReadParticleData(ctx, dataRef, ps);
        foreach (var modifier in modifiers)
        {
            if (!ctx.IsBlock(modifier)) continue;
            try
            {
                ReadModifier(ctx, modifier, ps, world);
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
            {
                ctx.Model.Warnings.Add($"block {modifier} {ctx.Header.TypeOf(modifier)}: {ex.Message}");
            }
        }
        ReadParticleControllers(ctx, av.Controller, ps);

        if (ps.BirthRate <= 0 && ps.BirthRateKeys is null)
            ps.BirthRate = ps.MaxParticles / Math.Max(ps.LifeSpan, 0.1f) * 0.5f;

        EstimateBounds(ps);
        ctx.Model.Particles.Add(ps);
    }

    /// <summary>NiPSysData (Bethesda 20.2): max particle count and the sub-texture atlas.</summary>
    private static void ReadParticleData(Context ctx, int index, NifParticleSystem ps)
    {
        var r = new BlockReader(ctx.Block(index));
        r.Int(); // group id
        ps.MaxParticles = Math.Clamp((int)r.UShort(), 1, 2048);
        r.Skip(2); // keep, compress
        r.Bool(); // has vertices
        r.UShort(); // BS data flags
        r.UInt(); // material CRC
        r.Bool(); // has normals
        r.Skip(16); // bound
        r.Bool(); // has vertex colors
        r.UShort(); // consistency
        r.Int(); // additional data

        r.Bool(); // has radii
        r.UShort(); // active count
        r.Bool(); // has sizes
        r.Bool(); // has rotations
        r.Bool(); // has rotation angles
        r.Bool(); // has rotation axes
        r.Bool(); // has texture indices
        var count = r.Int();
        if (count is > 0 and <= 256 && r.Remaining >= count * 16)
        {
            var cells = new Vector4[count];
            for (var i = 0; i < count; i++) cells[i] = r.Vec4();
            ps.SubTextures = cells;
        }
        else if (count > 256)
        {
            return;
        }
        else
        {
            r.Skip(Math.Max(count, 0) * 16);
        }

        if (r.Remaining < 18) return;
        var aspect = r.Float();
        var aspectFlags = r.UShort();
        ps.Aspect = aspect is > 0.01f and < 100f ? aspect : 1f;
        ps.VelocityOrientation = (aspectFlags & 0x1) != 0;
        ps.SpeedToAspect = (aspectFlags & 0x100) != 0;
        ps.Aspect2 = r.Float();
        ps.AspectSpeed1 = r.Float();
        ps.AspectSpeed2 = r.Float();
        if (ps.Aspect2 is <= 0.01f or >= 100f) ps.Aspect2 = ps.Aspect;
    }

    private static NifTransform NodeOr(Context ctx, int index, NifTransform fallback) =>
        ctx.NodeWorld.TryGetValue(index, out var t) ? t : fallback;

    private static void ReadModifier(Context ctx, int index, NifParticleSystem ps, NifTransform system)
    {
        var type = ctx.Header.TypeOf(index);
        var r = new BlockReader(ctx.Block(index));
        ctx.String(r.Int()); // name
        r.UInt(); // order
        r.Int(); // target
        if (!r.Bool()) return; // inactive

        switch (type)
        {
            case "NiPSysBoxEmitter":
            case "NiPSysSphereEmitter":
            case "NiPSysCylinderEmitter":
            case "BSPSysArrayEmitter":
            case "NiPSysMeshEmitter":
            {
                ps.Speed = r.Float();
                ps.SpeedVariation = r.Float();
                ps.Declination = r.Float();
                ps.DeclinationVariation = r.Float();
                ps.PlanarAngle = r.Float();
                ps.PlanarAngleVariation = r.Float();
                ps.InitialColor = r.Vec4();
                ps.Radius = r.Float();
                ps.RadiusVariation = r.Float();
                ps.LifeSpan = r.Float();
                ps.LifeSpanVariation = r.Float();

                if (type == "NiPSysMeshEmitter")
                {
                    // Emit from the vertices of the listed shapes (already in model space).
                    var meshes = r.Refs();
                    var points = new List<Vector3>();
                    foreach (var mesh in meshes)
                        if (ctx.ShapeByBlock.TryGetValue(mesh, out var shape) && shape.Billboard is null)
                            points.AddRange(shape.Positions.Take(4096));
                    if (points.Count > 0)
                    {
                        ps.Shape = EmitterShape.Mesh;
                        ps.EmitPoints = [.. points];
                        ps.Emitter = NifTransform.Identity with { Rotation = system.Rotation };
                    }
                    else
                    {
                        ps.Shape = EmitterShape.Point;
                        ps.Emitter = system;
                    }
                    break;
                }

                ps.Emitter = NodeOr(ctx, r.Int(), system);
                switch (type)
                {
                    case "NiPSysBoxEmitter":
                        ps.Shape = EmitterShape.Box;
                        ps.Size = r.Vec3();
                        break;
                    case "NiPSysSphereEmitter":
                        ps.Shape = EmitterShape.Sphere;
                        ps.Size = new Vector3(r.Float(), 0, 0);
                        break;
                    case "NiPSysCylinderEmitter":
                        ps.Shape = EmitterShape.Cylinder;
                        ps.Size = new Vector3(r.Float(), r.Float(), 0);
                        break;
                    default:
                        ps.Shape = EmitterShape.Point;
                        break;
                }
                break;
            }
            case "NiPSysGravityModifier":
            {
                var obj = NodeOr(ctx, r.Int(), system);
                var axis = r.Vec3();
                var decay = r.Float();
                var strength = r.Float();
                var force = r.UInt();
                r.Float(); // turbulence
                r.Float(); // turbulence scale
                var worldAligned = r.Remaining >= 1 && r.Bool();
                var direction = worldAligned ? axis : obj.Rotation.Transform(axis);
                if (direction.LengthSquared() > 1e-8f) direction = Vector3.Normalize(direction);
                ps.Gravity.Add(new NifGravity(force == 1 ? GravityKind.Spherical : GravityKind.Planar, direction, obj.Translation, strength, decay, worldAligned));
                break;
            }
            case "NiPSysGrowFadeModifier":
                ps.GrowTime = r.Float();
                r.UShort();
                ps.FadeTime = r.Float();
                r.UShort();
                if (r.Remaining >= 4) ps.BaseScale = r.Float();
                break;
            case "NiPSysColorModifier":
            {
                var data = r.Int();
                if (ctx.IsBlock(data) && ctx.Header.TypeOf(data) == "NiColorData") ps.ColorKeys = ReadColorKeys(ctx, data);
                break;
            }
            case "BSPSysSimpleColorModifier":
                ps.SimpleColor = new NifSimpleColor(r.Float(), r.Float(), r.Float(), r.Float(), r.Float(), r.Float(), r.Vec4(), r.Vec4(), r.Vec4());
                break;
            case "BSPSysScaleModifier":
            {
                var count = r.Int();
                if (count is > 0 and < 4096)
                {
                    var scales = new float[count];
                    for (var i = 0; i < count; i++) scales[i] = r.Float();
                    ps.Scales = scales;
                }
                break;
            }
            case "NiPSysRotationModifier":
                ps.Rotates = true;
                ps.RotationSpeed = r.Float();
                ps.RotationSpeedVariation = r.Float();
                ps.RotationAngle = r.Float();
                ps.RotationAngleVariation = r.Float();
                ps.RandomRotationSign = r.Bool();
                break;
            case "BSPSysSubTexModifier":
                ps.AnimatesSubTexture = true;
                ps.StartFrame = r.Float();
                ps.StartFrameFudge = r.Float();
                ps.EndFrame = r.Float();
                ps.LoopStartFrame = r.Float();
                r.Float(); // loop start fudge
                ps.FrameCount = r.Float();
                break;
            case "NiPSysDragModifier":
                r.Int();
                r.Vec3();
                ps.Drag = Math.Clamp(r.Float(), 0, 1);
                break;
        }
    }

    private static NifColorKeys ReadColorKeys(Context ctx, int index)
    {
        var r = new BlockReader(ctx.Block(index));
        var count = r.Int();
        if (count <= 0 || count > 10_000) return new NifColorKeys { Times = [], Values = [] };
        var interpolation = r.UInt();
        var times = new float[count];
        var values = new Vector4[count];
        for (var i = 0; i < count; i++)
        {
            times[i] = r.Float();
            values[i] = r.Vec4();
            if (interpolation == 2) r.Skip(32);
            else if (interpolation == 3) r.Skip(12);
        }
        return new NifColorKeys { Times = times, Values = values };
    }

    private static void ReadParticleControllers(Context ctx, int index, NifParticleSystem ps)
    {
        for (var guard = 0; guard < 32 && ctx.IsBlock(index); guard++)
        {
            var type = ctx.Header.TypeOf(index);
            var r = new BlockReader(ctx.Block(index));
            if (r.Remaining < 26) return;
            var timing = ReadTimeController(ref r, out var next, out _);

            if (type is "NiPSysEmitterCtlr" or "BSPSysMultiTargetEmitterCtlr" && r.Remaining >= 12)
            {
                var interpolator = r.Int();
                ctx.String(r.Int()); // modifier name
                var visibility = r.Int();
                ps.EmitTiming = timing;
                if (ReadFloatInterpolator(ctx, interpolator) is { IsEmpty: false } rate)
                {
                    if (rate.Times.Length == 1) ps.BirthRate = rate.Values[0];
                    else ps.BirthRateKeys = rate;
                }
                ps.ActiveKeys = ReadBoolInterpolator(ctx, visibility);
            }

            index = next;
        }
    }

    /// <summary>Emitter volume plus how far a particle can travel in its life.</summary>
    private static void EstimateBounds(NifParticleSystem ps)
    {
        var life = ps.LifeSpan + Math.Abs(ps.LifeSpanVariation) * 0.5f;
        var speed = Math.Abs(ps.Speed) + Math.Abs(ps.SpeedVariation) * 0.5f;
        var accel = ps.Gravity.Sum(g => Math.Abs(g.Strength));
        var maxScale = (ps.Scales is { Length: > 0 } s ? s.Max() : 1f) * Math.Max(ps.BaseScale, 0.01f);
        var size = (Math.Abs(ps.Radius) + Math.Abs(ps.RadiusVariation)) * maxScale * ps.Emitter.Scale;
        var center = ps.Emitter.Translation;
        var volume = Math.Max(Math.Max(ps.Size.X, ps.Size.Y), ps.Size.Z) * ps.Emitter.Scale;
        if (ps.Shape == EmitterShape.Mesh && ps.EmitPoints is { Length: > 0 } points)
        {
            var min = points.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var max = points.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            center = (min + max) * 0.5f;
            volume = Vector3.Distance(min, max) * 0.5f;
        }

        var reach = Math.Clamp(speed * life + 0.5f * accel * life * life + size + volume, 8f, 3000f);
        ps.BoundsMin = center - new Vector3(reach);
        ps.BoundsMax = center + new Vector3(reach);
    }
}
