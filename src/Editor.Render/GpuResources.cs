using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using StbEditor.Assets;
using StbEditor.Nif;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace StbEditor.Render;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MeshVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector3 Tangent;
    public Vector3 Bitangent;
    public Vector2 UV;
    public uint Color;

    public static readonly int Size = Marshal.SizeOf<MeshVertex>();
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct InstanceData
{
    public Matrix4x4 World;

    /// <summary>rgb highlight colour, w strength (0 = none).</summary>
    public Vector4 Highlight;

    public static readonly int Size = Marshal.SizeOf<InstanceData>();
}

/// <summary>One NIF shape on the GPU plus the CPU copy picking needs.</summary>
public sealed class GpuShape : IDisposable
{
    public required ID3D11Buffer VertexBuffer { get; init; }
    public required ID3D11Buffer IndexBuffer { get; init; }
    public required int IndexCount { get; init; }
    public required NifShape Source { get; init; }

    public NifMaterial Material => Source.Material;

    public void Dispose()
    {
        VertexBuffer.Dispose();
        IndexBuffer.Dispose();
    }
}

public sealed class GpuModel : IDisposable
{
    /// <summary>Composite (actor) models: parts that could not be loaded, e.g. a missing FaceGen head.</summary>
    public IReadOnlyList<string> MissingParts { get; set; } = [];

    public required string Path { get; init; }
    public required List<GpuShape> Shapes { get; init; }

    /// <summary>The NIF has Havok collision (navmesh generation uses only what the game collides with).</summary>
    public bool HasCollision { get; set; }

    /// <summary>The Havok collision as convex pieces when all of it could be read (model space), else null.</summary>
    public List<Vector3[]>? CollisionPieces { get; set; }

    /// <summary>Edges of the whole Havok collision (triangles and pieces), model space, as line pairs — for the collision view.</summary>
    public Vector3[] CollisionLines { get; set; } = [];

    public required Vector3 BoundsMin { get; init; }
    public required Vector3 BoundsMax { get; init; }

    /// <summary>Simulated on the CPU by the renderer (shared by every reference of the model).</summary>
    public IReadOnlyList<NifParticleSystem> Particles { get; init; } = [];

    /// <summary>Bounds of the geometry alone, without particle reach (for drop-to-ground).</summary>
    public Vector3 GeometryMin { get; init; }
    public Vector3 GeometryMax { get; init; }

    public bool IsEmpty => Shapes.Count == 0 && Particles.Count == 0;
    public Vector3 Center => (BoundsMin + BoundsMax) * 0.5f;
    public float Radius => IsEmpty ? 0 : Vector3.Distance(BoundsMin, BoundsMax) * 0.5f;

    /// <summary>GPU buffers plus the CPU copy kept for picking (estimate, for the cache budget).</summary>
    public long Bytes { get; init; }

