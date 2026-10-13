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
/// World → Idle animations (CK: Gameplay → Animations): every idle by behaviour graph and the action it hangs from,
/// children in the order the game tries them (the first whose conditions pass plays). Drag an idle onto another to put
/// it right after that one (Ctrl: as its last child) or onto an action; the context menu moves it up, down, in, out and
/// adds new idles. Each move is one undo step over every idle whose parent / previous sibling changes. A click opens
/// the idle's form (event, conditions, flags).
/// </summary>
internal sealed class IdleTreeWindow
{
    private bool _open;
    private List<IdleNode> _roots = [];
    private Dictionary<FormKey, IdleNode> _byKey = [];
    private int _builtFor = -1;
    private object? _builtSession;
    private string _filter = "";
    private int _graph;
    private string[] _graphs = [];
    private FormKey? _selected;
    private RecordLookup? _lookup;

    public IReadOnlyList<IdleNode> Roots => _roots;
    public IReadOnlyDictionary<FormKey, IdleNode> Nodes => _byKey;

    public void Show() => _open = true;

    /// <summary>The idle as it is now: the editor's edit of it, else the winning version.</summary>
    private IIdleAnimationGetter? Resolve(EditorApp app, FormKey key)
        => app.Document.Override(key)?.Record as IIdleAnimationGetter ?? _lookup!.Resolve<IIdleAnimationGetter>(key);

    public void Rebuild(EditorApp app)
    {
        if (app.Session is not { } session) return;
        if (!ReferenceEquals(_builtSession, session)) { _lookup = new RecordLookup(session.Workspace, session.Catalog); _builtSession = session; }
        var keys = (session.Catalog.BySignature.GetValueOrDefault("IDLE") ?? []).Select(r => r.FormKey)
            .Concat(app.Document.Drafts.Values.OfType<OverrideDraft>().Where(d => d.Record is IIdleAnimationGetter).Select(d => d.Key))
            .Distinct();
        _roots = IdleTree.Build(keys, k => Resolve(app, k));
        _byKey = IdleTree.Index(_roots);
        _graphs = [T("All graphs"), .. _roots.Select(r => r.Behavior).Distinct().OrderBy(g => g, StringComparer.OrdinalIgnoreCase).Select(g => g.Length == 0 ? T("(no graph)") : g)];
        _graph = Math.Min(_graph, _graphs.Length - 1);
        _builtFor = app.Document.Version;
    }

