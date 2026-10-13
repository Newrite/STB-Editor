using Hexa.NET.ImGui;
using StbEditor.App.Ui;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>The selected record (or placed reference) as a form; follows the selection.</summary>
internal sealed class InspectorPanel : Panel
{
    private readonly RecordForm _form = new();

    public override string Name => "Inspector";

    public RecordForm Form => _form;

    protected override void DrawContent(EditorApp app)
    {
        if (!RequireSession(app)) return;
        ReferenceProperties.Update(app);

        if (app.Selected is not { } key)
        {
            ImGui.TextColored(Theme.TextDim, T("Select a record in the Object Window or Cell View."));
            return;
        }
        _form.Draw(app, key, inWindow: false);
    }
}
