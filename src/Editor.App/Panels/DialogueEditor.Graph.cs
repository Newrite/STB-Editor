using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Ui;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// The quest's dialogue as a graph (CK: Dialogue Views): a node per topic, laid out in columns from the topics a conversation
/// starts with (branch starting topics and topics nothing leads to; topics with no links at all in the last column), an arrow where a line of one topic leads to another
/// (Link To). Click selects the topic, double-click opens it in the list; RMB / MMB drag pans, wheel zooms.
/// </summary>
internal static partial class DialogueEditor
{
    private static bool _graph;
    private static Vector2 _graphPan = new(20, 20);
    private static float _graphZoom = 1f;
    private static FormKey? _graphFor;

    private sealed record GraphNode(IDialogTopicGetter Topic, int Column, int Row, string Branch, int Lines);

    private static void DrawGraph(EditorApp app, IQuestGetter quest, List<IDialogTopicGetter> topics, List<IDialogBranchGetter> branches)
    {
        if (_graphFor != quest.FormKey) { _graphFor = quest.FormKey; _graphPan = new(20, 20); _graphZoom = 1f; }
        var keys = topics.Select(t => t.FormKey).ToHashSet();
        var lines = topics.ToDictionary(t => t.FormKey, t => Lines(app, t.FormKey));
        var edges = new Dictionary<(FormKey From, FormKey To), int>();
        foreach (var (from, list) in lines)
            foreach (var (line, _) in list)
                foreach (var link in line.LinkTo)
                    if (!link.IsNull && keys.Contains(link.FormKey) && link.FormKey != from)
                        edges[(from, link.FormKey)] = edges.GetValueOrDefault((from, link.FormKey)) + 1;
        var branchOf = new Dictionary<FormKey, string>();
        foreach (var topic in topics)
            if (!topic.Branch.IsNull && branches.FirstOrDefault(b => b.FormKey == topic.Branch.FormKey) is { } b) branchOf[topic.FormKey] = b.EditorID ?? "";

        // Columns by distance from the starts (breadth first); what no start reaches goes after.
        // Topics no line leads to or from (greetings, combat barks…) go into the last column, not among the starts.
        bool Linked(FormKey k) => edges.Keys.Any(e => e.From == k || e.To == k);
        var starts = branches.Select(b => b.StartingTopic.FormKeyNullable).OfType<FormKey>().Where(k => keys.Contains(k) && Linked(k))
            .Concat(topics.Select(t => t.FormKey).Where(k => Linked(k) && edges.Keys.All(e => e.To != k))).Distinct().ToList();
        var column = new Dictionary<FormKey, int>();
        var queue = new Queue<FormKey>();
        foreach (var s in starts) { column[s] = 0; queue.Enqueue(s); }
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            foreach (var (from, to) in edges.Keys.Where(e => e.From == at))
                if (!column.ContainsKey(to)) { column[to] = column[at] + 1; queue.Enqueue(to); }
        }
        var last = column.Count > 0 ? column.Values.Max() + 1 : 0;
        foreach (var t in topics) column.TryAdd(t.FormKey, last);
        var nodes = new Dictionary<FormKey, GraphNode>();
        foreach (var group in topics.GroupBy(t => column[t.FormKey]).OrderBy(g => g.Key))
        {
            var row = 0;
            foreach (var t in group.OrderBy(t => branchOf.GetValueOrDefault(t.FormKey), StringComparer.OrdinalIgnoreCase).ThenBy(t => t.EditorID, StringComparer.OrdinalIgnoreCase))
                nodes[t.FormKey] = new GraphNode(t, group.Key, row++, branchOf.GetValueOrDefault(t.FormKey) ?? "", lines[t.FormKey].Count);
        }

