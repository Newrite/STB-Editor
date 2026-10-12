using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using StbEditor.Data;
using StbEditor.Data.Catalog;

namespace StbEditor.Core;

/// <summary>A text found in a record: where (field path) and what.</summary>
/// <param name="Display">Text the player sees (a translated string: names, dialogue, descriptions…) — what Replace changes.</param>
public sealed record TextHit(FormKey Key, string Signature, string? EditorId, IReadOnlyList<object> Path, string PathText, string Text, FormKey? Topic, bool Display = false)
{
    public bool IsEditorId => PathText == "EditorID";
}

/// <summary>
/// Find Text (CK) / Search and Replace (xEdit): the plugin's records searched field by field (every string
/// and translated string: names, descriptions, dialogue responses, log entries, book text…), or the whole
/// load order by EditorID and name through the catalog.
/// </summary>
public static class TextSearch
{
    /// <summary>Every record of one plugin whose text fields contain <paramref name="query"/>.</summary>
    public static List<TextHit> InPlugin(PluginInfo plugin, RecordCatalog catalog, string query, bool matchCase, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        if (query.Length == 0) return [];
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return Walk(plugin, catalog, text => text.Contains(query, comparison), displayOnly: false, progress, cancel);
    }

    /// <summary>Every text the player sees in one plugin (names, descriptions, dialogue, book text…): what a translation covers.</summary>
    public static List<TextHit> DisplayTexts(PluginInfo plugin, RecordCatalog catalog, IProgress<string>? progress = null, CancellationToken cancel = default) =>
        Walk(plugin, catalog, text => !string.IsNullOrWhiteSpace(text), displayOnly: true, progress, cancel);

    private static List<TextHit> Walk(PluginInfo plugin, RecordCatalog catalog, Func<string, bool> match, bool displayOnly, IProgress<string>? progress, CancellationToken cancel)
    {
        var hits = new List<TextHit>();
        if (plugin.Mod is not { } mod) return hits;
        // A dialogue line is edited under its topic.
        var topicOf = new Dictionary<FormKey, FormKey>();
        foreach (var topic in mod.DialogTopics)
            foreach (var line in topic.Responses)
                topicOf[line.FormKey] = topic.FormKey;
        var count = 0;
        foreach (var record in mod.EnumerateMajorRecords())
        {
            cancel.ThrowIfCancellationRequested();
            if (++count % 2000 == 0) progress?.Report($"{count:N0} records");
            if (record is ICellGetter or IWorldspaceGetter or IPlacedGetter or INavigationMeshGetter or ILandscapeGetter) { Header(record); continue; }
            Search(record);
        }
        return hits;

        void Header(IMajorRecordGetter record)
        {
            // Containers and placed things: their own EditorID and name only (their contents are records of their own).
            if (!displayOnly && record.EditorID is { } id && match(id)) hits.Add(Hit(record, ["EditorID"], "EditorID", id));
            if (record is ITranslatedNamedGetter { Name: { } translated } && match(translated.String ?? "")) hits.Add(Hit(record, ["Name"], "Name", translated.String!) with { Display = true });
            else if (!displayOnly && record is INamedGetter { Name: { } name } && match(name)) hits.Add(Hit(record, ["Name"], "Name", name));
        }

        void Search(IMajorRecordGetter record)
        {
            IEnumerable<FieldLeaf> leaves;
            try { leaves = RecordFields.Leaves(record, maxDepth: 10, maxListItems: 500).ToList(); }
            catch { return; }
            foreach (var leaf in leaves)
            {
                // A topic's lines are records of their own (found as INFO, edited under the topic).
                if (record is IDialogTopicGetter && leaf.Path is ["Responses", ..]) continue;
                var text = leaf.Value switch
                {
                    string s => s,
                    ITranslatedStringGetter t => t.String,
                    _ => null,
                };
                if (text is null || (displayOnly && leaf.Value is not ITranslatedStringGetter) || !match(text)) continue;
                hits.Add(Hit(record, leaf.Path, leaf.PathText, text) with { Display = leaf.Value is ITranslatedStringGetter });
            }
        }

        TextHit Hit(IMajorRecordGetter record, IReadOnlyList<object> path, string pathText, string text) =>
            new(record.FormKey, catalog.Get(record.FormKey)?.Signature ?? Signature(record), record.EditorID, path, pathText, text, topicOf.TryGetValue(record.FormKey, out var topic) ? topic : null);
    }

    /// <summary>The whole load order by EditorID and name (the catalog's winning versions).</summary>
    public static List<TextHit> InCatalog(RecordCatalog catalog, string query, bool matchCase)
    {
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var hits = new List<TextHit>();
        if (query.Length == 0) return hits;
        foreach (var record in catalog.ByKey.Values)
        {
            if (record.EditorId is { } id && id.Contains(query, comparison)) hits.Add(new TextHit(record.FormKey, record.Signature, id, ["EditorID"], "EditorID", id, null));
            if (record.Name is { } name && name.Contains(query, comparison)) hits.Add(new TextHit(record.FormKey, record.Signature, record.EditorId, ["Name"], "Name", name, null, Display: true));
        }
        return hits;
    }

    /// <summary>The text with every occurrence replaced.</summary>
    public static string Replace(string text, string query, string replacement, bool matchCase) =>
        text.Replace(query, replacement, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static string Signature(IMajorRecordGetter record) => record switch
    {
        IDialogResponsesGetter => "INFO",
        IPlacedNpcGetter => "ACHR",
        IPlacedObjectGetter => "REFR",
        INavigationMeshGetter => "NAVM",
        ILandscapeGetter => "LAND",
        _ => "?",
    };
}
