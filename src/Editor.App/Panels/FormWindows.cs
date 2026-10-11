using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// Separate form windows, one per record, as the Creation Kit opens a dialog per form: double-click
/// a record in the Object Window (or «Open in window» in the Inspector). Several can be open at once.
/// </summary>
internal sealed class FormWindows
{
    private readonly List<(FormKey Key, RecordForm Form)> _open = [];
    private FormKey? _focus;

    public int Count => _open.Count;

    public void Open(FormKey key)
    {
        if (!_open.Any(w => w.Key == key)) _open.Add((key, new RecordForm()));
        _focus = key;
    }

    public void CloseAll() => _open.Clear();

    public void Draw(EditorApp app)
    {
        if (app.Session is not { } session) return;
        for (var i = 0; i < _open.Count; i++)
        {
            var (key, form) = _open[i];
            var record = session.Catalog.Get(key);
            var title = $"{record?.EditorId ?? session.Workspace.FormIdText(key)}  [{record?.Signature}]###form{key}";
            ImGui.SetNextWindowSize(app.Scaled(760, 680), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(ImGui.GetMainViewport().Pos + ImGui.GetMainViewport().Size * 0.5f + new Vector2(i % 6, i % 6) * 24, ImGuiCond.FirstUseEver, new Vector2(0.5f));
            if (_focus == key)
            {
                ImGui.SetNextWindowFocus();
                _focus = null;
            }
            var open = true;
            if (ImGui.Begin(title, ref open, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking))
                form.Draw(app, key, inWindow: true);
            ImGui.End();
            if (!open) _open.RemoveAt(i--);
        }
    }
}
