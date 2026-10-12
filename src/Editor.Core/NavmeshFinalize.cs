using System.Numerics;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace StbEditor.Core;

/// <summary>
/// What the Creation Kit's "Finalize navmesh" writes, as read from the game's own data:
/// <list type="bullet">
/// <item>door triangles — the triangle under a load door's arrival point (the other door's teleport
/// marker) gets the Door flag and an entry (door, constant <see cref="DoorHash"/>); the door's XNDP
/// points at it;</item>
/// <item>edge links — an open edge that lies on another navmesh's edge (cell borders, several
/// navmeshes in one cell) links to it both ways (type 0);</item>
/// <item>the navmesh's entry in the navmesh info map (NAVI): a point on the mesh, its cell or
/// worldspace grid, its doors. Each plugin's NAVI holds entries for its own navmeshes; the game merges
/// them (Update.esm and Dawnguard.esm carry only theirs).</item>
/// </list>
/// </summary>
public static class NavmeshFinalize
{
    /// <summary>The value every door triangle and NAVI linked door carries in vanilla data.</summary>
    public const int DoorHash = unchecked((int)0xE48B73F3);

    /// <summary>Every navmesh's CrcHash and NAVI Unknown2 in vanilla data.</summary>
    public const uint NavmeshCrc = 0xA5E9A03C;

    /// <summary>A load door of the navmesh's cell and the point where one arrives through it (in this cell).</summary>
    public readonly record struct Door(FormKey Key, Vector3 Arrival);

    private static Vector3 V(P3Float p) => new(p.X, p.Y, p.Z);

    private static (Vector3 A, Vector3 B, Vector3 C) Corners(INavigationMeshDataGetter data, int t)
    {
        var v = data.Triangles[t].Vertices;
        return (V(data.Vertices[v.X]), V(data.Vertices[v.Y]), V(data.Vertices[v.Z]));
    }

    /// <summary>The triangle under a point (2D containment, the closest in height), else the nearest centre within <paramref name="maxDistance"/>; -1 when none.</summary>
    public static int TriangleAt(INavigationMeshDataGetter data, Vector3 point, float maxDistance = 256f)
    {
        var best = -1;
        var bestScore = float.MaxValue;
        for (var t = 0; t < data.Triangles.Count; t++)
        {
            var (a, b, c) = Corners(data, t);
            if (!Contains2D(a, b, c, point)) continue;
            var dz = Math.Abs((a.Z + b.Z + c.Z) / 3 - point.Z);
            if (dz < bestScore) { bestScore = dz; best = t; }
        }
        if (best >= 0 && bestScore < 512) return best;
        best = -1;
        bestScore = maxDistance;
        for (var t = 0; t < data.Triangles.Count; t++)
        {
            var (a, b, c) = Corners(data, t);
            var d = Vector3.Distance((a + b + c) / 3, point);
            if (d < bestScore) { bestScore = d; best = t; }
        }
        return best;
    }

    private static bool Contains2D(Vector3 a, Vector3 b, Vector3 c, Vector3 p)
    {
        static float Cross(Vector3 o, Vector3 u, Vector3 w) => (u.X - o.X) * (w.Y - o.Y) - (u.Y - o.Y) * (w.X - o.X);
        var d1 = Cross(a, b, p);
        var d2 = Cross(b, c, p);
        var d3 = Cross(c, a, p);
        var negative = d1 < 0 || d2 < 0 || d3 < 0;
        var positive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(negative && positive);
    }

    /// <summary>
    /// Door triangles of one navmesh: each door whose arrival point lies on it gets its triangle (Door
    /// flag, entry); entries and flags of doors that are not there any more go. Returns door → triangle.
    /// </summary>
    public static Dictionary<FormKey, int> LinkDoors(NavigationMesh mesh, IEnumerable<Door> doors)
    {
        var data = mesh.Data ?? throw new InvalidOperationException("navmesh without data");
        var result = new Dictionary<FormKey, int>();
        foreach (var door in doors)
        {
            var t = TriangleAt(data, door.Arrival);
            if (t >= 0) result[door.Key] = t;
        }
        data.DoorTriangles.Clear();
        foreach (var (door, t) in result.OrderBy(r => r.Value))
        {
            var entry = new DoorTriangle { TriangleBeforeDoor = (short)t, Unknown = DoorHash };
            entry.Door.SetTo(door);
            data.DoorTriangles.Add(entry);
        }
        var doorTriangles = result.Values.ToHashSet();
        for (var t = 0; t < data.Triangles.Count; t++)
        {
            if (doorTriangles.Contains(t)) data.Triangles[t].Flags |= NavmeshTriangle.Flag.Door;
            else data.Triangles[t].Flags &= ~NavmeshTriangle.Flag.Door;
        }
        return result;
    }

