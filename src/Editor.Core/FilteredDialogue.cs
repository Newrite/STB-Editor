using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Data;

namespace StbEditor.Core;

/// <summary>A line an NPC can say: where it is and whether a condition names the NPC (its ID, voice, faction…) or nothing rules it out.</summary>
public sealed record FilteredLine(FormKey Quest, string? QuestEditorId, FormKey Topic, string? TopicEditorId, FormKey Info, string Text, bool Named, int Unknown, bool ById = false);

/// <summary>
/// CK: Filtered Dialogue — the lines an NPC could say. Each line's conditions on the subject (with the quest's dialogue
/// conditions) are checked against what is fixed about the NPC: who it is, its voice type, race, sex, class, factions and
/// ranks. Conditions that depend on the game (quest stages, globals, distance…) count as passing. OR-joined conditions
/// form groups, as in the game: a line is out when a group is certainly false.
/// </summary>
public static class FilteredDialogue
{
    /// <summary>What is fixed about the NPC.</summary>
    public sealed class Actor
    {
        public required FormKey Key { get; init; }
        public FormKey? Voice { get; init; }
        public FormKey? Race { get; init; }
        public bool Female { get; init; }
        public FormKey? Class { get; init; }
        public Dictionary<FormKey, int> Factions { get; init; } = [];