        ImGui.TextColored(Theme.TextDim, TF($"{topics.Count} topic(s), {edges.Count} link(s). Columns: how many steps from where a conversation starts. Click: select, double-click: edit in the list."));
        var origin = ImGui.GetCursorScreenPos();
        var size = Vector2.Max(ImGui.GetContentRegionAvail(), new Vector2(200, 200));
        ImGui.InvisibleButton("##graph", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        var hovered = ImGui.IsItemHovered();
        var io = ImGui.GetIO();
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(origin, origin + size, true);
        var nodeSize = new Vector2(230, 46) * _graphZoom;
        var gap = new Vector2(90, 22) * _graphZoom;
        Vector2 Place(GraphNode n) => origin + _graphPan + new Vector2(n.Column * (nodeSize.X + gap.X), n.Row * (nodeSize.Y + gap.Y));
        var selected = SelectedTopic.TryGetValue(quest.FormKey, out var s0) ? s0 : (FormKey?)null;

        // Arrows first, under the nodes.
        foreach (var ((from, to), count) in edges)
        {
            if (!nodes.TryGetValue(from, out var a) || !nodes.TryGetValue(to, out var b)) continue;
            var start = Place(a) + new Vector2(nodeSize.X, nodeSize.Y / 2);
            var end = Place(b) + new Vector2(0, nodeSize.Y / 2);
            var back = b.Column <= a.Column;
            var colour = ImGui.GetColorU32(from == selected || to == selected ? Theme.Accent : Theme.Alpha(Theme.Text, back ? 0.3f : 0.55f));
            var bend = MathF.Max(40 * _graphZoom, MathF.Abs(end.X - start.X) / 2);
            draw.AddBezierCubic(start, start + new Vector2(bend, 0), end - new Vector2(bend, 0), end, colour, count > 1 ? 2.2f : 1.4f);
            draw.AddTriangleFilled(end, end - new Vector2(8, 4) * _graphZoom, end - new Vector2(8, -4) * _graphZoom, colour);
        }

        FormKey? hit = null;
        foreach (var node in nodes.Values)
        {
            var p = Place(node);
            var isSelected = node.Topic.FormKey == selected;
            draw.AddRectFilled(p, p + nodeSize, ImGui.GetColorU32(isSelected ? Theme.Alpha(Theme.Accent, 0.35f) : Theme.Alpha(Theme.Text, 0.08f)), 4);
            draw.AddRect(p, p + nodeSize, ImGui.GetColorU32(isSelected ? Theme.Accent : Theme.Alpha(Theme.Text, 0.4f)), 4);
            if (_graphZoom > 0.45f)
            {
                var title = node.Topic.EditorID ?? app.Session!.Workspace.FormIdText(node.Topic.FormKey);
                draw.AddText(p + new Vector2(6, 3), ImGui.GetColorU32(Theme.Text), Short(title, (int)(30 * _graphZoom)));
                var first = lines[node.Topic.FormKey].FirstOrDefault().Line;
                var sub = (node.Branch.Length > 0 ? $"[{node.Branch}] " : "") + TF($"{node.Lines} line(s)") + (first is not null ? " · " + FirstText(first) : "");
                draw.AddText(p + new Vector2(6, 3 + ImGui.GetTextLineHeight()), ImGui.GetColorU32(Theme.TextDim), Short(sub, (int)(34 * _graphZoom)));
            }
            var mouse = io.MousePos;
            if (mouse.X >= p.X && mouse.Y >= p.Y && mouse.X <= p.X + nodeSize.X && mouse.Y <= p.Y + nodeSize.Y) hit = node.Topic.FormKey;
        }
        draw.PopClipRect();

        if (hovered && hit is { } over)
        {
            ImGui.SetTooltip(string.Join("\n", lines[over].Take(6).Select(l => "• " + Short(FirstText(l.Line), 80))) + (lines[over].Count > 6 ? "\n…" : ""));
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) SelectedTopic[quest.FormKey] = over;
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) { SelectedTopic[quest.FormKey] = over; _graph = false; }
        }
        if (hovered && (ImGui.IsMouseDragging(ImGuiMouseButton.Right, 1) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 1))) _graphPan += io.MouseDelta;
        if (hovered && io.MouseWheel != 0)
        {
            var before = (io.MousePos - origin - _graphPan) / _graphZoom;
            _graphZoom = Math.Clamp(_graphZoom * (io.MouseWheel > 0 ? 1.15f : 1 / 1.15f), 0.25f, 2.5f);
            _graphPan = io.MousePos - origin - before * _graphZoom;
        }
    }

    /// <summary>Autotests: show the graph instead of the list.</summary>
    public static void ShowGraphForTest(bool on) => _graph = on;
}