    private static NavmeshTriangle.Flag EdgeFlag(int edge) => (NavmeshTriangle.Flag)(1 << edge);

    private static short GetEdge(INavmeshTriangleGetter t, int edge) => edge switch { 0 => t.EdgeLink_0_1, 1 => t.EdgeLink_1_2, _ => t.EdgeLink_2_0 };

    private static void SetEdge(NavmeshTriangle t, int edge, short value)
    {
        switch (edge)
        {
            case 0: t.EdgeLink_0_1 = value; break;
            case 1: t.EdgeLink_1_2 = value; break;
            default: t.EdgeLink_2_0 = value; break;
        }
    }

    private static (Vector3 A, Vector3 B) EdgeEnds(INavigationMeshDataGetter data, int t, int edge)
    {
        var v = data.Triangles[t].Vertices;
        int[] corners = [v.X, v.Y, v.Z];
        return (V(data.Vertices[corners[edge]]), V(data.Vertices[corners[(edge + 1) % 3]]));
    }

    /// <summary>
    /// How much two edges overlap along one line (2D): 0 unless the other edge's ends lie within a few units
    /// of this edge's line, the shared part is a real stretch, and the heights there agree.
    /// </summary>
    private static float Overlap(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        var ab = new Vector2(b.X - a.X, b.Y - a.Y);
        var length = ab.Length();
        if (length < 0.5f) return 0;
        var dir = ab / length;
        var normal = new Vector2(-dir.Y, dir.X);
        float Along(Vector3 p) => Vector2.Dot(new Vector2(p.X - a.X, p.Y - a.Y), dir);
        float Across(Vector3 p) => Math.Abs(Vector2.Dot(new Vector2(p.X - a.X, p.Y - a.Y), normal));
        if (Across(c) > 8f || Across(d) > 8f) return 0;
        float sc = Along(c), sd = Along(d);
        var from = Math.Max(0, Math.Min(sc, sd));
        var to = Math.Min(length, Math.Max(sc, sd));
        var overlap = to - from;
        var other = Math.Abs(sd - sc);
        if (overlap < 2f || overlap < 0.25f * Math.Min(length, other)) return 0;
        // Heights at the middle of the shared stretch.
        var mid = (from + to) / 2;
        var zHere = a.Z + (b.Z - a.Z) * (mid / length);
        var zThere = Math.Abs(sd - sc) < 0.5f ? c.Z : c.Z + (d.Z - c.Z) * ((mid - sc) / (sd - sc));
        return Math.Abs(zHere - zThere) <= 64f ? overlap : 0;
    }

