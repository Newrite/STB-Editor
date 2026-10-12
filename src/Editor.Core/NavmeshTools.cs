using System.Numerics;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace StbEditor.Core;

/// <summary>
/// Geometry upkeep for edited navmeshes (NAVM): neighbour links between triangles that share an
/// edge, bounds and the triangle lookup grid the game uses — what the Creation Kit recomputes
/// after manual edits. Links to other navmeshes (edge links) are kept where the edge still exists.
/// </summary>
public static class NavmeshTools
{
    /// <summary>
    /// Lookup grid: divisor × divisor cells over the mesh bounds; per cell a ushort count and the
    /// ushort indices of the triangles whose 2D bounds touch the cell (row by row).
    /// </summary>
    public static byte[] BuildGrid(IReadOnlyList<P3Float> vertices, IReadOnlyList<INavmeshTriangleGetter> triangles, uint divisor,
        Vector2 min, float cellX, float cellY)
    {
        var cells = new List<ushort>[divisor * divisor];
        for (var i = 0; i < cells.Length; i++) cells[i] = [];

        for (var t = 0; t < triangles.Count; t++)
        {
            var tri = triangles[t];
            var a = vertices[tri.Vertices.X];
            var b = vertices[tri.Vertices.Y];
            var c = vertices[tri.Vertices.Z];
            var minX = Math.Min(a.X, Math.Min(b.X, c.X));
            var maxX = Math.Max(a.X, Math.Max(b.X, c.X));
            var minY = Math.Min(a.Y, Math.Min(b.Y, c.Y));
            var maxY = Math.Max(a.Y, Math.Max(b.Y, c.Y));

            // The game's own test differs by a triangle here and there; a cell listing a triangle too
            // many costs nothing, one too few can lose the floor under an actor. So: the triangle's
            // bounds plus a margin, which lists every triangle the game would (checked by navtest).
            var margin = Math.Max(cellX, cellY) * 0.05f + 4f;
            var x0 = Clamp((int)MathF.Floor((minX - margin - min.X) / cellX), divisor);
            var x1 = Clamp((int)MathF.Floor((maxX + margin - min.X) / cellX), divisor);
            var y0 = Clamp((int)MathF.Floor((minY - margin - min.Y) / cellY), divisor);
            var y1 = Clamp((int)MathF.Floor((maxY + margin - min.Y) / cellY), divisor);
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++)
                    cells[y * divisor + x].Add((ushort)t);
        }

