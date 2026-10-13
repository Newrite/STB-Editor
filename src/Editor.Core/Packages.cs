using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace StbEditor.Core;

/// <summary>
/// AI packages (PACK) as the Creation Kit shows them. A package mostly fills in a template: the template
/// (a PACK of type PackageTemplate, «Sandbox», «Travel»…) owns the procedure tree and names its data inputs
/// (BNAM), the package holds only the values under the same keys, unnamed. Picking a template copies its
/// inputs with their defaults.
/// </summary>
public static class Packages
{
    /// <summary>A data input: its key (the procedure tree's DataInputIndices), name and value.</summary>
    public sealed record Input(sbyte Key, string Name, IAPackageDataGetter Data);

    /// <summary>
    /// The package's data inputs in the template's order (the CK's), named by the template, else by the
    /// package's own names; inputs the template does not have follow in key order.
    /// </summary>
    public static List<Input> Inputs(IPackageGetter package, IPackageGetter? template)
    {
        var inputs = new List<Input>();
        var seen = new HashSet<sbyte>();
        if (template is not null && !ReferenceEquals(template, package))
        {
            foreach (var key in template.Data.Keys)
                if (package.Data.TryGetValue(key, out var data) && seen.Add(key))
                    inputs.Add(new Input(key, NameOf(key, data, template), data));
        }
        foreach (var (key, data) in package.Data.OrderBy(kv => kv.Key))
            if (seen.Add(key)) inputs.Add(new Input(key, NameOf(key, data, template), data));
        return inputs;
    }

    private static string NameOf(sbyte key, IAPackageDataGetter data, IPackageGetter? template) =>
        template is not null && template.Data.TryGetValue(key, out var named) && !string.IsNullOrEmpty(named.Name) ? named.Name
        : !string.IsNullOrEmpty(data.Name) ? data.Name
        : $"#{key}";

    /// <summary>Names of the inputs by key (procedure branches, package-data conditions).</summary>
    public static Dictionary<int, string> InputNames(IPackageGetter package, IPackageGetter? template) =>
        Inputs(package, template).ToDictionary(i => (int)i.Key, i => i.Name);

    /// <summary>The kind of an input as the CK names it: Bool, Int, Float, Location, Target, ObjectList, Topic.</summary>
    public static string KindOf(IAPackageDataGetter data) => data switch
    {
        IPackageDataBoolGetter => "Bool",
        IPackageDataIntGetter => "Int",
        IPackageDataFloatGetter => "Float",
        IPackageDataLocationGetter => "Location",
        IPackageDataTargetGetter => "Target",
        IPackageDataObjectListGetter => "ObjectList",
        IPackageDataTopicGetter => "Topic",
        _ => "—",
    };

    /// <summary>
    /// Makes <paramref name="package"/> use <paramref name="template"/> as the CK does when one is picked:
    /// the template's inputs that carry a value are copied with their defaults (unnamed, sorted by key, as
    /// the CK writes them); inputs the package already had of the same kind keep their values.
    /// </summary>
    public static void UseTemplate(Package package, IPackageGetter template)
    {
        var old = package.Data.ToDictionary(kv => kv.Key, kv => kv.Value);
        package.PackageTemplate.SetTo(template.FormKey);
        package.Data.Clear();
        foreach (var (key, data) in template.Data.OrderBy(kv => kv.Key))
        {
            // Plain APackageData entries are the template's retired inputs: a name, no value.
            if (KindOf(data) == "—") continue;
            var copy = old.TryGetValue(key, out var mine) && KindOf(mine) == KindOf(data) ? mine : data.DeepCopy();
            copy.Name = null;
            copy.Flags = null;
            package.Data[key] = copy;
        }
        package.DataInputVersion = template.DataInputVersion;
        if (package.XnamMarker.Length == 0) package.XnamMarker = template.XnamMarker.ToArray();
        // The tree is the template's; a package with a template has none of its own.
        package.ProcedureTree.Clear();
    }

    /// <summary>A branch of the procedure tree with its depth; <see cref="Index"/> is its place in the list.</summary>
    public sealed record Node(int Index, int Depth, IPackageBranchGetter Branch);