    /// <summary>
    /// Evicted from the cache: GPU buffers are gone. Holders (scene objects) drop it and ask the
    /// cache again; the CPU shapes stay readable, so picking on a stale model is still safe.
    /// </summary>
    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        IsDisposed = true;
        foreach (var shape in Shapes) shape.Dispose();
    }

    public static GpuModel Create(ID3D11Device device, string path, IEnumerable<NifShape> shapes, IReadOnlyList<NifParticleSystem>? particles = null)
    {
        var list = new List<GpuShape>();
        long bytes = 0;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var shape in shapes)
        {
            var vertices = new MeshVertex[shape.Positions.Length];
            for (var i = 0; i < vertices.Length; i++)
            {
                var n = shape.Normals?[i] ?? Vector3.UnitZ;
                vertices[i] = new MeshVertex
                {
                    Position = shape.Positions[i],
                    Normal = n,
                    Tangent = shape.Tangents?[i] ?? Vector3.UnitX,
                    Bitangent = shape.Bitangents?[i] ?? Vector3.UnitY,
                    UV = shape.UVs?[i] ?? Vector2.Zero,
                    Color = shape.Colors?[i] ?? 0xFFFFFFFF,
                };
            }

            var vb = device.CreateBuffer(vertices, BindFlags.VertexBuffer, ResourceUsage.Immutable);
            var ib = device.CreateBuffer(shape.Indices, BindFlags.IndexBuffer, ResourceUsage.Immutable);
            list.Add(new GpuShape { VertexBuffer = vb, IndexBuffer = ib, IndexCount = shape.Indices.Length, Source = shape });
            bytes += (long)vertices.Length * MeshVertex.Size * 2 + (long)shape.Indices.Length * sizeof(ushort) * 2;

            min = Vector3.Min(min, shape.BoundsMin);
            max = Vector3.Max(max, shape.BoundsMax);
        }

        // Solid geometry only: camera-facing glow cards, heat haze and hidden helpers reach far
        // beyond what you would frame or set on the ground.
        var geometryMin = new Vector3(float.MaxValue);
        var geometryMax = new Vector3(float.MinValue);
        foreach (var shape in list.Select(x => x.Source))
        {
            if (shape.Billboard is not null || shape.IsHidden || shape.IsEditorMarker || shape.Material.Flags1.HasFlag(ShaderFlags1.Refraction)) continue;
            geometryMin = Vector3.Min(geometryMin, shape.BoundsMin);
            geometryMax = Vector3.Max(geometryMax, shape.BoundsMax);
        }
        if (geometryMin.X > geometryMax.X) (geometryMin, geometryMax) = list.Count > 0 ? (min, max) : (Vector3.Zero, Vector3.Zero);
        foreach (var system in particles ?? [])
        {
            if (system.IsHidden || system.IsEditorMarker) continue;
            min = Vector3.Min(min, system.BoundsMin);
            max = Vector3.Max(max, system.BoundsMax);
        }

        if (min.X > max.X)
        {
            min = max = Vector3.Zero;
        }

        return new GpuModel { Path = path, Shapes = list, BoundsMin = min, BoundsMax = max, Particles = particles ?? [],
            GeometryMin = geometryMin, GeometryMax = geometryMax, Bytes = bytes + 4096 };
    }
}

public enum LoadState { Pending, Ready, Failed }

/// <summary>
/// Path → resource, loaded on worker threads. <see cref="Get"/> never blocks: it returns the
/// resource once ready and queues the load the first time it is asked for. Every Get marks the
/// entry used; <see cref="Trim"/> drops the longest-unused ones once the cache is over budget, so
/// flying across a worldspace does not keep everything it ever saw.
/// </summary>
public abstract class AsyncCache<T> : IDisposable where T : class, IDisposable
{
    private sealed class Entry
    {
        public volatile T? Value;
        public volatile LoadState State;
        public string? Error;
        public long Bytes;
        public long LastUsed;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _workers;
    private int _pending;
    private int _version;
    private long _bytes;

    /// <summary>Estimated size of everything loaded.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    protected AsyncCache(int workers) => _workers = new SemaphoreSlim(workers);

    public int Pending => Volatile.Read(ref _pending);
    public int Count => _entries.Count;

    /// <summary>Bumped whenever something finishes loading, so the scene knows to re-resolve.</summary>
    public int Version => Volatile.Read(ref _version);

    public IEnumerable<(string Path, string Error)> Failures =>
        _entries.Where(e => e.Value.State == LoadState.Failed).Select(e => (e.Key, e.Value.Error ?? ""));

    public T? Get(string path, out LoadState state)
    {
        var entry = _entries.GetOrAdd(path, static (key, self) =>
        {
            var e = new Entry { State = LoadState.Pending };
            Interlocked.Increment(ref self._pending);
            Task.Run(() => self.Load(key, e));
            return e;
        }, this);

        entry.LastUsed = Environment.TickCount64;
        state = entry.State;
        return entry.Value;
    }

    public T? Get(string path) => Get(path, out _);

    private async Task Load(string path, Entry entry)
    {
        await _workers.WaitAsync().ConfigureAwait(false);
        try
        {
            entry.Value = Create(path, out var bytes);
            entry.Bytes = bytes;
            Interlocked.Add(ref _bytes, bytes);
            entry.State = LoadState.Ready;
        }
        catch (Exception ex)
        {
            entry.Error = ex.Message;
            entry.State = LoadState.Failed;
        }
        finally
        {
            _workers.Release();
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _version);
        }
    }

    protected abstract T Create(string path, out long bytes);

