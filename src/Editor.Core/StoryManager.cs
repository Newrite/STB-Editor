using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Data;
using StbEditor.Data.Catalog;

namespace StbEditor.Core;

/// <summary>A Story Manager node in its tree: an event, a branch or a quest node with its quests.</summary>
public sealed record StoryNode(FormKey Key, string Signature, string? EditorId, string Detail, int Conditions, IReadOnlyList<FormKey> Quests)
{
    /// <summary>The node it hangs under (null for event nodes and nodes whose parent is missing).</summary>
    public FormKey? Parent { get; set; }

    public List<StoryNode> Children { get; } = [];
}

/// <summary>
/// The Creation Kit's Story Manager tree (SMEN event nodes at the top, SMBN branch and SMQN quest nodes under them):
/// children are the nodes whose Parent is the node, in the order of their PreviousSibling chain — the order the game
/// tries them in.
/// </summary>
public static class StoryManager
{
    public static List<StoryNode> Build(RecordLookup lookup, RecordCatalog catalog) => Build(lookup, catalog, null);

    /// <param name="resolve">The node as it is now (an edit of it), else null for the winning version.</param>
    public static List<StoryNode> Build(RecordLookup lookup, RecordCatalog catalog, Func<FormKey, IMajorRecordGetter?>? resolve)
    {
        T? Get<T>(FormKey key) where T : class, IMajorRecordGetter => resolve?.Invoke(key) as T ?? lookup.Resolve<T>(key);
        var nodes = new Dictionary<FormKey, (StoryNode Node, FormKey? Parent, FormKey? Previous)>();
        foreach (var signature in new[] { "SMEN", "SMBN", "SMQN" })
            foreach (var record in catalog.BySignature.GetValueOrDefault(signature) ?? [])
            {
                switch (signature)
                {
                    case "SMEN" when Get<IStoryManagerEventNodeGetter>(record.FormKey) is { } e:
                        nodes[e.FormKey] = (new StoryNode(e.FormKey, signature, e.EditorID, e.Type?.ToString() ?? "", e.Conditions.Count, []), e.Parent.FormKeyNullable, e.PreviousSibling.FormKeyNullable);
                        break;
                    case "SMBN" when Get<IStoryManagerBranchNodeGetter>(record.FormKey) is { } b:
                        nodes[b.FormKey] = (new StoryNode(b.FormKey, signature, b.EditorID, b.Flags?.ToString() ?? "", b.Conditions.Count, []), b.Parent.FormKeyNullable, b.PreviousSibling.FormKeyNullable);
                        break;
                    case "SMQN" when Get<IStoryManagerQuestNodeGetter>(record.FormKey) is { } q:
                        nodes[q.FormKey] = (new StoryNode(q.FormKey, signature, q.EditorID, q.Flags.ToString(), q.Conditions.Count,
                            q.Quests.Select(x => x.Quest.FormKeyNullable).OfType<FormKey>().ToList()), q.Parent.FormKeyNullable, q.PreviousSibling.FormKeyNullable);
                        break;
                }
            }

        var roots = new List<StoryNode>();
        foreach (var group in nodes.Values.GroupBy(n => n.Parent is { IsNull: false } p && nodes.ContainsKey(p) ? p : (FormKey?)null))
        {
            var ordered = Ordered(group.ToList());
            if (group.Key is { } parent)
            {
                foreach (var child in ordered) child.Parent = parent;
                nodes[parent].Node.Children.AddRange(ordered);
            }
            else roots.AddRange(ordered);
        }
        return roots.OrderBy(r => r.Signature == "SMEN" ? 0 : 1).ThenBy(r => r.Detail, StringComparer.Ordinal).ThenBy(r => r.EditorId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Siblings along their PreviousSibling chain; any that are not on it follow by EditorID.</summary>
    private static List<StoryNode> Ordered(List<(StoryNode Node, FormKey? Parent, FormKey? Previous)> siblings)
    {
        var keys = siblings.Select(s => s.Node.Key).ToHashSet();
        var byPrevious = siblings.Where(s => s.Previous is { IsNull: false } p && keys.Contains(p)).GroupBy(s => s.Previous!.Value).ToDictionary(g => g.Key, g => g.First().Node);
        var ordered = new List<StoryNode>();
        var seen = new HashSet<FormKey>();
        foreach (var first in siblings.Where(s => s.Previous is not { IsNull: false } p || !keys.Contains(p)).Select(s => s.Node))
        {
            for (var node = first; node is not null && seen.Add(node.Key); node = byPrevious.GetValueOrDefault(node.Key))
                ordered.Add(node);
        }
        ordered.AddRange(siblings.Select(s => s.Node).Where(n => !seen.Contains(n.Key)).OrderBy(n => n.EditorId, StringComparer.OrdinalIgnoreCase));
        return ordered;
    }
}
