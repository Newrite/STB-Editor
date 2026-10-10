using System.Globalization;
using System.Numerics;
using Mutagen.Bethesda.Plugins;
using StbEditor.App.Services;
using StbEditor.Render;
using Vortice.Direct3D11;

namespace StbEditor.App.Ui;

/// <summary>
/// <c>--object-shot FormID out.bmp [size] [yawDeg] [pitchDeg] [zoom]</c>: renders one base object
/// with the Preview renderer at a large size, after its mesh and textures have streamed in and a
/// second of animation has played. For checking effects and models without a person at the screen.
/// </summary>
internal sealed class ObjectShot
{
    private readonly FormKey _key;
    private readonly string _path;
    private readonly int _size;
    private readonly float _yaw;
    private readonly float _pitch;
    private readonly float _zoom;
    private readonly Camera _camera = new();
    private ViewportTarget? _target;
    private int _readyFrames;
    private int _frames;

    public bool Done { get; private set; }

    private ObjectShot(FormKey key, string path, int size, float yaw, float pitch, float zoom)
    {
        _key = key;
        _path = path;
        _size = size;
        _yaw = yaw;
        _pitch = pitch;
        _zoom = zoom;
    }

    public static ObjectShot? FromCommandLine(EditorApp app)
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "--object-shot");
        if (i < 0 || i + 2 >= args.Length || app.Session is null) return null;
        if (app.Session.Workspace.ParseFormReference(args[i + 1]) is not { } key)
        {
            Log.Warn($"--object-shot: no form {args[i + 1]}");
            return null;
        }

        float Arg(int offset, float fallback) =>
            i + offset < args.Length && float.TryParse(args[i + offset], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        return new ObjectShot(key, args[i + 2], (int)Arg(3, 768), Arg(4, 180 - 31.5f) * MathF.PI / 180f, Arg(5, -21.8f) * MathF.PI / 180f, Arg(6, 1f));
    }

    /// <summary>Call once per frame; writes the file when everything is in.</summary>
    public void Step(EditorApp app)
    {
        if (Done || app.Session is not { } session || app.Preview is not { } preview) return;
        if (++_frames > 60 * 60)
        {
            Log.Warn("--object-shot: gave up waiting for assets");
            Done = true;
            return;
        }

        var scene = preview.SceneFor(session, _key);
        _target ??= new ViewportTarget(app.Graphics.Device);
        _target.EnsureSize(_size, _size);

        ObjectPreview.Frame(_camera, scene, _yaw, _pitch, _zoom);
        preview.Render(scene, _camera, _target.ColorView!, _target.DepthView!, _size, _size, new Vector4(0.07f, 0.07f, 0.08f, 1));

        if (!preview.IsReady(scene)) return;
        // About a second of frames once ready, so particles and controllers have moved on.
        if (++_readyFrames < 60) return;

        Screenshot.SaveBmp(_path, _size, _size, app.Graphics.ReadTexture(_target.ColorTexture!, _size, _size, swapRedBlue: true));
        Log.Info($"object shot {_key} -> {_path}");
        _target.Dispose();
        Done = true;
    }
}