    /// <summary>
    /// The procedure tree from its flat list: branches are stored depth first, a branch with a root (Sequence,
    /// Stacked, Simultaneous, Random…) is followed by its <c>BranchCount</c> sub-trees.
    /// </summary>
    public static List<Node> Tree(IReadOnlyList<IPackageBranchGetter> branches)
    {
        var nodes = new List<Node>();
        var next = 0;
        void Walk(int depth)
        {
            var at = next++;
            var branch = branches[at];
            nodes.Add(new Node(at, depth, branch));
            var children = branch.Root?.BranchCount ?? 0;
            for (var i = 0; i < children && next < branches.Count; i++) Walk(depth + 1);
        }
        while (next < branches.Count) Walk(0);
        return nodes;
    }

    /// <summary>Where a branch's sub-tree ends in the flat list (the index after its last descendant).</summary>
    public static int SubtreeEnd(IReadOnlyList<IPackageBranchGetter> branches, int index)
    {
        var next = index;
        void Walk()
        {
            var branch = branches[next++];
            var children = branch.Root?.BranchCount ?? 0;
            for (var i = 0; i < children && next < branches.Count; i++) Walk();
        }
        Walk();
        return next;
    }

    /// <summary>The branch whose sub-trees include <paramref name="index"/> directly, or null for a top-level branch.</summary>
    public static int? ParentOf(IReadOnlyList<IPackageBranchGetter> branches, int index)
    {
        var nodes = Tree(branches);
        var at = nodes.FindIndex(n => n.Index == index);
        if (at < 0 || nodes[at].Depth == 0) return null;
        for (var i = at - 1; i >= 0; i--)
            if (nodes[i].Depth == nodes[at].Depth - 1) return nodes[i].Index;
        return null;
    }

    /// <summary>A new branch: a procedure (leaf) or, with <paramref name="composite"/>, a node with sub-branches (Sequence, Stacked…).</summary>
    public static PackageBranch NewBranch(string type, bool composite) => composite
        ? new PackageBranch { BranchType = type, Root = new PackageRoot { BranchCount = 0, Flags = 0 } }
        : new PackageBranch { BranchType = "Procedure", ProcedureType = type };

    /// <summary>
    /// Adds <paramref name="branch"/> as the last sub-branch of <paramref name="parent"/> (which must have a root), or — with
    /// <paramref name="asChild"/> false — right after <paramref name="parent"/>'s sub-tree as its sibling. Returns the new index.
    /// </summary>
    public static int Insert(ExtendedList<PackageBranch> tree, int at, PackageBranch branch, bool asChild)
    {
        if (tree.Count == 0) { tree.Add(branch); return 0; }
        var end = SubtreeEnd(tree, at);
        if (asChild)
        {
            if (tree[at].Root is not { } root) throw new InvalidOperationException("a procedure has no sub-branches");
            root.BranchCount++;
        }
        else if (ParentOf(tree, at) is { } parent) tree[parent].Root!.BranchCount++;
        tree.Insert(end, branch);
        return end;
    }

    /// <summary>Removes a branch with its sub-tree.</summary>
    public static void Remove(ExtendedList<PackageBranch> tree, int at)
    {
        var parent = ParentOf(tree, at);
        var end = SubtreeEnd(tree, at);
        tree.RemoveRange(at, end - at);
        if (parent is { } p && tree[p].Root is { } root) root.BranchCount = Math.Max(0, root.BranchCount - 1);
    }

    /// <summary>Swaps a branch's sub-tree with the next (down) or previous (up) sibling's; returns its new index, or null.</summary>
    public static int? Move(ExtendedList<PackageBranch> tree, int at, bool down)
    {
        var parent = ParentOf(tree, at);
        var siblings = Tree(tree).Where(n => ParentOf(tree, n.Index) == parent && (parent is null ? n.Depth == 0 : true)).Select(n => n.Index).ToList();
        var i = siblings.IndexOf(at);
        var j = down ? i + 1 : i - 1;
        if (i < 0 || j < 0 || j >= siblings.Count) return null;
        var (first, second) = down ? (at, siblings[j]) : (siblings[j], at);
        var firstBlock = tree.Skip(first).Take(SubtreeEnd(tree, first) - first).ToList();
        var secondEnd = SubtreeEnd(tree, second);
        var secondBlock = tree.Skip(second).Take(secondEnd - second).ToList();
        tree.RemoveRange(first, secondEnd - first);
        tree.InsertRange(first, [.. secondBlock, .. firstBlock]);
        return down ? first + secondBlock.Count : first;
    }
}
