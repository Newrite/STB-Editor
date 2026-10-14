using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Render;
using Vortice.Direct3D11;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// What the Claude bridge does in the Render Window: pictures of the view, camera moves, objects near the camera, edits as
/// undoable steps (same paths as the mouse), Claude's notes drawn over the view, and the user's marks (K under the cursor)
/// that tell Claude «here».
/// </summary>
internal sealed partial class RenderWindow
{
    /// <summary>A note of Claude's over the view: at a place or on a reference, with a colour.</summary>
    public sealed record Annotation(Vector3 Position, string Text, uint Colour, FormKey? Reference);

    /// <summary>A place the user marked for Claude (K): where, which cell and object, and what they wrote about it.</summary>
    public sealed class UserMark
    {
        public required int Number { get; init; }
        public required Vector3 Position { get; init; }
        public FormKey? Cell { get; init; }
        public FormKey? World { get; init; }
        public FormKey? Reference { get; init; }
        public string? ReferenceLabel { get; init; }
        public string Note = "";
    }

    public List<Annotation> Annotations { get; } = [];
    public List<UserMark> Marks { get; } = [];
    private int _nextMark = 1;

    public Camera ViewCamera => _camera;
    public Vector2 ViewSize => _viewSize;
    public bool IsLoading => _loading is not null;
    public RenderOptions? RenderOptions => _renderer?.Options;
    public (int Meshes, int Textures) Streaming => _renderer is null ? (0, 0) : (_renderer.Meshes.Pending, _renderer.Textures.Pending);

    private readonly List<(TaskCompletionSource<(byte[] Rgba, int Width, int Height)> Done, bool Wait, int Frames, bool Overlay)> _captures = [];
    private readonly List<(TaskCompletionSource<(byte[] Rgba, int Width, int Height)> Done, int X, int Y, int Width, int Height)> _windowShots = [];

    /// <summary>A picture of the view is waiting for the next frames.</summary>
    public bool HasCaptures => _captures.Count > 0;