    /// <summary>
    /// Edge links between navmeshes. Every edge of the <paramref name="finalize"/> meshes that has no neighbour
    /// in its own mesh is linked to the edge of another mesh lying on it (both ways); an external link whose
    /// target is loaded but no longer matches is cleared; links into navmeshes that are not loaded stay.
    /// Returns the meshes that changed (targets included).
    /// </summary>
    public static HashSet<FormKey> LinkEdges(IReadOnlyDictionary<FormKey, NavigationMesh> meshes, IReadOnlySet<FormKey> finalize)
    {
        var changed = new HashSet<FormKey>();
        // Every boundary edge of every loaded mesh, by a coarse cell of its midpoint.
        var byCell = new Dictionary<(int, int), List<(FormKey Mesh, int Triangle, int Edge, Vector3 A, Vector3 B)>>();
        static (int, int) Bucket(Vector3 a, Vector3 b) => ((int)MathF.Floor((a.X + b.X) / 2 / 256), (int)MathF.Floor((a.Y + b.Y) / 2 / 256));
        foreach (var (key, mesh) in meshes)
        {
            if (mesh.Data is not { } data) continue;
            for (var t = 0; t < data.Triangles.Count; t++)
            {
                var tri = data.Triangles[t];
                for (var e = 0; e < 3; e++)
                {
                    if (!tri.Flags.HasFlag(EdgeFlag(e)) && GetEdge(tri, e) >= 0) continue; // inside its own mesh
                    var (a, b) = EdgeEnds(data, t, e);
                    var bucket = Bucket(a, b);
                    if (!byCell.TryGetValue(bucket, out var list)) byCell[bucket] = list = [];
                    list.Add((key, t, e, a, b));
                }
            }
        }

        // The CK links edges on one line that overlap; their ends need not meet (vertices at a cell border
        // are a few units apart on each side). The candidate with the longest overlap wins.
        // A neighbour edge already linked to another triangle of this mesh that still lies on it keeps that
        // link: edges pair one to one (the CK leaves the second edge open).
        bool TakenByOther(FormKey self, int t, (FormKey Mesh, int Triangle, int Edge, Vector3 A, Vector3 B) other)
        {
            var otherData = meshes[other.Mesh].Data!;
            var tri = otherData.Triangles[other.Triangle];
            var index = GetEdge(tri, other.Edge);
            if (!tri.Flags.HasFlag(EdgeFlag(other.Edge)) || index < 0 || index >= otherData.EdgeLinks.Count) return false;
            var link = otherData.EdgeLinks[index];
            if (link.Mesh.FormKey == self && link.TriangleIndex == t) return false;
            // Linked to a mesh that is not loaded: cannot tell, keep it.
            if (!meshes.TryGetValue(link.Mesh.FormKey, out var target) || target.Data is not { } targetData) return true;
            if (link.TriangleIndex < 0 || link.TriangleIndex >= targetData.Triangles.Count) return false;
            for (var e = 0; e < 3; e++)
            {
                var (a, b) = EdgeEnds(targetData, link.TriangleIndex, e);
                if (Overlap(a, b, other.A, other.B) > 0) return true;
            }
            return false;
        }

        (FormKey Mesh, int Triangle, int Edge)? Match(FormKey self, int t, Vector3 a, Vector3 b)
        {
            var (bx, by) = Bucket(a, b);
            (FormKey, int, int)? best = null;
            var bestOverlap = 0f;
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    if (byCell.TryGetValue((bx + dx, by + dy), out var list))
                        foreach (var other in list)
                        {
                            if (other.Mesh == self) continue;
                            var overlap = Overlap(a, b, other.A, other.B);
                            if (overlap > bestOverlap && TakenByOther(self, t, other)) continue;
                            if (overlap > bestOverlap) { bestOverlap = overlap; best = (other.Mesh, other.Triangle, other.Edge); }
                        }
            return best;
        }

        void Link(FormKey from, int t, int e, FormKey to, int u)
        {
            var data = meshes[from].Data!;
            var tri = data.Triangles[t];
            if (tri.Flags.HasFlag(EdgeFlag(e)) && GetEdge(tri, e) is var current && current >= 0 && current < data.EdgeLinks.Count
                && data.EdgeLinks[current].Mesh.FormKey == to && data.EdgeLinks[current].TriangleIndex == u)
                return;
            var link = new EdgeLink { Unknown = 0, TriangleIndex = (short)u };
            link.Mesh.SetTo(to);
            data.EdgeLinks.Add(link);
            tri.Flags |= EdgeFlag(e);
            SetEdge(tri, e, (short)(data.EdgeLinks.Count - 1));
            changed.Add(from);
        }

        foreach (var key in finalize)
        {
            if (!meshes.TryGetValue(key, out var mesh) || mesh.Data is not { } data) continue;
            for (var t = 0; t < data.Triangles.Count; t++)
            {
                for (var e = 0; e < 3; e++)
                {
                    var tri = data.Triangles[t];
                    var external = tri.Flags.HasFlag(EdgeFlag(e));
                    if (!external && GetEdge(tri, e) >= 0) continue;
                    var (a, b) = EdgeEnds(data, t, e);
                    if (Match(key, t, a, b) is { } match)
                    {
                        Link(key, t, e, match.Mesh, match.Triangle);
                        Link(match.Mesh, match.Triangle, match.Edge, key, t);
                        continue;
                    }
                    // No edge here any more: a link into a loaded navmesh is stale; into an unloaded one it stays.
                    if (external && GetEdge(tri, e) is var index && index >= 0 && index < data.EdgeLinks.Count
                        && meshes.ContainsKey(data.EdgeLinks[index].Mesh.FormKey))
                    {
                        tri.Flags &= ~EdgeFlag(e);
                        SetEdge(tri, e, -1);
                        changed.Add(key);
                    }
                }
            }
        }
        foreach (var key in changed) CompactEdgeLinks(meshes[key]);
        return changed;
    }

