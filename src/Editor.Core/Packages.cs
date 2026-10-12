using Mutagen.Bethesda.Skyrim;

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
}
