using System.Numerics;
using Mutagen.Bethesda.Plugins;
using StbEditor.App.Services;
using StbEditor.Render;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace StbEditor.App.Ui;

/// <summary>
/// Renders single base objects in a neutral studio: the Preview panel and the Object Window
/// thumbnails. Shares the session's mesh and texture caches with the render window.
/// </summary>
internal sealed class ObjectPreview : IDisposable
{
    public static readonly SceneLighting Studio = new(
        new Vector3(0.50f, 0.50f, 0.52f),
        new Vector3(0.95f, 0.93f, 0.88f),
        Vector3.Normalize(new Vector3(-0.35f, 0.55f, 0.75f)),
        Vector3.Zero,
        0,
        0);

    private readonly ID3D11Device _device;
    private readonly SceneRenderer _renderer;
    private readonly Dictionary<FormKey, Scene> _scenes = new();
    private static readonly HashSet<SceneObject> NoSelection = [];

    public SceneRenderer Renderer => _renderer;

    /// <summary>The session's edits: new and edited base records preview as edited.</summary>
    public StbEditor.Core.EditDocument? Document { get; set; }

    public ObjectPreview(ID3D11Device device, ID3D11DeviceContext context, MeshCache meshes, TextureCache textures)
    {
        _device = device;
        _renderer = new SceneRenderer(device, context, meshes, textures);
        _renderer.Options.Fog = false;
        _renderer.Options.ShowMarkers = true;
        _renderer.Options.Brightness = 1.15f;
    }

    /// <summary>A one-object scene for a base form (cached per form).</summary>
    public Scene SceneFor(EditorSession session, FormKey baseKey)
    {
        if (_scenes.TryGetValue(baseKey, out var scene)) return scene;

        var builder = new SceneBuilder(session.Workspace, session.Catalog) { Document = Document };
        var obj = builder.CreatePlaced(FormKey.Null, baseKey, Vector3.Zero, Vector3.Zero, 1f, FormKey.Null, false);
        scene = new Scene { Title = obj.BaseEditorId ?? baseKey.ToString(), IsInterior = true, Lighting = Studio };
        scene.Objects.Add(obj);
        _scenes[baseKey] = scene;
        return scene;
    }

    /// <summary>Requests the mesh and reports whether the scene can be drawn in its final form.</summary>
    public bool IsReady(Scene scene)
    {
        var obj = scene.Objects[0];
        // Meshes and actors with a resolved look wait for their model; markers draw at once.
        if (obj.ModelPath is null) return true;
        if (obj.Model is not null || obj.ModelFailed) return _renderer.Textures.Pending == 0;

        var model = _renderer.Meshes.Get(obj.ModelPath!, out var state);
        if (state == LoadState.Ready && model is not null)
        {
            obj.Model = model;
            obj.UpdateBounds();
        }
        else if (state == LoadState.Failed)
        {
            obj.ModelFailed = true;
        }
        return false;
    }

    /// <summary>Camera looking at the object's front (Skyrim models face +Y) from above-right.</summary>
    public static void Frame(Camera camera, Scene scene, float yaw, float pitch, float zoom = 1f)
    {
        var obj = scene.Objects[0];
        var center = obj.BoundsCenter;
        var radius = Math.Max(obj.BoundsRadius, 8f);
        // Frame the geometry, not the reach of its smoke and sparks.
        if (obj.Model is { Shapes.Count: > 0 } model)
        {
            center = obj.Transform.Apply((model.GeometryMin + model.GeometryMax) * 0.5f);
            radius = Math.Max(Vector3.Distance(model.GeometryMin, model.GeometryMax) * 0.5f * obj.Scale, 8f);
        }
        camera.FieldOfView = 40f * MathF.PI / 180f;
        camera.Near = Math.Max(0.5f, radius * 0.01f);
        camera.Yaw = yaw;
        camera.Pitch = pitch;
        camera.Position = center - camera.Forward * (radius / MathF.Sin(camera.FieldOfView / 2) * 1.05f * zoom);
    }

    public void Render(Scene scene, Camera camera, ID3D11RenderTargetView color, ID3D11DepthStencilView depth, int width, int height, Vector4 background) =>
        _renderer.RenderTo(scene, camera, color, depth, width, height, NoSelection, background);