        public static Actor Of(INpcGetter npc, RecordLookup lookup)
        {
            // Traits from the template chain when the NPC uses them.
            var traits = npc;
            for (var i = 0; i < 8 && traits.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) && lookup.Resolve<INpcGetter>(traits.Template.FormKey) is { } t; i++) traits = t;
            var factionsFrom = npc;
            for (var i = 0; i < 8 && factionsFrom.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Factions) && lookup.Resolve<INpcGetter>(factionsFrom.Template.FormKey) is { } t; i++) factionsFrom = t;
            return new Actor
            {
                Key = npc.FormKey,
                Voice = traits.Voice.FormKeyNullable is { IsNull: false } v ? v : null,
                Race = traits.Race.FormKey,
                Female = traits.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female),
                Class = traits.Class.FormKey,
                Factions = factionsFrom.Factions.Where(f => !f.Faction.IsNull).GroupBy(f => f.Faction.FormKey).ToDictionary(g => g.Key, g => (int)g.Max(f => f.Rank)),
            };
        }
    }

    public static List<FilteredLine> Lines(Workspace workspace, RecordLookup lookup, DialogueIndex index, Actor actor, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var result = new List<FilteredLine>();
        var quests = (lookup.Catalog.BySignature.GetValueOrDefault("QUST") ?? []).ToList();
        var done = 0;
        foreach (var entry in quests)
        {
            cancel.ThrowIfCancellationRequested();
            if (++done % 100 == 0) progress?.Report($"{done} / {quests.Count} quests");
            if (lookup.Resolve<IQuestGetter>(entry.FormKey) is not { } quest) continue;
            var questState = Evaluate(quest.DialogConditions, actor, lookup);
            if (questState.Out) continue;
            foreach (var topicKey in index.TopicsOf(quest.FormKey))
            {
                var topic = lookup.Resolve<IDialogTopicGetter>(topicKey);
                foreach (var (line, _) in DialogueIndex.Lines(workspace, topicKey))
                {
                    var state = Evaluate(line.Conditions, actor, lookup);
                    var speaker = !line.Speaker.IsNull;
                    if (speaker && line.Speaker.FormKey != actor.Key) continue;
                    if (state.Out) continue;
                    var text = string.Join(" ", line.Responses.Select(r => r.Text?.String).Where(t => !string.IsNullOrEmpty(t)));
                    if (text.Length == 0) text = line.Prompt?.String ?? "";
                    result.Add(new FilteredLine(quest.FormKey, quest.EditorID, topicKey, topic?.EditorID, line.FormKey, text,
                        speaker || state.Named || questState.Named, state.Unknown + questState.Unknown, speaker || state.ById || questState.ById));
                }
            }
        }
        return result;
    }

    /// <summary>The conditions against the actor: certainly false somewhere, true for the actor by a fact about it, how many could not be told.</summary>
    public static (bool Out, bool Named, int Unknown, bool ById) Evaluate(IReadOnlyList<IConditionGetter> conditions, Actor actor, RecordLookup lookup)
    {
        var named = false;
        var byId = false;
        var unknown = 0;
        // OR binds to the next condition: groups of OR-joined conditions, AND between groups.
        var group = new List<bool?>();
        bool outOfAll = false;
        for (var i = 0; i < conditions.Count; i++)
        {
            var value = Test(conditions[i], actor, lookup, ref named);
            // It is this NPC (not «is not someone else»).
            if (value == true && conditions[i].Data is IGetIsIDConditionDataGetter { Object.Link: { } who } && who.FormKey == actor.Key) byId = true;
            if (value is null) unknown++;
            group.Add(value);
            var or = conditions[i].Flags.HasFlag(Condition.Flag.OR);
            if (!or || i == conditions.Count - 1)
            {
                if (group.All(v => v == false)) outOfAll = true;
                group.Clear();
            }
        }
        return (outOfAll, named, unknown, byId);
    }

    private static bool? Test(IConditionGetter condition, Actor actor, RecordLookup lookup, ref bool named)
    {
        if (condition.Data.RunOnType != Condition.RunOnType.Subject || condition is not IConditionFloatGetter f || condition.Data.UseAliases) return null;
        double? value = condition.Data switch
        {
            IGetIsIDConditionDataGetter id when id.Object.Link is { IsNull: false } l => l.FormKey == actor.Key ? 1 : 0,
            IGetIsVoiceTypeConditionDataGetter v when v.VoiceTypeOrList.Link is { IsNull: false } l => actor.Voice is { } voice && InList(lookup, l.FormKey, voice) ? 1 : 0,
            IGetIsRaceConditionDataGetter r when r.Race.Link is { IsNull: false } l => l.FormKey == actor.Race ? 1 : 0,
            IGetIsSexConditionDataGetter s => (s.MaleFemaleGender == MaleFemaleGender.Female) == actor.Female ? 1 : 0,
            IGetIsClassConditionDataGetter c when c.Class.Link is { IsNull: false } l => l.FormKey == actor.Class ? 1 : 0,
            IGetInFactionConditionDataGetter fa when fa.Faction.Link is { IsNull: false } l => actor.Factions.ContainsKey(l.FormKey) ? 1 : 0,
            IGetFactionRankConditionDataGetter fr when fr.Faction.Link is { IsNull: false } l => actor.Factions.TryGetValue(l.FormKey, out var rank) ? rank : -2,
            _ => null,
        };
        if (value is not { } v2) return null;
        var target = f.ComparisonValue;
        var pass = condition.CompareOperator switch
        {
            CompareOperator.EqualTo => v2 == target,
            CompareOperator.NotEqualTo => v2 != target,
            CompareOperator.GreaterThan => v2 > target,
            CompareOperator.GreaterThanOrEqualTo => v2 >= target,
            CompareOperator.LessThan => v2 < target,
            CompareOperator.LessThanOrEqualTo => v2 <= target,
            _ => true,
        };
        // A passing identity test (it is this NPC, has this voice, is in this faction…) names the actor.
        if (pass && v2 >= 1 && condition.Data is IGetIsIDConditionDataGetter or IGetIsVoiceTypeConditionDataGetter or IGetInFactionConditionDataGetter or IGetIsClassConditionDataGetter)
            named = true;
        return pass;
    }

    private static bool InList(RecordLookup lookup, FormKey list, FormKey voice)
    {
        if (list == voice) return true;
        var seen = new HashSet<FormKey>();
        bool Walk(FormKey key)
        {
            if (!seen.Add(key)) return false;
            if (key == voice) return true;
            return lookup.Resolve<IFormListGetter>(key) is { } l && l.Items.Any(i => !i.IsNull && Walk(i.FormKey));
        }
        return Walk(list);
    }

    /// <summary>CK: Export Relationship Data — every relationship (RELA) as tab-separated rows.</summary>
    public static string Relationships(RecordLookup lookup, Func<FormKey, string> formId)
    {
        var text = new System.Text.StringBuilder("FORMID\tEDITORID\tPARENT\tCHILD\tRANK\tASSOCIATION\r\n");
        string Name(FormKey key) => lookup.Catalog.Get(key)?.EditorId ?? formId(key);
        foreach (var entry in lookup.Catalog.BySignature.GetValueOrDefault("RELA") ?? [])
            if (lookup.Resolve<IRelationshipGetter>(entry.FormKey) is { } r)
                text.Append(formId(r.FormKey)).Append('\t').Append(r.EditorID).Append('\t').Append(Name(r.Parent.FormKey)).Append('\t').Append(Name(r.Child.FormKey))
                    .Append('\t').Append(r.Rank).Append('\t').Append(r.AssociationType.IsNull ? "" : Name(r.AssociationType.FormKey)).Append("\r\n");
        return text.ToString();
    }
}
