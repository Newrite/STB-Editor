using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Ui;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// The perk tree of a skill (AVIF «Perk tree» tab; CK: Actor Values → Perk Tree) as the game draws it: each node at
/// grid column / row plus its fine offset, lines to the nodes it leads to, the perk's name and the skill level its
/// conditions ask for. Drag moves a node, Ctrl+click on another node links or unlinks it, double-click opens the
/// perk; the selected node's fields are below. Every change is one undo step.
/// </summary>
internal static class PerkTreeEditor
{
    public static bool Draws(FormTab tab) => tab.Title == "Perk tree";

    private static FormKey _key;
    private static int _selected = -1;
    private static int _drag = -1;
    private static Vector2 _dragAt;
    private static Vector2 _dragGrab;
    private static bool _dragMoved;
    private static Vector2 _pan;
    private static float _zoom = 1f;
    private static bool _fit = true;
    private static bool _snap;
    private static RecordLookup? _lookup;
    private static object? _lookupFor;
    private static EditTarget? _lastTarget;
    private static IActorValueInformationGetter? _lastRecord;

    /// <summary>Grid units → pixels at zoom 1.</summary>
    private const float Unit = 90f;

    /// <summary>A node's place in grid units: column / row plus the offsets (rows go up, as in the game's menu).</summary>
    public static Vector2 Position(IActorValuePerkNodeGetter node)
        => new((node.PerkGridX ?? 0) + (node.HorizontalPosition ?? 0), (node.PerkGridY ?? 0) + (node.VerticalPosition ?? 0));

    /// <summary>The fields for a place: the column / row nearest to it and what is left as offsets (none with snapping).</summary>
    public static (uint GridX, uint GridY, float X, float Y) Place(Vector2 at, bool snap)
    {
        var gx = (uint)Math.Max(0, MathF.Round(at.X));
        var gy = (uint)Math.Max(0, MathF.Round(at.Y));
        return snap ? (gx, gy, 0, 0) : (gx, gy, Round(at.X - gx), Round(at.Y - gy));
        static float Round(float v) => MathF.Round(v * 1000) / 1000;
    }

    /// <summary>The skill level a perk's conditions ask for (GetBaseActorValue / GetActorValue ≥ N), if any.</summary>
    public static float? RequiredLevel(IPerkGetter perk)
    {
        foreach (var c in perk.Conditions.OfType<IConditionFloatGetter>())
            if (c.CompareOperator is CompareOperator.GreaterThanOrEqualTo or CompareOperator.GreaterThan
                && c.Data is IGetBaseActorValueConditionDataGetter or IGetActorValueConditionDataGetter)
                return c.ComparisonValue;
        return null;
    }

