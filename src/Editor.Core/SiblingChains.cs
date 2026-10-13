using Mutagen.Bethesda.Plugins;

namespace StbEditor.Core;

/// <summary>
/// Trees kept as a parent and a previous-sibling link per node (idle animations, Story Manager nodes): the edits that move a
/// node to a new place.
/// </summary>
public static class SiblingChains
{
    /// <summary>
    /// The edits that move <paramref name="moved"/> to be a child of <paramref name="parent"/> (null: a root) right after
    /// <paramref name="after"/> (null: first): the moved node's new parent and previous, the node that followed it before
    /// (now after its old previous) and the node that follows it at the new place. Key → (parent, previous).
    /// </summary>
    public static Dictionary<FormKey, (FormKey? Parent, FormKey? Previous)> Move(IReadOnlyList<FormKey> oldSiblings, FormKey moved, FormKey? parent,
        IReadOnlyList<FormKey> newSiblings, FormKey? after, Func<FormKey, FormKey?> parentOf)
    {
        var edits = new Dictionary<FormKey, (FormKey? Parent, FormKey? Previous)>();
        // Close the gap where it was.
        var at = oldSiblings.ToList().IndexOf(moved);
        if (at >= 0 && at + 1 < oldSiblings.Count)
        {
            var next = oldSiblings[at + 1];
            edits[next] = (parentOf(next), at > 0 ? oldSiblings[at - 1] : null);
        }
        // Open one where it goes (the list without it).
        var target = newSiblings.Where(n => n != moved).ToList();
        var index = after is { } a ? target.IndexOf(a) + 1 : 0;
        if (index < target.Count)
        {
            var follower = target[index];
            edits[follower] = (edits.TryGetValue(follower, out var e) ? e.Parent : parentOf(follower), moved);
        }
        edits[moved] = (parent, index > 0 ? target[index - 1] : null);
        return edits;
    }
}
