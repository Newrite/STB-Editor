using System.Diagnostics;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Data.Catalog;

namespace StbEditor.Data;

/// <summary>
/// Which topics (DIAL) and branches (DLBR) belong to which quest, from the winning versions — the
/// quest's Dialogue tab lists them. Dialogue lines (INFO) are not catalogued: <see cref="Lines"/> merges
/// them from every plugin's copy of a topic, as the game does.
/// </summary>
public sealed class DialogueIndex
{
    private readonly Dictionary<FormKey, List<FormKey>> _topics = new();
    private readonly Dictionary<FormKey, List<FormKey>> _branches = new();

    public int TopicCount { get; private set; }
    public TimeSpan BuildTime { get; private set; }

    public IReadOnlyList<FormKey> TopicsOf(FormKey quest) => _topics.TryGetValue(quest, out var list) ? list : [];
    public IReadOnlyList<FormKey> BranchesOf(FormKey quest) => _branches.TryGetValue(quest, out var list) ? list : [];

    public static DialogueIndex Build(Workspace workspace, RecordCatalog catalog, CancellationToken cancel = default)
    {
        var clock = Stopwatch.StartNew();
        var lookup = new RecordLookup(workspace, catalog);
        var index = new DialogueIndex();
        foreach (var record in catalog.BySignature.GetValueOrDefault("DIAL") ?? [])
        {
            cancel.ThrowIfCancellationRequested();
            if (lookup.Resolve<IDialogTopicGetter>(record.FormKey)?.Quest.FormKeyNullable is not { IsNull: false } quest) continue;
            (index._topics.TryGetValue(quest, out var list) ? list : index._topics[quest] = []).Add(record.FormKey);
            index.TopicCount++;
        }
        foreach (var record in catalog.BySignature.GetValueOrDefault("DLBR") ?? [])
        {
            if (lookup.Resolve<IDialogBranchGetter>(record.FormKey)?.Quest.FormKeyNullable is not { IsNull: false } quest) continue;
            (index._branches.TryGetValue(quest, out var list) ? list : index._branches[quest] = []).Add(record.FormKey);
        }
        index.BuildTime = clock.Elapsed;
        return index;
    }

    /// <summary>
    /// The lines of a topic as the game sees them: every plugin's copy of the topic adds its lines, each
    /// line's last version wins, in the order lines first appear (load order). With the plugin of each.
    /// </summary>
    public static List<(IDialogResponsesGetter Line, string Plugin)> Lines(Workspace workspace, FormKey topic)
    {
        var order = new List<FormKey>();
        var latest = new Dictionary<FormKey, (IDialogResponsesGetter, string)>();
        IEnumerable<IModContext<IDialogTopicGetter>> versions;
        try { versions = workspace.LinkCache.ResolveAllSimpleContexts<IDialogTopicGetter>(topic).Reverse().ToList(); }
        catch { return []; }
        foreach (var version in versions)
        {
            foreach (var line in version.Record.Responses)
            {
                if (!latest.ContainsKey(line.FormKey)) order.Add(line.FormKey);
                latest[line.FormKey] = (line, version.ModKey.FileName.String);
            }
        }
        return order.Select(k => latest[k]).ToList();
    }
}