    public static void Draw(EditorApp app, IActorValueInformationGetter avif, FormTab tab, EditTarget? target)
    {
        var session = app.Session!;
        if (!ReferenceEquals(_lookupFor, session)) { _lookup = new RecordLookup(session.Workspace, session.Catalog); _lookupFor = session; }
        var lookup = _lookup!;
        if (_key != avif.FormKey)
        {
            _key = avif.FormKey;
            _selected = -1;
            _drag = -1;
            _fit = true;
        }
        var nodes = avif.PerkTree;
        if (_selected >= nodes.Count) _selected = -1;
        _lastTarget = target;
        _lastRecord = avif;

        // Toolbar.
        ImGui.BeginDisabled(target is null);
        if (ImGui.Button(nodes.Count == 0 ? T("Add root node") : T("Add node"))) AddNode(avif, target!);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(T("A new node in the middle of the view; with a node selected it is linked from it. Choose its perk below."));
        ImGui.SameLine();
        ImGui.BeginDisabled(_selected < 0 || IsRoot(nodes, _selected));
        if (ImGui.Button(T("Delete node"))) DeleteNode(target!, nodes[_selected]);
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(T("Fit"))) _fit = true;
        ImGui.SameLine();
        ImGui.Checkbox(T("Snap to grid"), ref _snap);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Dragged nodes land on whole columns and rows (offsets 0)"));
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextDim, TF($"{nodes.Count} node(s)"));
        if (target is null) ImGui.TextColored(Theme.TextDim, T("Read only here: this record is not edited in the active file."));
        ImGui.TextColored(Theme.TextDim, T("Drag: move a node. Ctrl+click another node: link / unlink. Double-click: open the perk. RMB / MMB drag: pan, wheel: zoom."));

        var avail = ImGui.GetContentRegionAvail();
        var height = Math.Max(260f, avail.Y * 0.62f);
        ImGui.BeginChild("##perktree", new Vector2(avail.X, height), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        DrawCanvas(app, avif, nodes, lookup, target);
        ImGui.EndChild();

        if (_selected >= 0 && _selected < nodes.Count)
        {
            var node = nodes[_selected];
            var perk = node.Perk.IsNull ? null : lookup.Resolve<IPerkGetter>(node.Perk.FormKey);
            ImGui.Text(IsRoot(nodes, _selected) ? TF($"Root node #{node.Index}") : TF($"Node #{node.Index}: {Name(perk, node)}"));
            if (perk is not null)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton(T("Open perk"))) app.FormWindows.Open(perk.FormKey);
                if (perk.Description?.String is { Length: > 0 } text) ImGui.TextWrapped(text);
                var ranks = Ranks(perk, lookup);
                if (ranks > 1) ImGui.TextColored(Theme.TextDim, TF($"{ranks} ranks (Next Perk chain)"));
            }
            FormView.DrawObject(app, node, ["Perk", "PerkGridX", "PerkGridY", "HorizontalPosition", "VerticalPosition", "ConnectionLineToIndices", "AssociatedSkill", "Index", "FNAM"],
                ["PerkTree", _selected], target);
        }
        else ImGui.TextColored(Theme.TextDim, T("Click a node to see its fields."));
    }

    private static void DrawCanvas(EditorApp app, IActorValueInformationGetter avif, IReadOnlyList<IActorValuePerkNodeGetter> nodes, RecordLookup lookup, EditTarget? target)
    {
        var io = ImGui.GetIO();
        var origin = ImGui.GetCursorScreenPos();
        var size = ImGui.GetContentRegionAvail();
        if (size.X < 10 || size.Y < 10) return;
        ImGui.InvisibleButton("##canvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();

        // The root's own grid fields hold leftovers (text bytes in vanilla trees): it is drawn under the perks it leads to.
        var rootAt = RootPlace(nodes);
        Vector2 At(int i) => i == _drag ? _dragAt : IsRoot(nodes, i) ? rootAt : Position(nodes[i]);
        if (_fit && nodes.Count > 0)
        {
            var points = Enumerable.Range(0, nodes.Count).Select(At).ToList();
            var low = points.Aggregate(new Vector2(float.MaxValue), Vector2.Min) - new Vector2(0.7f);
            var high = points.Aggregate(new Vector2(float.MinValue), Vector2.Max) + new Vector2(0.7f);
            var span = Vector2.Max(high - low, new Vector2(1));
            _zoom = Math.Clamp(Math.Min(size.X / span.X, size.Y / span.Y) / Unit, 0.2f, 3f);
            var mid = (low + high) / 2;
            _pan = size / 2 - new Vector2(mid.X, -mid.Y) * Unit * _zoom;
            _fit = false;
        }
        var scale = Unit * _zoom;
        Vector2 Screen(Vector2 p) => origin + _pan + new Vector2(p.X, -p.Y) * scale;
        Vector2 World(Vector2 s) { var v = (s - origin - _pan) / scale; return new Vector2(v.X, -v.Y); }

        // Grid: whole columns and rows.
        draw.PushClipRect(origin, origin + size, true);
        var gridColour = ImGui.GetColorU32(Theme.Alpha(Theme.TextDim, 0.18f));
        var lo = World(origin + new Vector2(0, size.Y));
        var hi = World(origin + new Vector2(size.X, 0));
        for (var x = (int)MathF.Floor(lo.X); x <= hi.X; x++) draw.AddLine(Screen(new(x, lo.Y)), Screen(new(x, hi.Y)), gridColour);
        for (var y = (int)MathF.Floor(lo.Y); y <= hi.Y; y++) draw.AddLine(Screen(new(lo.X, y)), Screen(new(hi.X, y)), gridColour);

        // Links: a line from a node to each node it leads to, with an arrow head at the far end.
        var byIndex = new Dictionary<uint, int>();
        for (var i = 0; i < nodes.Count; i++) if (nodes[i].Index is { } index) byIndex.TryAdd(index, i);
        var radius = Math.Clamp(11f * _zoom, 5f, 16f);
        var lineColour = ImGui.GetColorU32(Theme.Alpha(Theme.Text, 0.55f));
        for (var i = 0; i < nodes.Count; i++)
            foreach (var to in nodes[i].ConnectionLineToIndices)
            {
                if (!byIndex.TryGetValue(to, out var j) || j == i) continue;
                Vector2 a = Screen(At(i)), b = Screen(At(j));
                var d = b - a;
                if (d.Length() < radius * 2) continue;
                d = Vector2.Normalize(d);
                var tip = b - d * radius;
                var side = new Vector2(-d.Y, d.X);
                var colour = i == _selected || j == _selected ? ImGui.GetColorU32(Theme.Accent) : lineColour;
                draw.AddLine(a + d * radius, tip, colour, 1.5f);
                draw.AddTriangleFilled(tip, tip - d * 8 + side * 4, tip - d * 8 - side * 4, colour);
            }

        // Nodes and names.
        int? hit = null;
        var mouse = io.MousePos;
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var p = Screen(At(i));
            var root = IsRoot(nodes, i);
            if (Vector2.Distance(mouse, p) <= radius + 3) hit = i;
            var perk = node.Perk.IsNull ? null : lookup.Resolve<IPerkGetter>(node.Perk.FormKey);
            var fill = ImGui.GetColorU32(i == _selected ? Theme.Accent : root ? Theme.Alpha(Theme.TextDim, 0.5f) : perk is null && !root ? Theme.Warning : Theme.Alpha(Theme.Text, 0.85f));
            if (root) draw.AddRectFilled(p - new Vector2(radius * 0.8f), p + new Vector2(radius * 0.8f), fill);
            else draw.AddCircleFilled(p, radius, fill);
            draw.AddCircle(p, radius + 1, ImGui.GetColorU32(Theme.Alpha(Theme.Text, 0.9f)), 0, 1f);
            if (_zoom > 0.3f || i == _selected || hit == i)
            {
                var label = root ? T("(root)") : Name(perk, node);
                if (perk is not null && RequiredLevel(perk) is { } level) label += $"  {level:0}";
                var width = ImGui.CalcTextSize(label).X;
                draw.AddText(p + new Vector2(-width / 2, radius + 2), ImGui.GetColorU32(i == _selected ? Theme.Accent : Theme.Text), label);
            }
        }
        draw.PopClipRect();

        if (hovered && hit is { } over && _drag < 0)
        {
            var node = nodes[over];
            var perk = node.Perk.IsNull ? null : lookup.Resolve<IPerkGetter>(node.Perk.FormKey);
            string column = $"{node.PerkGridX ?? 0}", across = $"{node.HorizontalPosition ?? 0:0.###}", row = $"{node.PerkGridY ?? 0}", up = $"{node.VerticalPosition ?? 0:0.###}";
            ImGui.SetTooltip($"#{node.Index} {Name(perk, node)}\n{TF($"column {column} + {across}, row {row} + {up}")}"
                + (perk is not null && RequiredLevel(perk) is { } level ? "\n" + TF($"skill {level:0}") : ""));
        }

        // Mouse: select, link, drag, open; pan and zoom.
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (hit is { } clicked)
            {
                if (io.KeyCtrl && _selected >= 0 && _selected != clicked && target is not null) ToggleLink(target, nodes, _selected, clicked);
                else
                {
                    _selected = clicked;
                    _drag = target is null || IsRoot(nodes, clicked) ? -1 : clicked;
                    _dragAt = Position(nodes[clicked]);
                    _dragGrab = World(mouse) - _dragAt;
                    _dragMoved = false;
                }
            }
            else _selected = -1;
        }
        if (hovered && hit is { } opened && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !nodes[opened].Perk.IsNull)
        {
            app.FormWindows.Open(nodes[opened].Perk.FormKey);
            _drag = -1;
        }
        if (_drag >= 0 && _drag < nodes.Count)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var at = World(mouse) - _dragGrab;
                if (!_dragMoved && Vector2.Distance(at, Position(nodes[_drag])) * scale > 3) _dragMoved = true;
                if (_dragMoved) _dragAt = _snap ? new Vector2(MathF.Round(at.X), MathF.Round(at.Y)) : at;
            }
            else
            {
                if (_dragMoved && target is not null) MoveNode(target, _drag, _dragAt);
                _drag = -1;
            }
        }
        if (hovered && (ImGui.IsMouseDragging(ImGuiMouseButton.Right, 1) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 1))) _pan += io.MouseDelta;
        if (hovered && io.MouseWheel != 0)
        {
            var before = World(mouse);
            _zoom = Math.Clamp(_zoom * (io.MouseWheel > 0 ? 1.15f : 1 / 1.15f), 0.2f, 3f);
            scale = Unit * _zoom;
            // Keep the point under the cursor in place.
            _pan = mouse - origin - new Vector2(before.X, -before.Y) * scale;
        }
    }

    /// <summary>Where the root is drawn: a row under the perks it leads to (all perks when it leads nowhere).</summary>
    private static Vector2 RootPlace(IReadOnlyList<IActorValuePerkNodeGetter> nodes)
    {
        var root = Enumerable.Range(0, nodes.Count).FirstOrDefault(i => IsRoot(nodes, i), -1);
        var perks = Enumerable.Range(0, nodes.Count).Where(i => i != root).ToList();
        if (perks.Count == 0) return Vector2.Zero;
        var children = root >= 0 ? perks.Where(i => nodes[i].Index is { } index && nodes[root].ConnectionLineToIndices.Contains(index)).ToList() : [];
        var under = children.Count > 0 ? children : perks;
        var x = under.Average(i => Position(nodes[i]).X);
        var y = perks.Min(i => Position(nodes[i]).Y) - 1;
        return new Vector2((float)x, y);
    }

    private static bool IsRoot(IReadOnlyList<IActorValuePerkNodeGetter> nodes, int i) => nodes[i].Perk.IsNull && (nodes[i].Index ?? 0) == 0;

    private static string Name(IPerkGetter? perk, IActorValuePerkNodeGetter node)
        => perk is null ? node.Perk.IsNull ? T("(no perk)") : node.Perk.FormKey.ToString() : perk.Name?.String is { Length: > 0 } name ? name : perk.EditorID ?? perk.FormKey.ToString();

    /// <summary>How many ranks a perk has: itself and its Next Perk chain.</summary>
    private static int Ranks(IPerkGetter perk, RecordLookup lookup)
    {
        var seen = new HashSet<FormKey> { perk.FormKey };
        var current = perk;
        while (!current.NextPerk.IsNull && seen.Add(current.NextPerk.FormKey) && lookup.Resolve<IPerkGetter>(current.NextPerk.FormKey) is { } next) current = next;
        return seen.Count;
    }

    private static ActorValuePerkNode NodeOf(Mutagen.Bethesda.Plugins.Records.IMajorRecord record, int i) => ((ActorValueInformation)record).PerkTree[i];

    public static void MoveNode(EditTarget target, int i, Vector2 at)
    {
        var (gx, gy, x, y) = Place(at, _snap);
        target.Apply(T("Move perk node"), r =>
        {
            var node = NodeOf(r, i);
            node.PerkGridX = gx;
            node.PerkGridY = gy;
            node.HorizontalPosition = x;
            node.VerticalPosition = y;
        });
    }

    public static void ToggleLink(EditTarget target, IReadOnlyList<IActorValuePerkNodeGetter> nodes, int from, int to)
    {
        if (nodes[to].Index is not { } index) return;
        var linked = nodes[from].ConnectionLineToIndices.Contains(index);
        target.Apply(linked ? T("Unlink perk nodes") : T("Link perk nodes"), r =>
        {
            var list = NodeOf(r, from).ConnectionLineToIndices;
            if (linked) list.RemoveAll(v => v == index);
            else list.Add(index);
        });
    }

    /// <summary>A new node in the middle of the view (the first one is the tree's root), linked from the selected node.</summary>
    public static void AddNode(IActorValueInformationGetter avif, EditTarget target)
    {
        var nodes = avif.PerkTree;
        var index = nodes.Count == 0 ? 0u : nodes.Max(n => n.Index ?? 0) + 1;
        var skill = nodes.FirstOrDefault(n => !n.AssociatedSkill.IsNull)?.AssociatedSkill.FormKey ?? avif.FormKey;
        var from = _selected >= 0 && _selected < nodes.Count ? nodes[_selected].Index : null;
        var at = nodes.Count == 0 ? new Vector2(2, 0)
            : _selected >= 0 && _selected < nodes.Count ? Position(nodes[_selected]) + new Vector2(0, 1)
            : nodes.Select(Position).Aggregate(Vector2.Zero, (a, b) => a + b) / nodes.Count;
        var (gx, gy, x, y) = Place(at, true);
        var root = nodes.Count == 0;
        target.Apply(root ? T("Add root perk node") : T("Add perk node"), r =>
        {
            var node = new ActorValuePerkNode
            {
                Index = index,
                PerkGridX = gx,
                PerkGridY = gy,
                HorizontalPosition = x,
                VerticalPosition = y,
                // Perk nodes carry 1 (as an int), the root 1.0 (as a float) — as in the game's trees.
                FNAM = root ? new byte[] { 0, 0, 0x80, 0x3F } : new byte[] { 1, 0, 0, 0 },
            };
            node.AssociatedSkill.SetTo(skill);
            var tree = ((ActorValueInformation)r).PerkTree;
            if (from is { } parent && tree.FirstOrDefault(n => n.Index == parent) is { } owner) owner.ConnectionLineToIndices.Add(index);
            tree.Add(node);
        });
        _selected = nodes.Count;
    }

    /// <summary>Removes a node and every link to it.</summary>
    public static void DeleteNode(EditTarget target, IActorValuePerkNodeGetter doomed)
    {
        var index = doomed.Index;
        target.Apply(T("Delete perk node"), r =>
        {
            var tree = ((ActorValueInformation)r).PerkTree;
            var at = tree.FindIndex(n => n.Index == index);
            if (at < 0) return;
            tree.RemoveAt(at);
            if (index is { } i) foreach (var n in tree) n.ConnectionLineToIndices.RemoveAll(v => v == i);
        });
        _selected = -1;
    }

    /// <summary>
    /// Autotests, as a person would with the drawn tree: node 1 dragged half a column right and up, a link from node 1
    /// to node 2 toggled, a node added under node 1. Returns false when no tree is drawn for editing.
    /// </summary>
    public static bool EditForTest()
    {
        if (_lastTarget is not { } target || _lastRecord is not { PerkTree.Count: > 2 } avif) return false;
        MoveNode(target, 1, Position(avif.PerkTree[1]) + new Vector2(0.5f, 0.5f));
        ToggleLink(target, avif.PerkTree, 1, 2);
        _selected = 1;
        AddNode(avif, target);
        return true;
    }
}