    /// <summary>
    /// Over <paramref name="budget"/> bytes: disposes ready entries not asked for in the last
    /// <paramref name="idleMilliseconds"/>, oldest first, until under budget. What is in view is
    /// asked for every frame, so it is never dropped — a scene bigger than the budget just keeps it.
    /// Returns how many were dropped. UI thread only (holders must check for disposal).
    /// </summary>
    public int Trim(long budget, long idleMilliseconds)
    {
        if (Bytes <= budget) return 0;
        var cutoff = Environment.TickCount64 - idleMilliseconds;
        var dropped = 0;
        foreach (var (path, entry) in _entries.Where(e => e.Value.State == LoadState.Ready && e.Value.LastUsed < cutoff)
                     .OrderBy(e => e.Value.LastUsed).ToList())
        {
            if (Bytes <= budget * 9 / 10) break;
            if (!_entries.TryRemove(path, out _)) continue;
            Interlocked.Add(ref _bytes, -entry.Bytes);
            entry.Value?.Dispose();
            dropped++;
        }
        if (dropped > 0) Interlocked.Increment(ref _version);
        return dropped;
    }

    /// <summary>
    /// Drops the entries whose path matches (a file changed on disk): the next Get loads it again. UI thread only;
    /// holders see their resource disposed and ask again. Returns how many.
    /// </summary>
    public int Forget(Func<string, bool> match)
    {
        var dropped = 0;
        foreach (var (path, entry) in _entries.Where(e => match(e.Key) && e.Value.State != LoadState.Pending).ToList())
        {
            if (!_entries.TryRemove(path, out _)) continue;
            Interlocked.Add(ref _bytes, -entry.Bytes);
            entry.Value?.Dispose();
            dropped++;
        }
        if (dropped > 0) Interlocked.Increment(ref _version);
        return dropped;
    }

    public virtual void Dispose()
    {
        foreach (var entry in _entries.Values) entry.Value?.Dispose();
        _entries.Clear();
        Interlocked.Exchange(ref _bytes, 0);
    }
}

