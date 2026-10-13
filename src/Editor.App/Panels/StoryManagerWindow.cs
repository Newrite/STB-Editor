using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// World → Story Manager (CK: Character → Story Manager): events, their branch and quest nodes in the order the game
/// tries them, and the quests each quest node can start; a node or quest opens its form. Branch and quest nodes are moved by
/// dragging (onto a node: after it; with Ctrl onto an event or branch: its last child) or with the context menu — one undo
/// step over every node whose parent / previous sibling changes; the rest is edited in the forms.
/// </summary>
internal sealed class StoryManagerWindow
{
    private bool _open;
    private List<StoryNode>? _tree;
    private string _filter = "";
    private int _builtFor = -1;
    private Dictionary<FormKey, StoryNode> _byKey = [];

    public IReadOnlyList<StoryNode>? Tree => _tree;

    public void Show(EditorApp app)
    {
        _open = true;
        Refresh(app);
    }

    public void Refresh(EditorApp app)
    {
        if (app.Session is not { } session) return;
        _tree = StoryManager.Build(new RecordLookup(session.Workspace, session.Catalog), session.Catalog, k => app.Document.Override(k)?.Record);
        _byKey = [];
        void Index(StoryNode n) { _byKey[n.Key] = n; foreach (var c in n.Children) Index(c); }
        foreach (var root in _tree) Index(root);
        _builtFor = app.Document.Version;
    }

    public void Draw(EditorApp app)
    {
        if (!_open || app.Session is not { } session) return;
        if (_builtFor != app.Document.Version) Refresh(app);
        ImGui.SetNextWindowSize(app.Scaled(620, 640), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Story Manager") + "###storymanager", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            if (ImGui.SmallButton(T("refresh"))) Refresh(app);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 14);
            ImGui.InputTextWithHint("##smfilter", T("Filter: node or quest"), ref _filter, 128);
            ImGui.TextColored(Theme.TextDim, T("Nodes under an event in the order the game tries them; a click opens the form. Drag a branch or quest node onto another: after it (Ctrl onto an event or branch: its last child); right click: up, down."));
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
        var open = ImGui.TreeNodeEx($"{label}{(app.Document.Override(node.Key) is not null ? "  *" : "")}##{node.Key}", flags);
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen()) app.FormWindows.Open(node.Key);
        DragAndMenu(app, node);
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

    private void DragAndMenu(EditorApp app, StoryNode node)
    {
        if (node.Signature != "SMEN" && ImGui.BeginDragDropSource())
        {
            unsafe
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(node.Key.ToString());
                fixed (byte* p = bytes) ImGui.SetDragDropPayload("STB_SMNODE", p, (nuint)bytes.Length);
            }
            ImGui.Text(node.EditorId ?? node.Key.ToString());
            ImGui.EndDragDropSource();
        }
        if (ImGui.BeginDragDropTarget())
        {
            unsafe
            {
                var payload = ImGui.AcceptDragDropPayload("STB_SMNODE");
                if (payload.Handle != null && payload.Data != null && FormKey.TryFactory(System.Text.Encoding.UTF8.GetString((byte*)payload.Data, payload.DataSize), out var key)
                    && key != node.Key && _byKey.TryGetValue(key, out var dragged))
                {
                    if (node.Signature == "SMEN" || (ImGui.GetIO().KeyCtrl && node.Signature == "SMBN"))
                        MoveTo(app, dragged, node.Key, node.Signature == "SMEN" && !ImGui.GetIO().KeyCtrl ? null : node.Children.LastOrDefault(c => c.Key != key)?.Key);
                    else MoveTo(app, dragged, node.Parent, node.Key);
                }
            }
            ImGui.EndDragDropTarget();
        }
        if (node.Signature == "SMEN" || !ImGui.BeginPopupContextItem($"##smctx{node.Key}")) return;
        var siblings = Siblings(node);
        var at = siblings.FindIndex(s => s.Key == node.Key);
        if (ImGui.MenuItem(T("Move up"), (string?)null, false, at > 0)) MoveTo(app, node, node.Parent, at > 1 ? siblings[at - 2].Key : null);
        if (ImGui.MenuItem(T("Move down"), (string?)null, false, at >= 0 && at + 1 < siblings.Count)) MoveTo(app, node, node.Parent, siblings[at + 1].Key);
        ImGui.EndPopup();
    }

    private List<StoryNode> Siblings(StoryNode node) =>
        node.Parent is { } p && _byKey.TryGetValue(p, out var parent) ? parent.Children : (_tree ?? []).Where(r => r.Parent is null && r.Signature == node.Signature).ToList();

    /// <summary>Puts a branch or quest node under an event or branch node, right after <paramref name="after"/> (null: first).</summary>
    public void MoveTo(EditorApp app, StoryNode node, FormKey? parent, FormKey? after)
    {
        if (node.Signature == "SMEN") return;
        if (parent is not { } p || !_byKey.TryGetValue(p, out var owner) || owner.Signature == "SMQN")
        {
            Log.Warn(T("Story Manager: branch and quest nodes go under an event or a branch node"));
            return;
        }
        for (var up = owner; up is not null; up = up.Parent is { } q ? _byKey.GetValueOrDefault(q) : null)
            if (up.Key == node.Key) { Log.Warn(T("Story Manager: a node cannot go under itself")); return; }
        var edits = SiblingChains.Move(Siblings(node).Select(n => n.Key).ToList(), node.Key, parent, owner.Children.Select(n => n.Key).ToList(), after,
            k => _byKey.GetValueOrDefault(k)?.Parent);
        var lookup = new RecordLookup(app.Session!.Workspace, app.Session.Catalog);
        var records = new List<(IMajorRecord, RecordPlace, RefContext?)>();
        foreach (var (key, (newParent, previous)) in edits)
        {
            var current = app.Document.Override(key)?.Record ?? lookup.Resolve(key);
            if (current is null || app.Document.WorkingCopy(key, current) is not AStoryManagerNode copy) continue;
            if (newParent is { } np) copy.Parent.SetTo(np); else copy.Parent.Clear();
            if (previous is { } pv) copy.PreviousSibling.SetTo(pv); else copy.PreviousSibling.Clear();
            records.Add((copy, RecordPlace.TopLevel, null));
        }
        app.Document.CommitEdits(TF($"Move Story Manager node {node.EditorId}"), records);
        Refresh(app);
    }

    /// <summary>Autotests: the first branch or quest node under the first event with two or more moved down one.</summary>
    public (string Before, string After)? MoveDownForTest(EditorApp app)
    {
        Refresh(app);
        var owner = _byKey.Values.Where(n => n.Signature is "SMEN" or "SMBN" && n.Children.Count >= 2).OrderBy(n => n.EditorId, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (owner is null) return null;
        string Order() => string.Join(", ", _byKey[owner.Key].Children.Take(3).Select(c => c.EditorId));
        var before = Order();
        MoveTo(app, owner.Children[0], owner.Key, owner.Children[1].Key);
        return (before, Order());
    }
}