    /// <summary>Drops edge links no triangle uses and renumbers the ones in use.</summary>
    public static void CompactEdgeLinks(NavigationMesh mesh)
    {
        var data = mesh.Data!;
        var used = new List<int>();
        var map = new Dictionary<int, int>();
        foreach (var tri in data.Triangles)
            for (var e = 0; e < 3; e++)
                if (tri.Flags.HasFlag(EdgeFlag(e)) && GetEdge(tri, e) is var i && i >= 0 && i < data.EdgeLinks.Count && !map.ContainsKey(i))
                {
                    map[i] = used.Count;
                    used.Add(i);
                }
        var links = used.Select(i => data.EdgeLinks[i]).ToList();
        data.EdgeLinks.Clear();
        data.EdgeLinks.AddRange(links);
        foreach (var tri in data.Triangles)
            for (var e = 0; e < 3; e++)
                if (tri.Flags.HasFlag(EdgeFlag(e)))
                    SetEdge(tri, e, map.TryGetValue(GetEdge(tri, e), out var to) ? (short)to : (short)-1);
    }

    /// <summary>A point on the mesh near its middle (the centre of the triangle closest to the vertices' average).</summary>
    public static P3Float Point(INavigationMeshDataGetter data)
    {
        if (data.Triangles.Count == 0) return new P3Float();
        var average = data.Vertices.Aggregate(Vector3.Zero, (sum, v) => sum + V(v)) / Math.Max(data.Vertices.Count, 1);
        var best = Enumerable.Range(0, data.Triangles.Count).MinBy(t => { var (a, b, c) = Corners(data, t); return Vector3.DistanceSquared((a + b + c) / 3, average); });
        var (x, y, z) = Corners(data, best);
        var centre = (x + y + z) / 3;
        return new P3Float(centre.X, centre.Y, centre.Z);
    }

    /// <summary>
    /// The navmesh's NAVI entry after a finalize: <paramref name="existing"/> (merges, island…) kept, point,
    /// parent and doors set; a new entry otherwise.
    /// </summary>
    public static NavigationMapInfo Info(INavigationMeshGetter mesh, INavigationMapInfoGetter? existing, FormKey cell, FormKey? world, P2Int? grid, IEnumerable<FormKey> doors)
    {
        var info = existing?.DeepCopy() ?? new NavigationMapInfo { Unknown = 0, Unknown2 = unchecked((int)NavmeshCrc) };
        info.NavigationMesh.SetTo(mesh.FormKey);
        if (mesh.Data is { } data) info.Point = Point(data);
        if (world is { } w && grid is { } g)
        {
            var parent = new NavigationMapInfoWorldParent { ParentWorldspaceCoord = new P2Int16((short)g.X, (short)g.Y) };
            parent.ParentWorldspace.SetTo(w);
            info.Parent = parent;
        }
        else
        {
            var parent = new NavigationMapInfoCellParent();
            parent.ParentCell.SetTo(cell);
            info.Parent = parent;
        }
        info.LinkedDoors.Clear();
        foreach (var door in doors)
        {
            var linked = new LinkedDoor { Unknown = DoorHash };
            linked.Door.SetTo(door);
            info.LinkedDoors.Add(linked);
        }
        return info;
    }
}
