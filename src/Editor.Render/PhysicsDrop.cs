using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;

namespace StbEditor.Render;

/// <summary>Where a simulated object came to rest.</summary>
public sealed record DropPose(SceneObject Object, Vector3 Position, Vector3 Rotation, bool Settled);

/// <summary>What a simulation took: ground pieces, triangles, simulated seconds, wall-clock milliseconds to build and to run.</summary>
public sealed record DropStats(int Statics, int Triangles, float SimulatedSeconds, long BuildMilliseconds, long RunMilliseconds)
{
    /// <summary>Moving objects that fell as their own Havok collision (the rest as their render mesh's convex hull).</summary>
    public int Compounds { get; init; }
}

/// <summary>
/// Run Havok Sim (CK) with BepuPhysics: the given objects fall as rigid bodies (the convex hull of their mesh) onto
/// everything else around them — the meshes of the other objects and the terrain, held still — until they come to rest.
/// Works in metres (70 game units each), as the solver's tolerances expect.
/// </summary>
public static class PhysicsDrop
{
    private const float UnitsPerMetre = 70f;

    /// <param name="moving">Objects to simulate; everything else in reach is ground.</param>
    /// <param name="seconds">Longest simulated time.</param>
    public static List<DropPose> Simulate(Scene scene, IReadOnlyCollection<SceneObject> moving, RenderOptions options, float seconds = 8f, CancellationToken cancel = default) =>
        Simulate(scene, moving, options, out _, seconds, cancel);

