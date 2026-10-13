using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Data;
using StbEditor.Data.Catalog;

namespace StbEditor.Core;

/// <summary>An idle animation (IDLE) in its tree: what it plays, under which parent, after which sibling.</summary>
public sealed record IdleNode(FormKey Key, string? EditorId, string Event, string Behavior, int Conditions, IdleAnimation.Flag Flags, FormKey? Parent, FormKey? Previous)
{
    public List<IdleNode> Children { get; } = [];
}

/// <summary>
/// The Creation Kit's Animations tree: an idle's children are the idles whose parent (RelatedIdles[0]) is it, in the
/// order of their previous-sibling chain (RelatedIdles[1]) — the order the game tries their conditions in, the first
/// that passes plays. Roots are idles without an idle parent (none, or an action); they are grouped by behaviour graph.
/// </summary>
public static class IdleTree
{
    public static List<IdleNode> Build(RecordLookup lookup, RecordCatalog catalog)
        => Build((catalog.BySignature.GetValueOrDefault("IDLE") ?? []).Select(r => r.FormKey), lookup.Resolve<IIdleAnimationGetter>);

    /// <param name="resolve">The idle as it is now (the editor's edit of it, else the winning version).</param>
    public static List<IdleNode> Build(IEnumerable<FormKey> keys, Func<FormKey, IIdleAnimationGetter?> resolve)
    {
        var nodes = new Dictionary<FormKey, IdleNode>();
        foreach (var key in keys)
            if (resolve(key) is { } idle)
                nodes[idle.FormKey] = Node(idle);
        return Arrange(nodes);
    }

    public static IdleNode Node(IIdleAnimationGetter idle)
    {
        var related = idle.RelatedIdles;
        FormKey? parent = related.Count > 0 && !related[0].IsNull ? related[0].FormKey : null;
        FormKey? previous = related.Count > 1 && !related[1].IsNull ? related[1].FormKey : null;
        return new IdleNode(idle.FormKey, idle.EditorID, idle.AnimationEvent ?? "", idle.Filename?.DataRelativePath.Path ?? "", idle.Conditions.Count, idle.Flags, parent, previous);
    }

    /// <summary>Children under their parents along the sibling chains; the roots (in chains per action they hang from).</summary>
    public static List<IdleNode> Arrange(Dictionary<FormKey, IdleNode> nodes)
    {
        var roots = new List<IdleNode>();
        foreach (var group in nodes.Values.GroupBy(n => n.Parent))
        {
            var ordered = Ordered(group.ToList());
            if (group.Key is { } parent && nodes.TryGetValue(parent, out var owner)) owner.Children.AddRange(ordered);
            else roots.AddRange(ordered);
        }
        return roots;
    }

    /// <summary>The idles that share <paramref name="node"/>'s parent, in order (roots: those under the same action).</summary>
    public static IReadOnlyList<IdleNode> Siblings(IReadOnlyList<IdleNode> roots, IdleNode node, Dictionary<FormKey, IdleNode> byKey)
        => node.Parent is { } p && byKey.TryGetValue(p, out var parent) ? parent.Children : roots.Where(r => r.Parent == node.Parent).ToList();

    /// <summary>Every node by key.</summary>
    public static Dictionary<FormKey, IdleNode> Index(IEnumerable<IdleNode> roots)
    {
        var result = new Dictionary<FormKey, IdleNode>();
        void Add(IdleNode n) { result[n.Key] = n; foreach (var c in n.Children) Add(c); }
        foreach (var r in roots) Add(r);
        return result;
    }

    /// <summary>Siblings along their previous-sibling chain; any that are not on it follow by EditorID.</summary>
    public static List<IdleNode> Ordered(List<IdleNode> siblings)
    {
        var keys = siblings.Select(s => s.Key).ToHashSet();
        var byPrevious = siblings.Where(s => s.Previous is { } p && keys.Contains(p)).GroupBy(s => s.Previous!.Value).ToDictionary(g => g.Key, g => g.First());
        var ordered = new List<IdleNode>();
        var seen = new HashSet<FormKey>();
        foreach (var first in siblings.Where(s => s.Previous is not { } p || !keys.Contains(p)))
            for (var node = first; node is not null && seen.Add(node.Key); node = byPrevious.GetValueOrDefault(node.Key))
                ordered.Add(node);
        ordered.AddRange(siblings.Where(n => !seen.Contains(n.Key)).OrderBy(n => n.EditorId, StringComparer.OrdinalIgnoreCase));
        return ordered;
    }

    /// <summary>The (parent, previous) edits that put <paramref name="moved"/> under <paramref name="parent"/> after <paramref name="after"/>.</summary>
    public static Dictionary<FormKey, (FormKey? Parent, FormKey? Previous)> Move(IReadOnlyList<IdleNode> oldSiblings, IdleNode moved, FormKey? parent,
        IReadOnlyList<IdleNode> newSiblings, FormKey? after)
    {
        var parents = oldSiblings.Concat(newSiblings).Append(moved).GroupBy(n => n.Key).ToDictionary(g => g.Key, g => g.First().Parent);
        return SiblingChains.Move(oldSiblings.Select(n => n.Key).ToList(), moved.Key, parent, newSiblings.Select(n => n.Key).ToList(), after, k => parents.GetValueOrDefault(k));
    }
}