    public void Draw(EditorApp app)
    {
        if (!_open || app.Session is not { } session) return;
        if (_builtFor != app.Document.Version || !ReferenceEquals(_builtSession, session)) Rebuild(app);
        ImGui.SetNextWindowSize(app.Scaled(700, 720), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Idle animations") + "###idletree", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 14);
            ImGui.InputTextWithHint("##idlefilter", T("Filter: idle or event"), ref _filter, 128);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 18);
            ImGui.Combo("##idlegraph", ref _graph, _graphs, _graphs.Length);
            ImGui.TextColored(Theme.TextDim, TF($"{_byKey.Count:N0} idles. Children in the order the game tries them: the first whose conditions pass plays."));
            ImGui.TextColored(Theme.TextDim, T("Click: open. Drag onto an idle: after it (Ctrl: as its last child), onto an action: first under it. Right click: move, new idle."));
            if (ImGui.BeginChild("##idletreelist"))
            {
                var graphs = _roots.GroupBy(r => r.Behavior).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
                foreach (var graph in graphs)
                {
                    var name = graph.Key.Length == 0 ? T("(no graph)") : graph.Key;
                    if (_graph > 0 && _graphs[_graph] != name) continue;
                    var shown = graph.Where(Matches).ToList();
                    if (shown.Count == 0) continue;
                    var flags = ImGuiTreeNodeFlags.SpanAvailWidth | (_filter.Length > 0 || _graph > 0 ? ImGuiTreeNodeFlags.DefaultOpen : 0);
                    if (!ImGui.TreeNodeEx($"{name}  ({graph.Sum(Count)})##graph{name}", flags)) continue;
                    foreach (var action in shown.GroupBy(r => r.Parent))
                        DrawAction(app, session, action.Key, action.ToList());
                    ImGui.TreePop();
                }
            }
            ImGui.EndChild();
        }
        ImGui.End();
    }

    private static int Count(IdleNode n) => 1 + n.Children.Sum(Count);

    private bool Matches(IdleNode node) =>
        _filter.Length == 0
        || (node.EditorId?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || node.Event.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        || node.Children.Any(Matches);

    private void DrawAction(EditorApp app, EditorSession session, FormKey? action, List<IdleNode> roots)
    {
        var name = action is { } a ? session.Catalog.Get(a)?.EditorId ?? session.Workspace.FormIdText(a) : T("(no action)");
        var flags = ImGuiTreeNodeFlags.SpanAvailWidth | (_filter.Length > 0 ? ImGuiTreeNodeFlags.DefaultOpen : 0);
        var open = ImGui.TreeNodeEx($"{name}  [{T("action")}]  ({roots.Sum(Count)})##action{action}", flags);
        if (ImGui.BeginDragDropTarget())
        {
            if (Dropped() is { } moved && _byKey.TryGetValue(moved, out var node)) MoveTo(app, node, action, null);
            ImGui.EndDragDropTarget();
        }
        if (!open) return;
        foreach (var root in roots) DrawIdle(app, session, root);
        ImGui.TreePop();
    }

    private void DrawIdle(EditorApp app, EditorSession session, IdleNode node)
    {
        if (!Matches(node)) return;
        var label = $"{node.EditorId ?? session.Workspace.FormIdText(node.Key)}"
            + (node.Event.Length > 0 ? $"   {node.Event}" : "")
            + (node.Conditions > 0 ? TF($"   {node.Conditions} condition(s)") : "")
            + (app.Document.Override(node.Key) is not null ? "  *" : "");
        var leaf = node.Children.Count == 0;
        var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnArrow | (leaf ? ImGuiTreeNodeFlags.Leaf : 0)
            | (_filter.Length > 0 ? ImGuiTreeNodeFlags.DefaultOpen : 0) | (_selected == node.Key ? ImGuiTreeNodeFlags.Selected : 0);
        var open = ImGui.TreeNodeEx($"{label}##{node.Key}", flags);
        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
        {
            _selected = node.Key;
            app.FormWindows.Open(node.Key);
        }
        if (ImGui.IsItemHovered() && node.Flags != 0) ImGui.SetTooltip(node.Flags.ToString());
        if (ImGui.BeginDragDropSource())
        {
            unsafe
            {
                var id = node.Key.ToString();
                var bytes = System.Text.Encoding.UTF8.GetBytes(id);
                fixed (byte* p = bytes) ImGui.SetDragDropPayload("STB_IDLE", p, (nuint)bytes.Length);
            }
            ImGui.Text(node.EditorId ?? node.Key.ToString());
            ImGui.EndDragDropSource();
        }
        if (ImGui.BeginDragDropTarget())
        {
            if (Dropped() is { } moved && moved != node.Key && _byKey.TryGetValue(moved, out var dragged))
            {
                if (ImGui.GetIO().KeyCtrl) MoveTo(app, dragged, node.Key, node.Children.LastOrDefault(c => c.Key != moved)?.Key);
                else MoveTo(app, dragged, node.Parent, node.Key);
            }
            ImGui.EndDragDropTarget();
        }
        if (ImGui.BeginPopupContextItem($"##idlectx{node.Key}"))
        {
            DrawMenu(app, node);
            ImGui.EndPopup();
        }
        if (!open) return;
        foreach (var child in node.Children) DrawIdle(app, session, child);
        ImGui.TreePop();
    }

    private static FormKey? Dropped()
    {
        unsafe
        {
            var payload = ImGui.AcceptDragDropPayload("STB_IDLE");
            if (payload.Handle == null || payload.Data == null) return null;
            var text = System.Text.Encoding.UTF8.GetString((byte*)payload.Data, payload.DataSize);
            return FormKey.TryFactory(text, out var key) ? key : null;
        }
    }

    private void DrawMenu(EditorApp app, IdleNode node)
    {
        var siblings = IdleTree.Siblings(_roots, node, _byKey);
        var at = siblings.ToList().FindIndex(s => s.Key == node.Key);
        if (ImGui.MenuItem(T("Open"))) app.FormWindows.Open(node.Key);
        ImGui.Separator();
        if (ImGui.MenuItem(T("Move up"), (string?)null, false, at > 0))
            MoveTo(app, node, node.Parent, at > 1 ? siblings[at - 2].Key : null);
        if (ImGui.MenuItem(T("Move down"), (string?)null, false, at >= 0 && at + 1 < siblings.Count))
            MoveTo(app, node, node.Parent, siblings[at + 1].Key);
        if (ImGui.MenuItem(T("Into the idle above (last child)"), (string?)null, false, at > 0))
            MoveTo(app, node, siblings[at - 1].Key, siblings[at - 1].Children.LastOrDefault()?.Key);
        var parent = node.Parent is { } p && _byKey.TryGetValue(p, out var owner) ? owner : null;
        if (ImGui.MenuItem(T("Out of its parent (after it)"), (string?)null, false, parent is not null))
            MoveTo(app, node, parent!.Parent, parent.Key);
        ImGui.Separator();
        if (ImGui.MenuItem(T("New idle after this one"))) NewIdle(app, node.Parent, node.Key, node.Behavior);
        if (ImGui.MenuItem(T("New child idle (first)"))) NewIdle(app, node.Key, null, "");
    }

    /// <summary>Puts <paramref name="node"/> under <paramref name="parent"/> right after <paramref name="after"/> (null: first): one undo step.</summary>
    public void MoveTo(EditorApp app, IdleNode node, FormKey? parent, FormKey? after)
    {
        if (parent == node.Key || (parent is { } p && IsUnder(p, node.Key))) { Log.Warn(T("An idle cannot go under itself")); return; }
        var oldSiblings = IdleTree.Siblings(_roots, node, _byKey);
        var newSiblings = parent is { } q && _byKey.TryGetValue(q, out var owner) ? owner.Children : _roots.Where(r => r.Parent == parent).ToList();
        var edits = IdleTree.Move(oldSiblings, node, parent, newSiblings, after);
        if (edits.TryGetValue(node.Key, out var self) && self.Parent == node.Parent && self.Previous == node.Previous && edits.Count == 1) return;
        Commit(app, TF($"Move idle {node.EditorId}"), edits, null);
    }

    private bool IsUnder(FormKey key, FormKey ancestor)
    {
        var seen = new HashSet<FormKey>();
        for (var at = key; _byKey.TryGetValue(at, out var n) && seen.Add(at);)
        {
            if (n.Parent is not { } up) return false;
            if (up == ancestor) return true;
            at = up;
        }
        return false;
    }

    /// <summary>A new idle of the active file at a place in the tree (the record after it now follows it); its form opens.</summary>
    public FormKey? NewIdle(EditorApp app, FormKey? parent, FormKey? after, string behavior)
    {
        if (app.ActivePlugin is not { } active || app.Session is not { } session)
        {
            app.ShowActiveFileDialog(T("A new idle goes into the active file — choose it first."));
            return null;
        }
        var id = "NewIdle";
        for (var n = 2; session.Catalog.ByKey.Values.Any(r => string.Equals(r.EditorId, id, StringComparison.OrdinalIgnoreCase)); n++) id = $"NewIdle{n}";
        var idle = (IdleAnimation)NewRecords.Blank(typeof(IdleAnimation), active.Allocate(), session.Game.Release, id);
        if (behavior.Length > 0) idle.Filename = behavior;
        var siblings = parent is { } p && _byKey.TryGetValue(p, out var owner) ? owner.Children : _roots.Where(r => r.Parent == parent).ToList();
        var node = new IdleNode(idle.FormKey, id, "", behavior, 0, 0, null, null);
        var edits = IdleTree.Move([], node, parent, siblings, after);
        Commit(app, TF($"New idle {id}"), edits, idle);
        _selected = idle.FormKey;
        app.FormWindows.Open(idle.FormKey);
        return idle.FormKey;
    }

    private void Commit(EditorApp app, string name, Dictionary<FormKey, (FormKey? Parent, FormKey? Previous)> edits, IdleAnimation? created)
    {
        var records = new List<(IMajorRecord, RecordPlace, RefContext?)>();
        foreach (var (key, (parent, previous)) in edits)
        {
            IdleAnimation idle;
            if (created is not null && key == created.FormKey) idle = created;
            else if (Resolve(app, key) is { } current) idle = (IdleAnimation)app.Document.WorkingCopy(key, current);
            else continue;
            idle.RelatedIdles.Clear();
            idle.RelatedIdles.Add(parent is { } p ? new FormLink<IIdleRelationGetter>(p) : FormLink<IIdleRelationGetter>.Null);
            idle.RelatedIdles.Add(previous is { } v ? new FormLink<IIdleRelationGetter>(v) : FormLink<IIdleRelationGetter>.Null);
            records.Add((idle, RecordPlace.TopLevel, null));
        }
        app.Document.CommitEdits(name, records);
        Log.Info($"{name}: {records.Count} idle(s) changed");
    }

    /// <summary>Autotests: which parent was used and how its first children were ordered.</summary>
    public sealed record TestCase(FormKey ParentKey, string Parent, string Before);

    /// <summary>Autotests: the first child of the first idle with three or more children moved down one, as the menu does.</summary>
    public TestCase? MoveDownForTest(EditorApp app)
    {
        var parent = _byKey.Values.Where(n => n.Children.Count >= 3).OrderBy(n => n.EditorId, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (parent is null) return null;
        var test = new TestCase(parent.Key, parent.EditorId ?? parent.Key.ToString(), Order(parent));
        MoveTo(app, parent.Children[0], parent.Key, parent.Children[1].Key);
        return test;
    }

    public string OrderForTest(TestCase test) => _byKey.TryGetValue(test.ParentKey, out var parent) ? Order(parent) : "(gone)";

    private static string Order(IdleNode parent) => string.Join(", ", parent.Children.Take(4).Select(c => c.EditorId));
}