        var bytes = new List<byte>();
        foreach (var cell in cells)
        {
            bytes.AddRange(BitConverter.GetBytes((uint)cell.Count));
            foreach (var t in cell) bytes.AddRange(BitConverter.GetBytes(t));
        }
        return [.. bytes];
    }

    private static int Clamp(int v, uint divisor) => Math.Clamp(v, 0, (int)divisor - 1);

    /// <summary>
    /// After vertices/triangles changed: neighbour links rebuilt from shared edges (an edge shared
    /// by exactly two triangles links them), external links kept on edges that have no neighbour
    /// now, bounds and grid recomputed, unused vertices dropped.
    /// </summary>
    public static void Rebuild(NavigationMesh mesh)
    {
        var data = mesh.Data ?? throw new InvalidOperationException("navmesh without data");
        CompactVertices(data);

        var vertices = data.Vertices;
        var triangles = data.Triangles;
        var byEdge = new Dictionary<(int, int), List<(int Triangle, int Edge)>>();
        for (var t = 0; t < triangles.Count; t++)
        {
            var v = triangles[t].Vertices;
            int[] corners = [v.X, v.Y, v.Z];
            for (var e = 0; e < 3; e++)
            {
                var a = corners[e];
                var b = corners[(e + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                if (!byEdge.TryGetValue(key, out var list)) byEdge[key] = list = [];
                list.Add((t, e));
            }
        }

        for (var t = 0; t < triangles.Count; t++)
        {
            var tri = triangles[t];
            var v = tri.Vertices;
            int[] corners = [v.X, v.Y, v.Z];
            for (var e = 0; e < 3; e++)
            {
                var a = corners[e];
                var b = corners[(e + 1) % 3];
                var shared = byEdge[a < b ? (a, b) : (b, a)];
                var flag = (NavmeshTriangle.Flag)(1 << e);
                var external = tri.Flags.HasFlag(flag);

                if (shared.Count == 2)
                {
                    var other = shared[0].Triangle == t ? shared[1].Triangle : shared[0].Triangle;
                    SetEdge(tri, e, (short)other);
                    tri.Flags &= ~flag;
                }
                else if (!external)
                {
                    SetEdge(tri, e, -1);
                }
                // External link on a boundary edge: left as it was (points into another navmesh).
            }
        }

        // Door triangles must still exist.
        for (var i = data.DoorTriangles.Count - 1; i >= 0; i--)
            if (data.DoorTriangles[i].TriangleBeforeDoor >= triangles.Count) data.DoorTriangles.RemoveAt(i);

        if (vertices.Count == 0) return;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in vertices)
        {
            min = Vector3.Min(min, new Vector3(p.X, p.Y, p.Z));
            max = Vector3.Max(max, new Vector3(p.X, p.Y, p.Z));
        }
        data.Min = new P3Float(min.X, min.Y, min.Z);
        data.Max = new P3Float(max.X, max.Y, max.Z);

        var divisor = Math.Max(data.NavmeshGridDivisor, 1u);
        data.NavmeshGridDivisor = divisor;
        data.MaxDistanceX = Math.Max((max.X - min.X) / divisor, 0.0001f);
        data.MaxDistanceY = Math.Max((max.Y - min.Y) / divisor, 0.0001f);
        data.NavmeshGrid = BuildGrid(vertices, triangles, divisor, new Vector2(min.X, min.Y), data.MaxDistanceX, data.MaxDistanceY);
    }

    private static void SetEdge(NavmeshTriangle tri, int edge, short value)
    {
        switch (edge)
        {
            case 0: tri.EdgeLink_0_1 = value; break;
            case 1: tri.EdgeLink_1_2 = value; break;
            default: tri.EdgeLink_2_0 = value; break;
        }
    }

    /// <summary>Drops vertices no triangle uses and renumbers the triangles.</summary>
    private static void CompactVertices(NavigationMeshData data)
    {
        var used = new bool[data.Vertices.Count];
        foreach (var t in data.Triangles)
        {
            used[t.Vertices.X] = true;
            used[t.Vertices.Y] = true;
            used[t.Vertices.Z] = true;
        }
        if (used.All(u => u)) return;

        var map = new short[used.Length];
        var kept = new List<P3Float>();
        for (var i = 0; i < used.Length; i++)
        {
            map[i] = (short)kept.Count;
            if (used[i]) kept.Add(data.Vertices[i]);
        }
        data.Vertices.Clear();
        data.Vertices.AddRange(kept);
        foreach (var t in data.Triangles)
            t.Vertices = new P3Int16(map[t.Vertices.X], map[t.Vertices.Y], map[t.Vertices.Z]);
    }

    /// <summary>
    /// Another navmesh's links into <paramref name="target"/>: renumbered after triangles there were
    /// removed (<paramref name="map"/>: old index → new, -1 gone); links to removed triangles become
    /// open edges. Returns whether anything changed.
    /// </summary>
    public static bool RemapLinksInto(NavigationMesh neighbour, Mutagen.Bethesda.Plugins.FormKey target, int[] map)
    {
        var data = neighbour.Data;
        if (data is null) return false;
        var changed = false;
        var dropped = new HashSet<int>();
        for (var i = 0; i < data.EdgeLinks.Count; i++)
        {
            var link = data.EdgeLinks[i];
            if (link.Mesh.FormKey != target) continue;
            var old = link.TriangleIndex;
            var now = old >= 0 && old < map.Length ? map[old] : -1;
            if (now == old) continue;
            changed = true;
            if (now >= 0) link.TriangleIndex = (short)now;
            else dropped.Add(i);
        }

        if (dropped.Count > 0)
        {
            foreach (var tri in data.Triangles)
            {
                for (var e = 0; e < 3; e++)
                {
                    var flag = (NavmeshTriangle.Flag)(1 << e);
                    if (!tri.Flags.HasFlag(flag)) continue;
                    var value = e switch { 0 => tri.EdgeLink_0_1, 1 => tri.EdgeLink_1_2, _ => tri.EdgeLink_2_0 };
                    if (!dropped.Contains(value)) continue;
                    SetEdge(tri, e, -1);
                    tri.Flags &= ~flag;
                }
            }
        }
        return changed;
    }

    /// <summary>
    /// CK: Move Selection to Separate NavMesh. The triangles go into <paramref name="target"/> (an empty navmesh of the
    /// same cell) with their vertices and flags; links between moved triangles are rebuilt there, links to the rest and
    /// to other navmeshes and door data are left for Finalize. Returns the source's old → new triangle indices (for
    /// neighbours' links into it).
    /// </summary>
    public static int[] MoveTriangles(NavigationMesh source, IReadOnlySet<int> moved, NavigationMesh target)
    {
        var data = source.Data!;
        var into = target.Data!;
        var vertices = new Dictionary<int, short>();
        short Vertex(int i)
        {
            if (!vertices.TryGetValue(i, out var n))
            {
                vertices[i] = n = (short)into.Vertices.Count;
                into.Vertices.Add(data.Vertices[i]);
            }
            return n;
        }
        const NavmeshTriangle.Flag links = NavmeshTriangle.Flag.EdgeLink_0_1 | NavmeshTriangle.Flag.EdgeLink_1_2 | NavmeshTriangle.Flag.EdgeLink_2_0 | NavmeshTriangle.Flag.Door;
        foreach (var t in moved.Where(t => t >= 0 && t < data.Triangles.Count).OrderBy(t => t))
        {
            var copy = data.Triangles[t].DeepCopy();
            var v = copy.Vertices;
            copy.Vertices = new P3Int16(Vertex(v.X), Vertex(v.Y), Vertex(v.Z));
            copy.EdgeLink_0_1 = copy.EdgeLink_1_2 = copy.EdgeLink_2_0 = -1;
            copy.Flags &= ~links;
            into.Triangles.Add(copy);
        }
        Rebuild(target);
        return DeleteTriangles(source, moved);
    }

    /// <summary>Removes triangles; links into them from this mesh are cleared, later indices shift down. Returns old → new indices.</summary>
    public static int[] DeleteTriangles(NavigationMesh mesh, IReadOnlySet<int> doomed)
    {
        var data = mesh.Data!;
        var map = new int[data.Triangles.Count];
        var next = 0;
        for (var i = 0; i < map.Length; i++) map[i] = doomed.Contains(i) ? -1 : next++;

        for (var i = data.Triangles.Count - 1; i >= 0; i--)
            if (doomed.Contains(i)) data.Triangles.RemoveAt(i);

        foreach (var t in data.Triangles)
        {
            for (var e = 0; e < 3; e++)
            {
                if (t.Flags.HasFlag((NavmeshTriangle.Flag)(1 << e))) continue; // external
                var link = e switch { 0 => t.EdgeLink_0_1, 1 => t.EdgeLink_1_2, _ => t.EdgeLink_2_0 };
                if (link >= 0) SetEdge(t, e, (short)(link < map.Length ? map[link] : -1));
            }
        }

        for (var i = data.DoorTriangles.Count - 1; i >= 0; i--)
        {
            var before = data.DoorTriangles[i].TriangleBeforeDoor;
            if (before < 0 || before >= map.Length || map[before] < 0) data.DoorTriangles.RemoveAt(i);
            else data.DoorTriangles[i].TriangleBeforeDoor = (short)map[before];
        }

        Rebuild(mesh);
        return map;
    }
}
