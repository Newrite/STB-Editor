using Hexa.NET.ImGui;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// World → Story Manager (CK: Character → Story Manager): events, their branch and quest nodes in the order the game
/// tries them, and the quests each quest node can start; a node or quest opens its form. The nodes themselves are
/// edited in their forms (conditions, flags, parent, previous sibling).
/// </summary>
internal sealed class StoryManagerWindow
{
    private bool _open;
    private List<StoryNode>? _tree;
    private string _filter = "";

    public IReadOnlyList<StoryNode>? Tree => _tree;

    public void Show(EditorApp app)
    {
        _open = true;
        Refresh(app);
    }

    public void Refresh(EditorApp app)
    {
        if (app.Session is { } session) _tree = StoryManager.Build(new RecordLookup(session.Workspace, session.Catalog), session.Catalog);
    }

    public void Draw(EditorApp app)
    {
        if (!_open || app.Session is not { } session) return;
        ImGui.SetNextWindowSize(app.Scaled(620, 640), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Story Manager") + "###storymanager", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            if (ImGui.SmallButton(T("refresh"))) Refresh(app);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 14);
            ImGui.InputTextWithHint("##smfilter", T("Filter: node or quest"), ref _filter, 128);
            ImGui.TextColored(Theme.TextDim, T("Nodes under an event in the order the game tries them; a click opens the form."));
            if (ImGui.BeginChild("##smtree"))
            {
                foreach (var root in _tree ?? [])
                    if (Matches(app, root)) DrawNode(app, root, 0);
            }
            ImGui.EndChild();
        }
        ImGui.End();
    }

    private bool Matches(EditorApp app, StoryNode node) =>
        _filter.Length == 0
        || (node.EditorId?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || node.Quests.Any(q => app.Session!.Catalog.Get(q)?.EditorId?.Contains(_filter, StringComparison.OrdinalIgnoreCase) == true)
        || node.Children.Any(c => Matches(app, c));

    private void DrawNode(EditorApp app, StoryNode node, int depth)
    {
        var kind = node.Signature switch { "SMEN" => T("event"), "SMBN" => T("branch"), _ => T("quests") };
        var label = $"{node.EditorId ?? app.Session!.Workspace.FormIdText(node.Key)}  [{kind}{(node.Signature == "SMEN" && node.Detail.Length > 0 ? " " + node.Detail : "")}]"
            + (node.Conditions > 0 ? TF($"  {node.Conditions} condition(s)") : "");
        var leaf = node.Children.Count == 0 && node.Quests.Count == 0;
        var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnArrow | (leaf ? ImGuiTreeNodeFlags.Leaf : 0) | (_filter.Length > 0 || depth == 0 ? ImGuiTreeNodeFlags.DefaultOpen : 0);
        var open = ImGui.TreeNodeEx($"{label}##{node.Key}", flags);
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen()) app.FormWindows.Open(node.Key);
        if (!open) return;
        foreach (var child in node.Children)
            if (Matches(app, child)) DrawNode(app, child, depth + 1);
        foreach (var quest in node.Quests)
        {
            var name = app.Session!.Catalog.Get(quest) is { } q ? $"{q.EditorId}  {q.Name}" : app.Session.Workspace.FormIdText(quest);
            ImGui.TreeNodeEx($"{name}##{node.Key}/{quest}", ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.Bullet);
            if (ImGui.IsItemClicked()) app.FormWindows.Open(quest);
        }
        ImGui.TreePop();
    }
}