public sealed class MeshCache(ID3D11Device device, AssetFileSystem assets)
    : AsyncCache<GpuModel>(Math.Max(2, Environment.ProcessorCount / 2))
{
    /// <summary>
    /// "grass:path.nif": a grass model as the game's grass shader draws it — its vertex alpha is the wind's weight, not
    /// transparency (read as transparency, only the tips of the blades would show).
    /// </summary>
    public const string GrassPrefix = "grass:";

    protected override GpuModel Create(string path, out long bytes)
    {
        var model = CreateModel(path);
        bytes = model.Bytes;
        return model;
    }

    private GpuModel CreateModel(string path)
    {
        // "actor:a.nif|b.nif|…": an NPC put together from body, outfit and head meshes.
        if (path.StartsWith(ActorAppearance.CompositePrefix, StringComparison.Ordinal))
        {
            var shapes = new List<NifShape>();
            var particles = new List<NifParticleSystem>();
            var missing = new List<string>();
            var parts = new List<string>();
            foreach (var entry in path[ActorAppearance.CompositePrefix.Length..].Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var split = entry.Split(ActorAppearance.FallbackSeparator, 2);
                if (split.Length == 2 && assets.Find(split[0]) is null)
                {
                    missing.Add(split[0] + " (using head parts)");
                    parts.AddRange(split[1].Split(',', StringSplitOptions.RemoveEmptyEntries));
                }
                else
                {
                    parts.Add(split[0]);
                }
            }

            foreach (var part in parts)
            {
                var partBytes = assets.ReadAllBytes(part);
                if (partBytes is null)
                {
                    missing.Add(part);
                    continue;
                }
                try
                {
                    var partModel = NifFile.Load(partBytes, part);
                    shapes.AddRange(partModel.Shapes);
                    particles.AddRange(partModel.Particles);
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
                {
                    missing.Add($"{part} ({ex.Message})");
                }
            }
            if (shapes.Count == 0) throw new FileNotFoundException("no actor parts: " + string.Join(", ", missing));
            var composite = GpuModel.Create(device, path, shapes, particles);
            composite.MissingParts = missing;
            return composite;
        }

        var grass = path.StartsWith(GrassPrefix, StringComparison.Ordinal);
        var file = grass ? path[GrassPrefix.Length..] : path;
        var bytes = assets.ReadAllBytes(file) ?? throw new FileNotFoundException(file);
        var model = NifFile.Load(bytes, file);
        if (grass)
            foreach (var shape in model.Shapes) shape.Material.Flags1 &= ~ShaderFlags1.VertexAlpha;
        var created = GpuModel.Create(device, path, model.Shapes, model.Particles);
        created.HasCollision = model.HasCollision;
        if (model.CollisionComplete && model.CollisionPieces.Count > 0) created.CollisionPieces = model.CollisionPieces;
        created.CollisionLines = CollisionLines(model);
        return created;
    }

    /// <summary>The collision's edges as line pairs: each triangle edge once, and the hull edges of each convex piece.</summary>
    public static Vector3[] CollisionLines(NifModel model)
    {
        var lines = new List<Vector3>();
        var seen = new HashSet<(int, int)>();
        var p = model.CollisionPositions;
        var idx = model.CollisionIndices;
        for (var t = 0; t + 2 < idx.Count; t += 3)
            for (var e = 0; e < 3; e++)
            {
                int a = idx[t + e], b = idx[t + (e + 1) % 3];
                if (a >= p.Count || b >= p.Count || !seen.Add(a < b ? (a, b) : (b, a))) continue;
                lines.Add(p[a]);
                lines.Add(p[b]);
            }
        if (model.CollisionPieces.Count == 0) return [.. lines];
        var pool = new BepuUtilities.Memory.BufferPool();
        try
        {
            foreach (var piece in model.CollisionPieces)
            {
                if (piece.Length < 4) continue;
                BepuPhysics.Collidables.ConvexHullHelper.ComputeHull(piece.AsSpan(), pool, out var hull);
                try
                {
                    var edges = new HashSet<(int, int)>();
                    for (var f = 0; f < hull.FaceStartIndices.Length; f++)
                    {
                        var first = hull.FaceStartIndices[f];
                        var end = f + 1 < hull.FaceStartIndices.Length ? hull.FaceStartIndices[f + 1] : hull.FaceVertexIndices.Length;
                        for (var k = first; k < end; k++)
                        {
                            int a = hull.OriginalVertexMapping[hull.FaceVertexIndices[k]], b = hull.OriginalVertexMapping[hull.FaceVertexIndices[k + 1 < end ? k + 1 : first]];
                            if (!edges.Add(a < b ? (a, b) : (b, a))) continue;
                            lines.Add(piece[a]);
                            lines.Add(piece[b]);
                        }
                    }
                }
                finally { hull.Dispose(pool); }
            }
        }
        finally { pool.Clear(); }
        return [.. lines];
    }
}

public sealed class TextureCache : AsyncCache<ID3D11ShaderResourceView>
{
    private readonly ID3D11Device _device;
    private readonly AssetFileSystem _assets;

    public ID3D11ShaderResourceView White { get; }

    /// <summary>Shown where a texture is missing or unreadable (purple, as in the Creation Kit).</summary>
    public ID3D11ShaderResourceView Missing { get; }
    public ID3D11ShaderResourceView Black { get; }

    /// <summary>Tangent-space "straight up" with zero specular in alpha.</summary>
    public ID3D11ShaderResourceView FlatNormal { get; }

    public TextureCache(ID3D11Device device, AssetFileSystem assets) : base(Math.Max(2, Environment.ProcessorCount / 2))
    {
        _device = device;
        _assets = assets;
        White = Solid(0xFFFFFFFF);
        Missing = Solid(0xFFC040B0);
        Black = Solid(0xFF000000);
        FlatNormal = Solid(0x00FF8080);
    }

    protected override ID3D11ShaderResourceView Create(string path, out long size)
    {
        var bytes = _assets.ReadAllBytes(path) ?? throw new FileNotFoundException(path);
        // The DDS file holds the same mip chain the GPU gets: a good size estimate.
        size = bytes.Length;
        return Dds.CreateTexture(_device, bytes).View;
    }

    private ID3D11ShaderResourceView Solid(uint rgba)
    {
        var description = new Texture2DDescription
        {
            Width = 1,
            Height = 1,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
        };

        var pixel = new[] { rgba };
        var handle = GCHandle.Alloc(pixel, GCHandleType.Pinned);
        try
        {
            using var texture = _device.CreateTexture2D(description, [new SubresourceData(handle.AddrOfPinnedObject(), 4, 4)]);
            return _device.CreateShaderResourceView(texture);
        }
        finally
        {
            handle.Free();
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        White.Dispose();
        Missing.Dispose();
        Black.Dispose();
        FlatNormal.Dispose();
    }
}
