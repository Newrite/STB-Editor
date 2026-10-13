using System.Numerics;
using Mutagen.Bethesda.Skyrim;

namespace StbEditor.Core;

/// <summary>One problem of a navmesh: which triangle (or vertex) and what is wrong.</summary>
/// <param name="Problem">A fixed text (translated by the UI); <paramref name="Detail"/> holds the numbers.</param>
public sealed record NavmeshIssue(int? Triangle, int? Vertex, string Problem, bool Error, string Detail = "");

/// <summary>
/// CK: Check NavMeshes — what makes actors stop, walk into walls or the game misbehave: triangles with repeated or
/// out-of-range corners, with no area, facing down; edges shared by more than two triangles; vertices at the same place;
/// vertices no triangle uses; links (inside the navmesh) to triangles that do not exist or do not link back.
/// </summary>
public static class NavmeshCheck
{
    public static List<NavmeshIssue> Check(INavigationMeshDataGetter data)
    {
        var issues = new List<NavmeshIssue>();
        var vertices = data.Vertices;
        var triangles = data.Triangles;
        Vector3 P(int i) => new(vertices[i].X, vertices[i].Y, vertices[i].Z);
        var used = new bool[vertices.Count];
        var edges = new Dictionary<(int, int), List<int>>();

        for (var t = 0; t < triangles.Count; t++)
        {
            var v = triangles[t].Vertices;
            int[] c = [v.X, v.Y, v.Z];
            if (c.Any(i => i < 0 || i >= vertices.Count)) { issues.Add(new(t, null, "a corner is not a vertex of the navmesh", true)); continue; }
            foreach (var i in c) used[i] = true;
            if (c[0] == c[1] || c[1] == c[2] || c[0] == c[2]) { issues.Add(new(t, null, "two corners are the same vertex", true)); continue; }
            var normal = Vector3.Cross(P(c[1]) - P(c[0]), P(c[2]) - P(c[0]));
            if (normal.Length() / 2 < 1f) issues.Add(new(t, null, "no area (corners in a line)", true));
            else if (normal.Z < 0) issues.Add(new(t, null, "faces down (clockwise seen from above)", false));
            for (var e = 0; e < 3; e++)
            {
                var a = c[e];
                var b = c[(e + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                if (!edges.TryGetValue(key, out var list)) edges[key] = list = [];
                list.Add(t);
            }
        }
        foreach (var (edge, list) in edges)
            if (list.Count > 2) issues.Add(new(list[0], null, "edge shared by more than two triangles", true, $"{edge.Item1}-{edge.Item2}: {list.Count}"));

        // Links inside the navmesh: an internal neighbour (no external-link flag) must exist and link back.
        for (var t = 0; t < triangles.Count; t++)
        {
            var tri = triangles[t];
            for (var e = 0; e < 3; e++)
            {
                if (tri.Flags.HasFlag((NavmeshTriangle.Flag)(1 << e))) continue;
                var other = e switch { 0 => tri.EdgeLink_0_1, 1 => tri.EdgeLink_1_2, _ => tri.EdgeLink_2_0 };
                if (other < 0) continue;
                if (other >= triangles.Count) { issues.Add(new(t, null, "links to a triangle that does not exist", true, $"{other}")); continue; }
                var back = triangles[other];
                if (back.EdgeLink_0_1 != t && back.EdgeLink_1_2 != t && back.EdgeLink_2_0 != t) issues.Add(new(t, null, "links to a triangle that does not link back", false, $"{other}"));
            }
        }

        for (var i = 0; i < vertices.Count; i++)
            if (!used[i]) issues.Add(new(null, i, "no triangle uses this vertex", false));

        // Vertices at the same place (within a unit): triangles meet there without sharing the vertex, so no link.
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (var i = 0; i < vertices.Count; i++)
        {
            var key = ((int)MathF.Floor(vertices[i].X), (int)MathF.Floor(vertices[i].Y), (int)MathF.Floor(vertices[i].Z));
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = [];
            list.Add(i);
        }
        foreach (var list in grid.Values.Where(l => l.Count > 1))
            for (var k = 1; k < list.Count; k++)
                if (Vector3.Distance(P(list[0]), P(list[k])) < 1f)
                    issues.Add(new(null, list[k], "at the same place as another vertex (merge them)", false, $"{list[0]}"));

        return issues;
    }
}