    /// <summary>The view's next frame as RGBA; with <paramref name="waitForStreaming"/> once meshes and textures have loaded (at most ~10 s).</summary>
    /// <param name="overlay">With what is drawn over the view (Claude's notes, the user's marks, the corner text, the gizmo): taken from the
    /// finished window frame, cut to the view.</param>
    public Task<(byte[] Rgba, int Width, int Height)> Capture(bool waitForStreaming, bool overlay = true)
    {
        var done = new TaskCompletionSource<(byte[], int, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _captures.Add((done, waitForStreaming, 0, overlay));
        return done.Task;
    }

    /// <summary>After the whole window is drawn (Program): the pictures that want the overlay, cut from the back buffer (BGRA).</summary>
    public void ServeWindowShots(Func<byte[]> readBackBuffer, int bufferWidth, int bufferHeight)
    {
        if (_windowShots.Count == 0) return;
        var frame = readBackBuffer();
        foreach (var (done, x0, y0, w0, h0) in _windowShots)
        {
            var x = Math.Clamp(x0, 0, bufferWidth - 1);
            var y = Math.Clamp(y0, 0, bufferHeight - 1);
            var w = Math.Clamp(w0, 1, bufferWidth - x);
            var h = Math.Clamp(h0, 1, bufferHeight - y);
            var rgba = new byte[w * h * 4];
            for (var row = 0; row < h; row++)
                for (var col = 0; col < w; col++)
                {
                    var s = ((y + row) * bufferWidth + x + col) * 4;
                    var d = (row * w + col) * 4;
                    rgba[d] = frame[s + 2];
                    rgba[d + 1] = frame[s + 1];
                    rgba[d + 2] = frame[s];
                    rgba[d + 3] = 255;
                }
            done.TrySetResult((rgba, w, h));
        }
        _windowShots.Clear();
    }

    /// <summary>After the frame is rendered: hands the pictures out that are ready.</summary>
    private void ServeCaptures(EditorApp app)
    {
        if (_captures.Count == 0) return;
        if (_target?.ColorTexture is not { } texture || _scene is null)
        {
            foreach (var c in _captures) c.Done.TrySetException(new InvalidOperationException("nothing is loaded in the Render Window"));
            _captures.Clear();
            return;
        }
        byte[]? pixels = null;
        for (var i = _captures.Count - 1; i >= 0; i--)
        {
            var (done, wait, frames, overlay) = _captures[i];
            frames++;
            var settled = _loading is null && (!wait || (_renderer!.Meshes.Pending == 0 && _renderer.Textures.Pending == 0));
            if ((settled && frames >= 3) || frames > 600)
            {
                if (overlay) _windowShots.Add((done, (int)_viewOrigin.X, (int)_viewOrigin.Y, (int)_viewSize.X, (int)_viewSize.Y));
                else
                {
                    pixels ??= ReadTexture(app, texture);
                    done.TrySetResult((pixels, _target.Width, _target.Height));
                }
                _captures.RemoveAt(i);
            }
            else _captures[i] = (done, wait, frames, overlay);
        }
    }

    private static byte[] ReadTexture(EditorApp app, ID3D11Texture2D texture)
    {
        var device = app.Graphics.Device;
        var context = app.Graphics.Context;
        var desc = texture.Description;
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        desc.MiscFlags = ResourceOptionFlags.None;
        using var staging = device.CreateTexture2D(desc);
        context.CopyResource(staging, texture);
        var mapped = context.Map(staging, 0, MapMode.Read);
        try
        {
            var width = (int)desc.Width;
            var height = (int)desc.Height;
            var pixels = new byte[width * height * 4];
            unsafe
            {
                for (var y = 0; y < height; y++)
                    new ReadOnlySpan<byte>((byte*)mapped.DataPointer + y * mapped.RowPitch, width * 4).CopyTo(pixels.AsSpan(y * width * 4));
            }
            return pixels;
        }
        finally { context.Unmap(staging, 0); }
    }

    /// <summary>Puts the camera at a place, looking along a heading (degrees, 0 = north, clockwise) and pitch (degrees, down negative).</summary>
    public void SetCamera(Vector3 position, float yawDegrees, float pitchDegrees)
    {
        _camera.Position = position;
        _camera.Yaw = yawDegrees * MathF.PI / 180;
        _camera.Pitch = Math.Clamp(pitchDegrees, -89, 89) * MathF.PI / 180;
    }

    /// <summary>Looks at a point from <paramref name="distance"/> away, from a heading and pitch (degrees).</summary>
    public void LookAtPoint(Vector3 target, float distance, float yawDegrees, float pitchDegrees)
    {
        _camera.Yaw = yawDegrees * MathF.PI / 180;
        _camera.Pitch = Math.Clamp(pitchDegrees, -89, 89) * MathF.PI / 180;
        _camera.Position = target - _camera.Forward * distance;
    }

    /// <summary>The loaded object with a reference FormKey.</summary>
    public SceneObject? ObjectOf(FormKey reference) => _scene?.Objects.FirstOrDefault(o => o.Reference == reference && !o.IsDeleted);

    /// <summary>What a point of the view (fractions 0..1 across and down) shows: the object and the point hit.</summary>
    public (SceneObject? Object, Vector3? Point) PickFraction(float fx, float fy)
    {
        if (_scene is null || _renderer is null) return (null, null);
        var (origin, direction) = _camera.Ray(new Vector2(fx * _viewSize.X, fy * _viewSize.Y), _viewSize);
        if (Picking.Pick(_scene, origin, direction, _renderer.Options) is { } hit) return (hit.Object, hit.Point);
        return (null, Picking.PickTerrain(_scene, origin, direction));
    }

    /// <summary>New placements of objects, as one undoable step (as a drag would).</summary>
    public void SetStates(string name, IReadOnlyList<(SceneObject Object, RefState State)> changes)
    {
        if (changes.Count == 0) return;
        var before = changes.ToDictionary(c => c.Object, c => c.Object.State);
        foreach (var (obj, state) in changes) obj.Apply(state);
        Commit(name, before);
    }

    /// <summary>A new reference of a base (needs the active file), selected; null when it cannot be placed.</summary>
    public SceneObject? PlaceNew(EditorApp app, FormKey baseKey, Vector3 position, Vector3 rotation, float scale)
    {
        if (_scene is null || app.Session?.Catalog.Get(baseKey)?.Signature is not { } signature) return null;
        if (!StbEditor.Data.Catalog.RecordTypes.IsPlaceable(signature) || app.ActivePlugin is null) return null;
        var created = CreateNew(app, baseKey, signature, position, rotation, scale);
        if (created is null) return null;
        _selection.Clear();
        _selection.Add(created);
        return created;
    }

    public void SelectObjects(IEnumerable<SceneObject> objects)
    {
        _selection.Clear();
        foreach (var o in objects) _selection.Add(o);
    }

    /// <summary>K over the view: a numbered mark at the point under the cursor, for Claude.</summary>
    private void PlaceMark(EditorApp app, Vector2 mouse, Vector2 size)
    {
        if (_scene is null) return;
        var (origin, direction) = _camera.Ray(mouse, size);
        var hit = Picking.Pick(_scene, origin, direction, _renderer!.Options);
        var point = hit?.Point ?? Picking.PickTerrain(_scene, origin, direction) ?? origin + direction * 400f;
        var cell = _scene.IsInterior ? _cell : null;
        Marks.Add(new UserMark
        {
            Number = _nextMark++, Position = point, Cell = cell, World = _scene.Worldspace,
            Reference = hit?.Object.Reference, ReferenceLabel = hit?.Object.BaseEditorId,
        });
        app.ClaudeWindow.Show();
    }

    /// <summary>Claude's notes and the user's marks over the view.</summary>
    private void DrawClaudeOverlay(ImDrawListPtr draw, Vector2 origin, Vector2 size)
    {
        if (Annotations.Count == 0 && Marks.Count == 0) return;
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(_camera.FieldOfView, size.X / size.Y, 1f, 1_000_000f);
        var viewProjection = _camera.View * projection;
        Vector2? Screen(Vector3 p)
        {
            var clip = Vector4.Transform(new Vector4(p, 1f), viewProjection);
            if (clip.W <= 0) return null;
            return origin + new Vector2((clip.X / clip.W * 0.5f + 0.5f) * size.X, (0.5f - clip.Y / clip.W * 0.5f) * size.Y);
        }
        draw.PushClipRect(origin, origin + size, true);
        foreach (var a in Annotations)
        {
            var place = a.Reference is { } r && ObjectOf(r) is { } obj ? obj.BoundsCenter : a.Position;
            if (Screen(place) is not { } s) continue;
            draw.AddCircle(s, 9, a.Colour, 0, 2f);
            draw.AddLine(s + new Vector2(7, -7), s + new Vector2(22, -22), a.Colour, 1.5f);
            var at = s + new Vector2(24, -32);
            var textSize = ImGui.CalcTextSize(a.Text);
            draw.AddRectFilled(at - new Vector2(4, 2), at + textSize + new Vector2(4, 2), 0xC0000000u, 3);
            draw.AddText(at, a.Colour, a.Text);
        }
        foreach (var m in Marks)
        {
            if (Screen(m.Position) is not { } s) continue;
            var colour = ImGui.GetColorU32(Theme.Accent);
            draw.AddCircleFilled(s, 11, 0xD0000000u);
            draw.AddCircle(s, 11, colour, 0, 2f);
            var label = $"{m.Number}";
            draw.AddText(s - ImGui.CalcTextSize(label) / 2, colour, label);
            if (m.Note.Length > 0) draw.AddText(s + new Vector2(14, -8), colour, m.Note.Length > 40 ? m.Note[..40] + "…" : m.Note);
        }
        draw.PopClipRect();
    }

    /// <summary>The caption of the mark key in Help.</summary>
    public static string MarkKeyHelp => T("Mark the point under the cursor for Claude (Claude window: notes, delete)");
}
