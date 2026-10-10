using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using StbEditor.App.Ui;
using StbEditor.Data.Catalog;
using StbEditor.Render;

namespace StbEditor.App.Panels;

/// <summary>
/// 3D preview of the selected base object (or the base of the selected reference): drag to
/// turn it, wheel to zoom, double-click to reset; drag the picture into the Render Window to place it.
/// </summary>
internal sealed class PreviewPanel : Panel
{
    private ViewportTarget? _target;
    private readonly Camera _camera = new();
    private float _yaw = MathF.PI - 0.55f;
    private float _pitch = -0.38f;
    private float _zoom = 1f;
    private bool _spin;
    private FormKey? _shown;

    public override string Name => "Preview";

    public bool NeedsRedraw => _spin && IsOpen;

    protected override ImGuiWindowFlags WindowFlags => ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    public override void OnSessionChanged(EditorApp app)
    {
        _shown = null;
    }

    /// <summary>The base form to show: a selected base record, or the base of a selected reference.</summary>
    public static FormKey? BaseToShow(EditorApp app)
    {
        if (app.Selected is not { } key || app.Session is not { } session) return null;

        var record = session.Catalog.Get(key);
        if (record is not null) return RecordTypes.IsPlaceable(record.Signature) ? key : null;

        return app.RenderWindow.Scene?.Objects.FirstOrDefault(o => o.Reference == key)?.Base;
    }

    protected override unsafe void DrawContent(EditorApp app)
    {
        if (!RequireSession(app) || app.Preview is not { } preview) return;

        var key = BaseToShow(app);
        if (key is null)
        {
            ImGui.TextColored(Theme.TextDim, "Select a placeable object (static, furniture, light, item…) in the Object Window.");
            return;
        }

        if (key != _shown)
        {
            _shown = key;
            _zoom = 1f;
        }

        var session = app.Session!;
        var scene = preview.SceneFor(session, key.Value);
        preview.IsReady(scene);
        var obj = scene.Objects[0];

        // Header line.
        ImGui.Text(obj.BaseEditorId ?? session.Workspace.FormIdText(key.Value));
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextDim, $"[{obj.BaseSignature}] {session.Workspace.FormIdText(key.Value)}");
        ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - ImGui.CalcTextSize("Spin").X - ImGui.GetStyle().FramePadding.X * 2);
        if (_spin) ImGui.PushStyleColor(ImGuiCol.Button, Theme.ButtonActive);
        if (ImGui.SmallButton("Spin")) _spin ^= true;
        if (_spin) ImGui.PopStyleColor();

        var size = ImGui.GetContentRegionAvail() - new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 2);
        if (size.X < 16 || size.Y < 16) return;

        _target ??= new ViewportTarget(app.Graphics.Device);
        _target.EnsureSize((int)size.X, (int)size.Y);

        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##preview", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        var io = ImGui.GetIO();

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Right, 0))
        {
            _yaw += io.MouseDelta.X * 0.01f;
            _pitch = Math.Clamp(_pitch - io.MouseDelta.Y * 0.01f, -1.5f, 1.5f);
        }
        if (ImGui.IsItemHovered() && io.MouseWheel != 0) _zoom = Math.Clamp(_zoom * (io.MouseWheel > 0 ? 0.88f : 1.14f), 0.2f, 6f);
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            _yaw = MathF.PI - 0.55f;
            _pitch = -0.38f;
            _zoom = 1f;
        }
        if (_spin) _yaw += io.DeltaTime * 0.6f;

        // Left-drag carries the object to the Render Window; right-drag turns it.
        if (ImGui.BeginDragDropSource())
        {
            FormDrag.Current = key;
            ImGui.SetDragDropPayload(FormDrag.PayloadType, null, 0);
            ImGui.Text($"Place {obj.BaseEditorId}");
            ImGui.EndDragDropSource();
        }

        ObjectPreview.Frame(_camera, scene, _yaw, _pitch, _zoom);
        preview.Render(scene, _camera, _target.ColorView!, _target.DepthView!, _target.Width, _target.Height, new Vector4(0.07f, 0.07f, 0.08f, 1));

        unsafe
        {
            ImGui.GetWindowDrawList().AddImage(new ImTextureRef(null, new ImTextureID((ulong)_target.ShaderView!.NativePointer)), origin, origin + size);
        }

        // Footer: model and size.
        if (obj.Model is { } model)
        {
            var extent = model.Shapes.Count > 0 ? model.GeometryMax - model.GeometryMin : model.BoundsMax - model.BoundsMin;
            var triangles = model.Shapes.Sum(s => s.IndexCount / 3);
            ImGui.TextColored(Theme.TextDim, $"{obj.ModelPath}");
            ImGui.TextColored(Theme.TextDim, $"{extent.X:0} x {extent.Y:0} x {extent.Z:0} units · {triangles:N0} tris · {model.Shapes.Count} shapes{(model.Particles.Count > 0 ? $" · {model.Particles.Count} particle systems" : "")} · RMB drag to turn, LMB drag to place");
        }
        else
        {
            ImGui.TextColored(Theme.TextDim, obj.ModelFailed ? $"model failed: {obj.ModelPath}" : obj.ModelPath is null ? "no model (marker)" : $"loading {obj.ModelPath}…");
        }
    }

    public void Shutdown() => _target?.Dispose();
}