    public static List<DropPose> Simulate(Scene scene, IReadOnlyCollection<SceneObject> moving, RenderOptions options, out DropStats stats, float seconds = 8f, CancellationToken cancel = default)
    {
        var result = new List<DropPose>();
        stats = new DropStats(0, 0, 0, 0, 0);
        if (moving.Count == 0) return result;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var staticCount = 0;
        var triangleCount = 0;

        var pool = new BufferPool();
        var apart = new HashSet<(int, int)>();
        var simulation = Simulation.Create(pool, new Contacts(apart), new Gravity(new Vector3(0, 0, -9.81f)), new SolveDescription(8, 1));
        try
        {
            // What can be landed on: objects and terrain near the moving ones (wide around, deep below).
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var o in moving)
            {
                min = Vector3.Min(min, o.BoundsCenter - new Vector3(o.BoundsRadius));
                max = Vector3.Max(max, o.BoundsCenter + new Vector3(o.BoundsRadius));
            }
            // Sideways a little beyond them (what slides off an edge lands close by), far down.
            min -= new Vector3(300, 300, 4000);
            max += new Vector3(300, 300, 200);
            var movingSet = moving.ToHashSet();
            // Small things that start inside a moving piece are its contents (garlic in a pot, food on a plate): they are
            // not ground for it, or they push it away; they stay where they are.
            var holders = new List<(Vector3 Min, Vector3 Max)>();
            foreach (var o in moving)
            {
                if (o.Model is not { Shapes.Count: > 0 } held) continue;
                var lo = new Vector3(float.MaxValue);
                var hi = new Vector3(float.MinValue);
                foreach (var shape in held.Shapes)
                    foreach (var p in shape.Source.Positions)
                    {
                        var w = Vector3.Transform(p, o.World);
                        lo = Vector3.Min(lo, w);
                        hi = Vector3.Max(hi, w);
                    }
                holders.Add((lo - new Vector3(2), hi + new Vector3(2)));
            }
            bool Contents(SceneObject obj) => obj.BoundsRadius < 60 && holders.Any(h =>
                obj.BoundsCenter.X >= h.Min.X && obj.BoundsCenter.X <= h.Max.X && obj.BoundsCenter.Y >= h.Min.Y && obj.BoundsCenter.Y <= h.Max.Y
                && obj.BoundsCenter.Z >= h.Min.Z && obj.BoundsCenter.Z <= h.Max.Z);
            // One mesh per model and scale: a room repeats its pieces.
            var meshes = new Dictionary<(GpuModel, float), TypedIndex?>();

            foreach (var obj in scene.Objects)
            {
                cancel.ThrowIfCancellationRequested();
                if (movingSet.Contains(obj) || obj.IsDeleted || options.Hides(obj) || obj.Kind != SceneObjectKind.Mesh || obj.Model is not { Shapes.Count: > 0 } model) continue;
                if (!Overlaps(obj.BoundsCenter, obj.BoundsRadius, min, max) || Contents(obj)) continue;
                var scale = obj.Transform.Scale / UnitsPerMetre;
                if (!meshes.TryGetValue((model, scale), out var shape))
                {
                    var triangles = Triangles(model, options, scale);
                    shape = null;
                    if (triangles.Count > 0)
                    {
                        pool.Take<Triangle>(triangles.Count, out var buffer);
                        for (var i = 0; i < triangles.Count; i++) buffer[i] = triangles[i];
                        shape = simulation.Shapes.Add(new Mesh(buffer, Vector3.One, pool));
                        triangleCount += triangles.Count;
                    }
                    meshes[(model, scale)] = shape;
                }
                if (shape is not { } index) continue;
                simulation.Statics.Add(new StaticDescription(obj.Position / UnitsPerMetre, Orientation(obj), index));
                staticCount++;
            }

            foreach (var cell in scene.Terrain)
            {
                var origin = cell.Origin;
                var size = (TerrainCell.Size - 1) * TerrainCell.Spacing;
                if (origin.X > max.X || origin.Y > max.Y || origin.X + size < min.X || origin.Y + size < min.Y) continue;
                var count = (TerrainCell.Size - 1) * (TerrainCell.Size - 1) * 2;
                pool.Take<Triangle>(count, out var buffer);
                var t = 0;
                for (var y = 0; y < TerrainCell.Size - 1; y++)
                    for (var x = 0; x < TerrainCell.Size - 1; x++)
                    {
                        Vector3 P(int px, int py) => (origin + new Vector3(px * TerrainCell.Spacing, py * TerrainCell.Spacing, cell.HeightAt(px, py))) / UnitsPerMetre;
                        // Clockwise seen from above: Bepu's triangles collide from the side that sees them clockwise.
                        buffer[t++] = new Triangle(P(x, y), P(x + 1, y + 1), P(x + 1, y));
                        buffer[t++] = new Triangle(P(x, y), P(x, y + 1), P(x + 1, y + 1));
                    }
                simulation.Statics.Add(new StaticDescription(Vector3.Zero, Quaternion.Identity, simulation.Shapes.Add(new Mesh(buffer, Vector3.One, pool))));
                staticCount++;
                triangleCount += count;
            }

            var bodies = new List<(SceneObject Object, BodyHandle Handle, Vector3 Center)>();
            var compounds = 0;
            var boxes = new List<(Vector3 Min, Vector3 Max)>();
            foreach (var obj in moving)
            {
                if (obj.Model is not { Shapes.Count: > 0 } model) continue;
                var points = new List<Vector3>();
                foreach (var shape in model.Shapes)
                    if (SceneRenderer.IsShapeVisible(shape.Source, options))
                        foreach (var p in shape.Source.Positions) points.Add(p * (obj.Transform.Scale / UnitsPerMetre));
                if (points.Count < 4) continue;
                TypedIndex shapeIndex;
                BodyInertia inertia;
                Vector3 center;
                if (model.CollisionPieces is { Count: > 0 } pieces)
                {
                    // The mesh's own Havok collision: its convex pieces as one compound body (bowls and pots hold things).
                    using var builder = new CompoundBuilder(pool, simulation.Shapes, pieces.Count);
                    foreach (var piece in pieces)
                    {
                        var scaled = piece.Select(p => p * (obj.Transform.Scale / UnitsPerMetre)).ToArray();
                        var pieceHull = new ConvexHull(scaled.AsSpan(), pool, out var pieceCenter);
                        var size = scaled.Aggregate(new Vector3(float.MinValue), Vector3.Max) - scaled.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
                        builder.Add(pieceHull, new RigidPose(pieceCenter), MathF.Max(size.X * size.Y * size.Z, 1e-6f));
                    }
                    builder.BuildDynamicCompound(out var children, out inertia, out center);
                    // Mass 1 like the hull bodies: only the shape and its balance matter here.
                    inertia = Scale(inertia, 1f);
                    shapeIndex = simulation.Shapes.Add(new Compound(children));
                    compounds++;
                }
                else
                {
                    var hull = new ConvexHull(points.ToArray().AsSpan(), pool, out center);
                    inertia = hull.ComputeInertia(1f);
                    shapeIndex = simulation.Shapes.Add(hull);
                }
                var orientation = Orientation(obj);
                var position = obj.Position / UnitsPerMetre + Vector3.Transform(center, orientation);
                var boxMin = new Vector3(float.MaxValue);
                var boxMax = new Vector3(float.MinValue);
                foreach (var p in points)
                {
                    var w = obj.Position / UnitsPerMetre + Vector3.Transform(p, orientation);
                    boxMin = Vector3.Min(boxMin, w);
                    boxMax = Vector3.Max(boxMax, w);
                }
                boxes.Add((boxMin, boxMax));
                var handle = simulation.Bodies.Add(BodyDescription.CreateDynamic(new RigidPose(position, orientation), inertia,
                    new CollidableDescription(shapeIndex, 0.1f), new BodyActivityDescription(0.01f)));
                bodies.Add((obj, handle, center));
            }
            // Pieces that start inside each other (a plate in a pot: the pot's convex hull is solid, two plates in one
            // place) do not push each other — they would fly apart or hang in the air. Pieces that only touch (a stack)
            // still do.
            for (var i = 0; i < bodies.Count; i++)
                for (var j = i + 1; j < bodies.Count; j++)
                {
                    var overlap = Vector3.Max(Vector3.Zero, Vector3.Min(boxes[i].Max, boxes[j].Max) - Vector3.Max(boxes[i].Min, boxes[j].Min));
                    var shared = overlap.X * overlap.Y * overlap.Z;
                    static float Volume((Vector3 Min, Vector3 Max) b) { var d = b.Max - b.Min; return MathF.Max(d.X * d.Y * d.Z, 1e-9f); }
                    if (shared > 0.25f * MathF.Min(Volume(boxes[i]), Volume(boxes[j])))
                        apart.Add((Math.Min(bodies[i].Handle.Value, bodies[j].Handle.Value), Math.Max(bodies[i].Handle.Value, bodies[j].Handle.Value)));
                }

            var built = clock.ElapsedMilliseconds;
            const float step = 1f / 60f;
            var settled = false;
            var time = 0f;
            for (; time < seconds; time += step)
            {
                cancel.ThrowIfCancellationRequested();
                simulation.Timestep(step);
                if (bodies.All(b => !simulation.Bodies.GetBodyReference(b.Handle).Awake)) { settled = true; break; }
            }
            stats = new DropStats(staticCount, triangleCount, time, built, clock.ElapsedMilliseconds - built) { Compounds = compounds };

            foreach (var (obj, handle, center) in bodies)
            {
                var pose = simulation.Bodies.GetBodyReference(handle).Pose;
                var position = (pose.Position - Vector3.Transform(center, pose.Orientation)) * UnitsPerMetre;
                var world = Matrix4x4.CreateFromQuaternion(pose.Orientation) * Matrix4x4.CreateTranslation(position);
                var (_, rotation, _) = TransformMath.Decompose(world);
                result.Add(new DropPose(obj, position, rotation, settled || !simulation.Bodies.GetBodyReference(handle).Awake));
            }
        }
        finally
        {
            simulation.Dispose();
            pool.Clear();
        }
        return result;
    }

    /// <summary>An inertia for a total mass of <paramref name="mass"/> (the builder's is for the sum of the pieces' weights).</summary>
    private static BodyInertia Scale(BodyInertia inertia, float mass)
    {
        if (inertia.InverseMass <= 0) return inertia;
        var factor = inertia.InverseMass * mass; // old mass → new mass
        var t = inertia.InverseInertiaTensor;
        return new BodyInertia
        {
            InverseMass = 1f / mass,
            InverseInertiaTensor = new Symmetric3x3 { XX = t.XX / factor, YX = t.YX / factor, YY = t.YY / factor, ZX = t.ZX / factor, ZY = t.ZY / factor, ZZ = t.ZZ / factor },
        };
    }

    /// <summary>The object's rotation as a quaternion (its world matrix without scale and translation).</summary>
    private static Quaternion Orientation(SceneObject obj)
    {
        var m = obj.World;
        var scale = obj.Transform.Scale == 0 ? 1f : obj.Transform.Scale;
        var r = new Matrix4x4(m.M11 / scale, m.M12 / scale, m.M13 / scale, 0, m.M21 / scale, m.M22 / scale, m.M23 / scale, 0, m.M31 / scale, m.M32 / scale, m.M33 / scale, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(r));
    }

    /// <summary>
    /// The model's visible triangles (model space, scaled), turned clockwise as Bepu wants them seen (NIF fronts are
    /// counterclockwise); double-sided materials both ways.
    /// </summary>
    private static List<Triangle> Triangles(GpuModel model, RenderOptions options, float scale)
    {
        var result = new List<Triangle>();
        foreach (var shape in model.Shapes)
        {
            var source = shape.Source;
            if (!SceneRenderer.IsShapeVisible(source, options) || source.Billboard is not null || source.Material.AlphaBlend) continue;
            var p = source.Positions;
            var idx = source.Indices;
            for (var i = 0; i + 2 < idx.Length; i += 3)
            {
                Vector3 a = p[idx[i]] * scale, b = p[idx[i + 1]] * scale, c = p[idx[i + 2]] * scale;
                if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-12f) continue;
                result.Add(new Triangle(a, c, b));
                if (source.Material.DoubleSided) result.Add(new Triangle(a, b, c));
            }
        }
        return result;
    }

    private static bool Overlaps(Vector3 center, float radius, Vector3 min, Vector3 max) =>
        center.X + radius >= min.X && center.X - radius <= max.X && center.Y + radius >= min.Y && center.Y - radius <= max.Y
        && center.Z + radius >= min.Z && center.Z - radius <= max.Z;

    // ------------------------------------------------------------------ Bepu callbacks

    private struct Contacts(HashSet<(int, int)> apart) : INarrowPhaseCallbacks
    {
        public void Initialize(Simulation simulation) { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
        {
            if (a.Mobility != CollidableMobility.Dynamic && b.Mobility != CollidableMobility.Dynamic) return false;
            if (a.Mobility == CollidableMobility.Dynamic && b.Mobility == CollidableMobility.Dynamic && apart.Count > 0)
            {
                int x = a.BodyHandle.Value, y = b.BodyHandle.Value;
                if (apart.Contains((Math.Min(x, y), Math.Max(x, y)))) return false;
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            // Gentle recovery (0.5 m/s): pieces that start inside each other come apart instead of jumping.
            pairMaterial = new PairMaterialProperties(0.8f, 0.5f, new SpringSettings(30, 1));
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;

        public void Dispose() { }
    }

    private struct Gravity(Vector3 gravity) : IPoseIntegratorCallbacks
    {
        private Vector3Wide _gravityDt;
        private Vector<float> _damping;

        public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
        public readonly bool AllowSubstepsForUnconstrainedBodies => false;
        public readonly bool IntegrateVelocityForKinematics => false;

        public void Initialize(Simulation simulation) { }

        public void PrepareForIntegration(float dt)
        {
            _gravityDt = Vector3Wide.Broadcast(gravity * dt);
            _damping = new Vector<float>(MathF.Pow(0.97f, dt * 60f));
        }

        public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia,
            Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity)
        {
            velocity.Linear = (velocity.Linear + _gravityDt) * _damping;
            velocity.Angular *= _damping;
        }
    }
}
