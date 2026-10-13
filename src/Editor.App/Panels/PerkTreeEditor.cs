using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Data;
using StbEditor.Render;
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
    private static float _aspect = 1.6f;
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
            _skyFit = true;
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
        if (ImGui.Button(T("Fit"))) { _fit = true; _skyFit = true; }
        ImGui.SameLine();
        ImGui.Checkbox(T("Snap to grid"), ref _snap);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Dragged nodes land on whole columns and rows (offsets 0)"));
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextDim, TF($"{nodes.Count} node(s)"));
        ImGui.SameLine();
        if (ImGui.RadioButton(T("Editor"), !_game)) _game = false;
        ImGui.SameLine();
        if (ImGui.RadioButton(T("As in the game"), _game)) _game = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("The tree as the skill menu shows it: take perks by clicking, see which branches open at which skill level"));
        if (_game)
        {
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
            ImGui.SliderInt("##skill", ref _skill, 0, 100, T("skill %d"));
            ImGui.SameLine();
            if (ImGui.SmallButton(T("Reset perks"))) TakenOf(avif.FormKey).Clear();
            ImGui.SameLine();
            if (ImGui.SmallButton(T("Game zoom"))) _skyZoom = 1f;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("The view angle the menu has (fStatsCameraFOV); Fit zooms to the tree and its picture"));
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextDim, TF($"perks taken: {TakenOf(avif.FormKey).Values.Sum()}") + "   " + T("Click: take a rank, Shift+click: give one back. Drag a star: move it. RMB drag: look around, wheel: zoom."));
        }
        else
        {
            if (target is null) ImGui.TextColored(Theme.TextDim, T("Read only here: this record is not edited in the active file."));
            ImGui.TextColored(Theme.TextDim, T("Drag: move a node. Ctrl+click another node: link / unlink. Double-click: open the perk. RMB / MMB drag: pan, wheel: zoom."));
        }

        var avail = ImGui.GetContentRegionAvail();
        var height = _game ? Math.Max(260f, avail.Y - 4) : Math.Max(260f, avail.Y * 0.62f);
        ImGui.BeginChild("##perktree", new Vector2(avail.X, height), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        DrawCanvas(app, avif, nodes, lookup, target);
        ImGui.EndChild();

        if (_game) return;
        if (_selected >= 0 && _selected < nodes.Count)
        {
            var node = nodes[_selected];
            var perk = node.Perk.IsNull ? null : lookup.Resolve<IPerkGetter>(node.Perk.FormKey);
            ImGui.Text(IsRoot(nodes, _selected) ? TF($"Root node #{node.Index}") : TF($"Node #{node.Index}: {Name(perk, node)}"));
            if (perk is not null)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton(T("Open perk"))) app.FormWindows.Open(perk.FormKey);
                if (perk.Description?.String is { Length: > 0 } text) ImGui.TextWrapped(CleanText(text));
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
        if (_game)
        {
            DrawSky(app, avif, nodes, lookup, target, origin, size);
            return;
        }
        ImGui.InvisibleButton("##canvas", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();

        // The root's own grid fields hold leftovers (text bytes in vanilla trees): it is drawn under the perks it leads to.
        var rootAt = RootPlace(nodes);
        Vector2 At(int i) => i == _drag ? _dragAt : IsRoot(nodes, i) ? rootAt : Position(nodes[i]);
        if (_fit && nodes.Count > 0)
        {
            // The tree fills the view: across and up are scaled apart (trees are narrow and tall in grid units).
            var points = Enumerable.Range(0, nodes.Count).Select(At).ToList();
            var low = points.Aggregate(new Vector2(float.MaxValue), Vector2.Min) - new Vector2(0.6f, 0.4f);
            var high = points.Aggregate(new Vector2(float.MinValue), Vector2.Max) + new Vector2(0.6f, 0.6f);
            var span = Vector2.Max(high - low, new Vector2(1));
            var across = size.X / span.X;
            var up = size.Y / span.Y;
            _aspect = Math.Clamp(across / up, 1f, 3.5f);
            _zoom = Math.Clamp(Math.Min(across / _aspect, up) / Unit, 0.15f, 3f);
            var mid = (low + high) / 2;
            _pan = size / 2 - new Vector2(mid.X * _aspect, -mid.Y) * Unit * _zoom;
            _fit = false;
        }
        var scale = Unit * _zoom;
        Vector2 Screen(Vector2 p) => origin + _pan + new Vector2(p.X * _aspect, -p.Y) * scale;
        Vector2 World(Vector2 s) { var v = (s - origin - _pan) / scale; return new Vector2(v.X / _aspect, -v.Y); }

        var byIndex = new Dictionary<uint, int>();
        for (var i = 0; i < nodes.Count; i++) if (nodes[i].Index is { } index) byIndex.TryAdd(index, i);
        var perks = Enumerable.Range(0, nodes.Count).Select(i => nodes[i].Perk.IsNull ? null : lookup.Resolve<IPerkGetter>(nodes[i].Perk.FormKey)).ToArray();
        draw.PushClipRect(origin, origin + size, true);
        var hit = DrawEditor(draw, origin, size, nodes, perks, byIndex, Screen, World);
        draw.PopClipRect();

        {
            if (hovered && hit is { } over && _drag < 0)
            {
                var node = nodes[over];
                string column = $"{node.PerkGridX ?? 0}", acrossText = $"{node.HorizontalPosition ?? 0:0.###}", row = $"{node.PerkGridY ?? 0}", upText = $"{node.VerticalPosition ?? 0:0.###}";
                ImGui.SetTooltip($"#{node.Index} {Name(perks[over], node)}\n{TF($"column {column} + {acrossText}, row {row} + {upText}")}"
                    + (perks[over] is { } p && RequiredLevel(p) is { } level ? "\n" + TF($"skill {level:0}") : ""));
            }

            // Mouse: select, link, drag, open.
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
                        _dragGrab = World(io.MousePos) - _dragAt;
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
                    var at = World(io.MousePos) - _dragGrab;
                    if (!_dragMoved && Vector2.Distance(at, Position(nodes[_drag])) * scale > 3) _dragMoved = true;
                    if (_dragMoved) _dragAt = _snap ? new Vector2(MathF.Round(at.X), MathF.Round(at.Y)) : at;
                }
                else
                {
                    if (_dragMoved && target is not null) MoveNode(target, _drag, _dragAt);
                    _drag = -1;
                }
            }
        }

        // Pan and zoom in both views.
        if (hovered && (ImGui.IsMouseDragging(ImGuiMouseButton.Right, 1) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle, 1))) _pan += io.MouseDelta;
        if (hovered && io.MouseWheel != 0)
        {
            var before = World(io.MousePos);
            _zoom = Math.Clamp(_zoom * (io.MouseWheel > 0 ? 1.15f : 1 / 1.15f), 0.15f, 3f);
            scale = Unit * _zoom;
            // Keep the point under the cursor in place.
            _pan = io.MousePos - origin - new Vector2(before.X * _aspect, -before.Y) * scale;
        }
    }

    /// <summary>The editor's look: a grid, arrows to the nodes each leads to, a plate per node with its name and skill level.</summary>
    private static int? DrawEditor(ImDrawListPtr draw, Vector2 origin, Vector2 size, IReadOnlyList<IActorValuePerkNodeGetter> nodes, IPerkGetter?[] perks,
        Dictionary<uint, int> byIndex, Func<Vector2, Vector2> screen, Func<Vector2, Vector2> world)
    {
        var gridColour = ImGui.GetColorU32(Theme.Alpha(Theme.TextDim, 0.14f));
        var lo = world(origin + new Vector2(0, size.Y));
        var hi = world(origin + new Vector2(size.X, 0));
        for (var x = (int)MathF.Floor(lo.X); x <= hi.X; x++) draw.AddLine(screen(new(x, lo.Y)), screen(new(x, hi.Y)), gridColour);
        for (var y = (int)MathF.Floor(lo.Y); y <= hi.Y; y++) draw.AddLine(screen(new(lo.X, y)), screen(new(hi.X, y)), gridColour);

        // Plates: name on the first line, the skill level it asks for and its ranks on the second.
        var line = ImGui.GetTextLineHeight();
        var plates = new (Vector2 Min, Vector2 Max)[nodes.Count];
        var maxWidth = Math.Clamp(170f * MathF.Sqrt(_zoom), 70f, 220f);
        for (var i = 0; i < nodes.Count; i++)
        {
            var p = screen(i == _drag ? _dragAt : IsRoot(nodes, i) ? RootPlace(nodes) : Position(nodes[i]));
            var name = IsRoot(nodes, i) ? T("(root)") : Name(perks[i], nodes[i]);
            var width = Math.Clamp(ImGui.CalcTextSize(name).X + 12, 46f, maxWidth);
            var half = new Vector2(width / 2, line + 3);
            plates[i] = (p - half, p + half);
        }

        // Links between plate edges, an arrow head at the far one.
        var lineColour = ImGui.GetColorU32(Theme.Alpha(Theme.Text, 0.45f));
        for (var i = 0; i < nodes.Count; i++)
            foreach (var to in nodes[i].ConnectionLineToIndices)
            {
                if (!byIndex.TryGetValue(to, out var j) || j == i) continue;
                var a = (plates[i].Min + plates[i].Max) / 2;
                var b = (plates[j].Min + plates[j].Max) / 2;
                var start = Edge(plates[i], b);
                var end = Edge(plates[j], a);
                var d = end - start;
                if (d.LengthSquared() < 4) continue;
                d = Vector2.Normalize(d);
                var side = new Vector2(-d.Y, d.X);
                var colour = i == _selected || j == _selected ? ImGui.GetColorU32(Theme.Accent) : lineColour;
                draw.AddLine(start, end, colour, 1.4f);
                draw.AddTriangleFilled(end, end - d * 8 + side * 4, end - d * 8 - side * 4, colour);
            }

        int? hit = null;
        var mouse = ImGui.GetIO().MousePos;
        for (var i = 0; i < nodes.Count; i++)
            if (mouse.X >= plates[i].Min.X && mouse.X <= plates[i].Max.X && mouse.Y >= plates[i].Min.Y && mouse.Y <= plates[i].Max.Y) hit = i;
        // The hovered and selected plates on top.
        foreach (var i in Enumerable.Range(0, nodes.Count).OrderBy(i => i == _selected ? 2 : i == hit ? 1 : 0))
        {
            var (min, max) = plates[i];
            var selected = i == _selected;
            var root = IsRoot(nodes, i);
            var fill = selected ? Theme.Alpha(Theme.Accent, 0.32f) : i == hit ? Theme.Alpha(Theme.Text, 0.16f) : Theme.Alpha(Theme.Text, 0.07f);
            // An opaque backdrop first: links under a plate do not show through.
            draw.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.WindowBg) | 0xFF000000u, 4);
            draw.AddRectFilled(min, max, ImGui.GetColorU32(fill), 4);
            var border = selected ? Theme.Accent : perks[i] is null && !root ? Theme.Warning : Theme.Alpha(Theme.Text, 0.45f);
            draw.AddRect(min, max, ImGui.GetColorU32(border), 4);
            var name = root ? T("(root)") : Name(perks[i], nodes[i]);
            draw.PushClipRect(min, max, true);
            draw.AddText(min + new Vector2(6, 2), ImGui.GetColorU32(selected ? Theme.Accent : Theme.Text), name);
            var sub = perks[i] is { } perk ? (RequiredLevel(perk) is { } level ? TF($"skill {level:0}") : "") : "";
            draw.AddText(min + new Vector2(6, 2 + line), ImGui.GetColorU32(Theme.TextDim), sub);
            draw.PopClipRect();
        }
        return hit;
    }

    /// <summary>Where the line from a plate's middle toward <paramref name="toward"/> leaves the plate.</summary>
    private static Vector2 Edge((Vector2 Min, Vector2 Max) plate, Vector2 toward)
    {
        var centre = (plate.Min + plate.Max) / 2;
        var half = (plate.Max - plate.Min) / 2;
        var d = toward - centre;
        if (d.LengthSquared() < 1e-6f) return centre;
        var t = MathF.Min(half.X / MathF.Max(MathF.Abs(d.X), 1e-6f), half.Y / MathF.Max(MathF.Abs(d.Y), 1e-6f));
        return centre + d * MathF.Min(t, 1);
    }

    // ------------------------------------------------------------------ the game's view

    private static bool _game;
    private static int _skill = 100;
    private static readonly Dictionary<FormKey, Dictionary<int, int>> Taken = new();

    private static Dictionary<int, int> TakenOf(FormKey avif) => Taken.TryGetValue(avif, out var t) ? t : Taken[avif] = [];

    /// <summary>The perks of a node's rank chain: the node's perk, then its Next Perk links.</summary>
    private static List<IPerkGetter> Chain(IPerkGetter? perk, RecordLookup lookup)
    {
        var chain = new List<IPerkGetter>();
        var seen = new HashSet<FormKey>();
        for (var p = perk; p is not null && seen.Add(p.FormKey) && chain.Count < 20; p = p.NextPerk.IsNull ? null : lookup.Resolve<IPerkGetter>(p.NextPerk.FormKey)) chain.Add(p);
        return chain;
    }

    /// <summary>A node can be taken when a node leading to it is taken (or it hangs from the root) — the game's rule.</summary>
    private static bool Reachable(IReadOnlyList<IActorValuePerkNodeGetter> nodes, Dictionary<uint, int> byIndex, Dictionary<int, int> taken, int i)
    {
        if (nodes[i].Index is not { } index) return false;
        for (var j = 0; j < nodes.Count; j++)
            if (nodes[j].ConnectionLineToIndices.Contains(index) && (IsRoot(nodes, j) || taken.GetValueOrDefault(j) > 0)) return true;
        return false;
    }

    private static void TakeRank(IReadOnlyList<IActorValuePerkNodeGetter> nodes, IPerkGetter?[] perks, Dictionary<uint, int> byIndex, RecordLookup lookup, int i, bool giveBack)
    {
        if (IsRoot(nodes, i) || perks[i] is null || _lastRecord is not { } avif) return;
        var taken = TakenOf(avif.FormKey);
        var rank = taken.GetValueOrDefault(i);
        var chain = Chain(perks[i], lookup);
        if (giveBack)
        {
            if (rank > 0) taken[i] = rank - 1;
            return;
        }
        if (rank >= chain.Count || !Reachable(nodes, byIndex, taken, i)) return;
        if ((RequiredLevel(chain[rank]) ?? 0) > _skill) return;
        taken[i] = rank + 1;
    }

    /// <summary>
    /// What the menu draws over its sky: lines between the stars (bright between taken ones), a glow on taken stars and a
    /// ring on those that can be taken now, ranks, and the hovered perk's name, ranks, description and what it needs at the
    /// bottom. <paramref name="at"/> are the stars on screen (NaN: behind the camera), <paramref name="radius"/> their size.
    /// </summary>
    private static int? DrawGame(ImDrawListPtr draw, Vector2 origin, Vector2 size, IReadOnlyList<IActorValuePerkNodeGetter> nodes, IPerkGetter?[] perks,
        Dictionary<uint, int> byIndex, RecordLookup lookup, Vector2[] at, float[] radius, IActorValueInformationGetter avif)
    {
        var taken = TakenOf(avif.FormKey);
        bool Shown(int i) => !IsRoot(nodes, i) && !float.IsNaN(at[i].X);

        for (var i = 0; i < nodes.Count; i++)
        {
            if (!Shown(i)) continue;
            foreach (var to in nodes[i].ConnectionLineToIndices)
            {
                if (!byIndex.TryGetValue(to, out var j) || j == i || !Shown(j)) continue;
                var lit = taken.GetValueOrDefault(i) > 0 && taken.GetValueOrDefault(j) > 0;
                // A soft beam: a wide faint stroke under a thin bright one, as the menu's glowing lines.
                draw.AddLine(at[i], at[j], lit ? 0x50FFE8D0u : 0x24FFD8B0u, lit ? 5f : 3.5f);
                draw.AddLine(at[i], at[j], lit ? 0xE0FFF6EEu : 0x80F0DCC0u, lit ? 1.8f : 1.1f);
            }
        }

        int? hit = null;
        var mouse = ImGui.GetIO().MousePos;
        var best = float.MaxValue;
        for (var i = 0; i < nodes.Count; i++)
        {
            if (!Shown(i)) continue;
            var p = at[i];
            var r = Math.Max(radius[i], 3f);
            var distance = Vector2.Distance(mouse, p);
            if (distance < Math.Max(10f, r * 1.6f) && distance < best) { hit = i; best = distance; }
            var rank = taken.GetValueOrDefault(i);
            var chain = Chain(perks[i], lookup);
            var next = rank < chain.Count ? chain[rank] : null;
            var can = next is not null && Reachable(nodes, byIndex, taken, i) && (RequiredLevel(next) ?? 0) <= _skill;
            if (rank > 0)
            {
                // Taken: the star lit up with a white glow.
                for (var g = 4; g >= 1; g--) draw.AddCircleFilled(p, r * (0.6f + g * 0.55f), ((uint)(16 * (5 - g)) << 24) | 0x00FFF4E8u);
                draw.AddCircleFilled(p, r * 0.45f, 0xFFFFFFFFu);
            }
            else if (can) draw.AddCircle(p, r * 1.25f, 0x90FFD0A0u, 0, 1.2f);
            else draw.AddCircleFilled(p, r * 0.9f, 0x70000000u);
            if (hit == i) draw.AddCircle(p, r * 1.7f + 2, 0xC0FFFFFFu, 0, 1.2f);
            if (chain.Count > 1 && rank > 0) draw.AddText(p + new Vector2(r + 4, -r - 6), 0xC0FFFFFFu, $"{rank}/{chain.Count}");
        }

        // The panel under the sky, as the menu has it.
        var show = hit ?? (_selected >= 0 && _selected < nodes.Count && !IsRoot(nodes, _selected) ? _selected : null);
        var title = avif.Name?.String ?? avif.EditorID ?? "";
        draw.AddText(origin + new Vector2(size.X / 2 - ImGui.CalcTextSize(title).X / 2, 8), 0xFFFFFFFFu, title);
        if (show is { } shown && perks[shown] is not null)
        {
            var rank = taken.GetValueOrDefault(shown);
            var chain = Chain(perks[shown], lookup);
            var current = chain[Math.Clamp(rank == 0 ? 0 : rank - 1, 0, chain.Count - 1)];
            var next = rank < chain.Count ? chain[rank] : null;
            var lines = new List<(string Text, uint Colour)>
            {
                ((current.Name?.String ?? current.EditorID ?? "") + (chain.Count > 1 ? TF($"   rank {rank}/{chain.Count}") : ""), 0xFFFFFFFFu),
                (CleanText(current.Description?.String ?? ""), 0xFFD8D8D8u),
            };
            if (next is not null && RequiredLevel(next) is { } need)
                lines.Add((TF($"Requires {title}: {need:0}") + (need > _skill ? TF($"  (now {_skill})") : ""), need > _skill ? 0xFF6060E0u : 0xFFA0A0A0u));
            if (next is not null && !Reachable(nodes, byIndex, taken, shown)) lines.Add((T("Take a perk that leads here first"), 0xFF6060E0u));
            var y = origin.Y + size.Y - 8 - lines.Count * ImGui.GetTextLineHeightWithSpacing();
            draw.AddRectFilled(new Vector2(origin.X, y - 6), origin + size, 0xB0000000u);
            foreach (var (text, colour) in lines)
            {
                var w = ImGui.CalcTextSize(text).X;
                draw.AddText(new Vector2(origin.X + MathF.Max(8, size.X / 2 - w / 2), y), colour, text);
                y += ImGui.GetTextLineHeightWithSpacing();
            }
        }
        return hit;
    }

    private static ViewportTarget? _skyTarget;
    private static readonly Camera SkyCamera = new() { Position = Vector3.Zero, Near = 0.05f };
    private static bool _skyFit = true;
    private static float _skyZoom = 1f;
    private static int _press = -1;
    private static Vector2 _pressAt;
    private static string _skyNote = "";

    /// <summary>
    /// The game's own perk menu, rebuilt: its skydome NIF (nebulae, star fields, the skill's constellation picture) drawn from the
    /// middle of the dome as the menu's camera sees it (fStatsCameraFOV across), the perk stars (INTPerkStars01.nif) where the
    /// menu puts them, lines and states on top. Click takes a rank (Shift+click gives one back); in the active file a star can
    /// be dragged along the tree's slope to fit the picture. RMB drag looks around, wheel zooms, Fit goes back.
    /// </summary>
    private static void DrawSky(EditorApp app, IActorValueInformationGetter avif, IReadOnlyList<IActorValuePerkNodeGetter> nodes, RecordLookup lookup,
        EditTarget? target, Vector2 origin, Vector2 size)
    {
        var session = app.Session!;
        var io = ImGui.GetIO();
        ImGui.InvisibleButton("##sky", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        if (app.Preview is not { } preview || PerkSky.Dome(session) is not { } dome)
        {
            draw.AddText(origin + new Vector2(8, 8), ImGui.GetColorU32(Theme.Warning), TF($"no {PerkSky.Skydome} in the build"));
            return;
        }
        var settings = PerkSky.Read(session, lookup);
        var (point, slot, known) = PerkSky.Place(dome, avif);
        var width = PerkSky.Width(nodes);
        Vector3 World(int i) => point.Apply(PerkSky.Local(i == _drag ? _dragAt : Position(nodes[i]), width, settings));

        // The menu's camera: in the middle of the dome, turned to the tree and its picture; the game's view angle across.
        var aspect = size.X / size.Y;
        var across = settings.Fov * MathF.PI / 180f;
        SkyCamera.Position = Vector3.Zero;
        SkyCamera.FieldOfView = Math.Clamp(2 * MathF.Atan(MathF.Tan(across / 2) / aspect) * _skyZoom, 0.05f, 2.6f);
        if (_skyFit)
        {
            var points = Enumerable.Range(0, nodes.Count).Where(i => !IsRoot(nodes, i)).Select(World).ToList();
            if (PerkSky.Picture(dome, slot) is { } picture) { points.Add(picture.Min); points.Add(picture.Max); }
            if (points.Count == 0) points.Add(point.Apply(new Vector3(0, 0, settings.ZInitialOffset)));
            SkyCamera.LookAt(points.Aggregate(Vector3.Zero, (a, b) => a + Vector3.Normalize(b)) / points.Count);
            // Zoomed (the view angle only: the camera stays where the game's is, so stars sit on the picture as in the game)
            // until the tree and its picture fill the view.
            var reach = 0f;
            foreach (var p in points)
            {
                var forward = Vector3.Dot(p, SkyCamera.Forward);
                if (forward <= 1e-3f) continue;
                reach = Math.Max(reach, Math.Max(MathF.Abs(Vector3.Dot(p, SkyCamera.Up)), MathF.Abs(Vector3.Dot(p, SkyCamera.Right)) / aspect) / forward);
            }
            var gameAngle = 2 * MathF.Atan(MathF.Tan(across / 2) / aspect);
            _skyZoom = reach > 0 ? Math.Clamp(2 * MathF.Atan(reach * 1.15f) / gameAngle, 0.15f, 2.2f) : 1f;
            SkyCamera.FieldOfView = Math.Clamp(gameAngle * _skyZoom, 0.05f, 2.6f);
            _skyFit = false;
        }

        // The dome and a star per perk, as the menu builds them.
        var scene = new StbEditor.Render.Scene { Title = "perks", IsInterior = true, Lighting = ObjectPreview.Studio };
        scene.Objects.Add(PerkSky.Object(PerkSky.Skydome, Vector3.Zero, 1f));
        for (var i = 0; i < nodes.Count; i++)
            if (!IsRoot(nodes, i)) scene.Objects.Add(PerkSky.Object(PerkSky.Star, World(i), settings.StarScale));
        foreach (var obj in scene.Objects)
        {
            var model = preview.Renderer.Meshes.Get(obj.ModelPath!, out var state);
            if (state == LoadState.Ready && model is not null) { obj.Model = model; obj.UpdateBounds(); }
        }
        _skyTarget ??= new ViewportTarget(app.Graphics.Device);
        _skyTarget.EnsureSize((int)size.X, (int)size.Y);
        preview.Render(scene, SkyCamera, _skyTarget.ColorView!, _skyTarget.DepthView!, _skyTarget.Width, _skyTarget.Height, new Vector4(0, 0, 0, 1));
        unsafe
        {
            draw.AddImage(new ImTextureRef(null, new ImTextureID((ulong)_skyTarget.ShaderView!.NativePointer)), origin, origin + size);
        }

        // Stars on screen, with their drawn size.
        var viewProjection = SkyCamera.View * SkyCamera.Projection(aspect);
        Vector2 Screen(Vector3 p)
        {
            var c = Vector4.Transform(new Vector4(p, 1), viewProjection);
            if (c.W <= 1e-4f) return new Vector2(float.NaN);
            return origin + new Vector2((c.X / c.W + 1) * 0.5f * size.X, (1 - c.Y / c.W) * 0.5f * size.Y);
        }
        var at = new Vector2[nodes.Count];
        var radius = new float[nodes.Count];
        // The star's bright core is about a third of its plane.
        var starRadius = 8.69f * settings.StarScale * 0.35f;
        for (var i = 0; i < nodes.Count; i++)
        {
            if (IsRoot(nodes, i)) { at[i] = new Vector2(float.NaN); continue; }
            var p = World(i);
            at[i] = Screen(p);
            radius[i] = float.IsNaN(at[i].X) ? 0 : Vector2.Distance(at[i], Screen(p + SkyCamera.Up * starRadius));
        }
        var byIndex = new Dictionary<uint, int>();
        for (var i = 0; i < nodes.Count; i++) if (nodes[i].Index is { } index) byIndex.TryAdd(index, i);
        var perks = Enumerable.Range(0, nodes.Count).Select(i => nodes[i].Perk.IsNull ? null : lookup.Resolve<IPerkGetter>(nodes[i].Perk.FormKey)).ToArray();
        draw.PushClipRect(origin, origin + size, true);
        var hit = DrawGame(draw, origin, size, nodes, perks, byIndex, lookup, at, radius, avif);
        _skyNote = (known ? TF($"slot {slot}: Point{slot:00} / cImage{slot:00}") : TF($"not one of the menu's 18 skills: shown in slot {slot}"))
            + "   " + TF($"width {width}");
        draw.AddText(origin + new Vector2(8, 8), ImGui.GetColorU32(known ? Theme.TextDim : Theme.Warning), _skyNote);
        draw.PopClipRect();

        // Click: take a rank (Shift: give back). Drag (active file): move the star along the slope.
        Vector3 Ray(Vector2 mouse)
        {
            var ndc = new Vector2((mouse.X - origin.X) / size.X * 2 - 1, 1 - (mouse.Y - origin.Y) / size.Y * 2);
            var tanY = MathF.Tan(SkyCamera.FieldOfView / 2);
            return Vector3.Normalize(SkyCamera.Forward + SkyCamera.Right * ndc.X * tanY * aspect + SkyCamera.Up * ndc.Y * tanY);
        }
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _press = hit ?? -1;
            _pressAt = io.MousePos;
            if (hit is { } pressed) _selected = pressed;
        }
        if (_press >= 0 && _press < nodes.Count)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (_drag < 0 && target is not null && Vector2.Distance(io.MousePos, _pressAt) > 3) { _drag = _press; _dragAt = Position(nodes[_press]); }
                if (_drag >= 0 && PerkSky.GridAt(Vector3.Zero, Ray(io.MousePos), point, width, settings) is { } grid)
                    _dragAt = _snap ? new Vector2(MathF.Round(grid.X), MathF.Round(grid.Y)) : grid;
            }
            else
            {
                if (_drag >= 0 && target is not null) MoveNode(target, _drag, _dragAt);
                else if (hovered && hit == _press) TakeRank(nodes, perks, byIndex, lookup, _press, io.KeyShift);
                _drag = -1;
                _press = -1;
            }
        }
        else _press = -1;
        if (hovered && hit is { } over && _drag < 0)
        {
            var node = nodes[over];
            string column = $"{node.PerkGridX ?? 0}", acrossText = $"{node.HorizontalPosition ?? 0:0.###}", row = $"{node.PerkGridY ?? 0}", upText = $"{node.VerticalPosition ?? 0:0.###}";
            ImGui.SetTooltip($"#{node.Index} {Name(perks[over], node)}\n{TF($"column {column} + {acrossText}, row {row} + {upText}")}");
        }
        if (_drag >= 0) ImGui.SetTooltip(TF($"column + offset {_dragAt.X:0.###}, row + offset {_dragAt.Y:0.###}"));
        if (hovered && ImGui.IsMouseDragging(ImGuiMouseButton.Right, 1)) SkyCamera.Rotate(io.MouseDelta.X * 0.003f * _skyZoom, -io.MouseDelta.Y * 0.003f * _skyZoom);
        if (hovered && io.MouseWheel != 0) _skyZoom = Math.Clamp(_skyZoom * (io.MouseWheel > 0 ? 1 / 1.12f : 1.12f), 0.15f, 2.2f);
    }

    /// <summary>Autotests: the game's view framed afresh; the note it shows (slot, width) and where node 1's star is, in world space.</summary>
    public static string SkyForTest(EditorSession session)
    {
        _skyFit = true;
        if (_lastRecord is not { } avif || _lookup is not { } lookup || PerkSky.Dome(session) is not { } dome) return "no tree / dome";
        var settings = PerkSky.Read(session, lookup);
        var (point, slot, known) = PerkSky.Place(dome, avif);
        var nodes = avif.PerkTree;
        var width = PerkSky.Width(nodes);
        var first = Enumerable.Range(0, nodes.Count).FirstOrDefault(i => !IsRoot(nodes, i), -1);
        var star = first < 0 ? Vector3.Zero : point.Apply(PerkSky.Local(Position(nodes[first]), width, settings));
        var back = first < 0 ? null : PerkSky.GridAt(Vector3.Zero, Vector3.Normalize(star), point, width, settings);
        return $"slot {slot} known {known} width {width} settings {settings} node {first} star {star} back {back} (grid {(first < 0 ? Vector2.Zero : Position(nodes[first]))})";
    }

    /// <summary>A description as the menu shows it: markup tags (&lt;font&gt;, &lt;span&gt;…) taken out.</summary>
    public static string CleanText(string text) =>
        System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(text, "<(Global|Mag|Dur)=?([^>]*)>", "$2"), "<[^>]+>", "").Trim();

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

    /// <summary>Autotests: the game's view with the first <paramref name="count"/> perks that can be taken taken, at a skill level.</summary>
    public static string GameViewForTest(int count, int skill)
    {
        _game = true;
        _skill = skill;
        if (_lastRecord is not { } avif || _lookup is not { } lookup) return "no tree";
        var nodes = avif.PerkTree;
        var byIndex = new Dictionary<uint, int>();
        for (var i = 0; i < nodes.Count; i++) if (nodes[i].Index is { } index) byIndex.TryAdd(index, i);
        var perks = Enumerable.Range(0, nodes.Count).Select(i => nodes[i].Perk.IsNull ? null : lookup.Resolve<IPerkGetter>(nodes[i].Perk.FormKey)).ToArray();
        var taken = TakenOf(avif.FormKey);
        taken.Clear();
        for (var round = 0; round < count; round++)
        {
            var before = taken.Values.Sum();
            for (var i = 0; i < nodes.Count && taken.Values.Sum() == before; i++) TakeRank(nodes, perks, byIndex, lookup, i, false);
            if (taken.Values.Sum() == before) break;
        }
        return $"{taken.Values.Sum()} rank(s) taken at skill {skill}: {string.Join(", ", taken.Where(t => t.Value > 0).Select(t => $"{perks[t.Key]?.EditorID} {t.Value}"))}";
    }
}