    public void Dispose()
    {
        _renderer.Dispose();
        _scenes.Clear();
    }
}

/// <summary>
/// Object Window tiles: one small texture per form, rendered on demand a few per frame and
/// evicted least-recently-used beyond a budget (each is 160×160 BGRA, 100 KB).
/// </summary>
internal sealed class ThumbnailCache : IDisposable
{
    public const int Size = 160;
    private const int Budget = 700;
    private const int PerFrame = 6;

    private sealed class Thumb : IDisposable
    {
        public required ID3D11Texture2D Texture { get; init; }
        public required ID3D11RenderTargetView Target { get; init; }
        public required ID3D11ShaderResourceView View { get; init; }
        public bool Done;
        public int Attempts;
        public long LastUsed;

        public void Dispose()
        {
            View.Dispose();
            Target.Dispose();
            Texture.Dispose();
        }
    }

    private readonly ID3D11Device _device;
    private readonly ObjectPreview _preview;
    private readonly Dictionary<FormKey, Thumb> _thumbs = new();
    private readonly List<FormKey> _wanted = [];
    private readonly ID3D11Texture2D _depthTexture;
    private readonly ID3D11DepthStencilView _depth;
    private readonly Camera _camera = new();
    private long _frame;

    public ThumbnailCache(ID3D11Device device, ObjectPreview preview)
    {
        _device = device;
        _preview = preview;
        _depthTexture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = Size, Height = Size, MipLevels = 1, ArraySize = 1, Format = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.DepthStencil,
        });
        _depth = device.CreateDepthStencilView(_depthTexture);
    }

    public bool HasPending => _wanted.Count > 0;

    /// <summary>The thumbnail if rendered; otherwise queues it for the next frames.</summary>
    public nint Get(FormKey key)
    {
        if (_thumbs.TryGetValue(key, out var thumb))
        {
            thumb.LastUsed = _frame;
            if (thumb.Done) return thumb.View.NativePointer;
        }
        if (!_wanted.Contains(key)) _wanted.Add(key);
        return 0;
    }

    /// <summary>Renders up to a few queued thumbnails whose meshes are ready. Call once per frame.</summary>
    public void Update(EditorSession session)
    {
        _frame++;
        var rendered = 0;

        for (var i = 0; i < _wanted.Count && rendered < PerFrame; i++)
        {
            var key = _wanted[i];
            var scene = _preview.SceneFor(session, key);
            var ready = _preview.IsReady(scene);
            if (!_thumbs.TryGetValue(key, out var thumb))
            {
                thumb = Create();
                _thumbs[key] = thumb;
            }

            thumb.Attempts++;
            // Draw as soon as the mesh is there; once more when textures have streamed in.
            if (scene.Objects[0].Model is null && scene.Objects[0].ModelPath is not null && !scene.Objects[0].ModelFailed && thumb.Attempts < 600) continue;

            ObjectPreview.Frame(_camera, scene, MathF.PI - 0.55f, -0.38f);
            _preview.Render(scene, _camera, thumb.Target, _depth, Size, Size, new Vector4(0.085f, 0.085f, 0.095f, 1));
            rendered++;

            if (ready || thumb.Attempts > 600)
            {
                thumb.Done = true;
                _wanted.RemoveAt(i);
                i--;
            }
        }

        // Forget requests for tiles that scrolled away; evict beyond the budget.
        if (_wanted.Count > 64) _wanted.RemoveRange(0, _wanted.Count - 64);
        if (_thumbs.Count > Budget)
        {
            foreach (var (key, thumb) in _thumbs.OrderBy(t => t.Value.LastUsed).Take(_thumbs.Count - Budget).ToList())
            {
                thumb.Dispose();
                _thumbs.Remove(key);
            }
        }
    }

    private Thumb Create()
    {
        var texture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = Size, Height = Size, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        return new Thumb
        {
            Texture = texture,
            Target = _device.CreateRenderTargetView(texture),
            View = _device.CreateShaderResourceView(texture),
        };
    }

    public void Dispose()
    {
        foreach (var thumb in _thumbs.Values) thumb.Dispose();
        _thumbs.Clear();
        _depth.Dispose();
        _depthTexture.Dispose();
    }
}
