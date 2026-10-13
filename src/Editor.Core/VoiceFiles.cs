using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Data;

namespace StbEditor.Core;

/// <summary>One spoken response of a dialogue line, for one voice type: where the game looks for its audio.</summary>
public sealed record VoiceLine(FormKey Quest, string? QuestEditorId, FormKey Topic, string? TopicEditorId, FormKey Info, int Response,
    FormKey? VoiceType, string? VoiceTypeEditorId, string Text, string Emotion, string ScriptNotes)
{
    /// <summary>Path under Data without the extension (.fuz, or .xwm / .wav with a .lip); null when no voice type is known.</summary>
    public string? PathStem => VoiceTypeEditorId is { } voice ? VoiceFiles.Folder(Info.ModKey, voice) + VoiceFiles.BaseName(QuestEditorId, TopicEditorId, Info, Response) : null;
}

/// <summary>
/// Voice files the way the Creation Kit names them: <c>Sound\Voice\&lt;plugin&gt;\&lt;voice type&gt;\&lt;quest&gt;_&lt;topic&gt;_&lt;FormID&gt;_&lt;n&gt;</c>,
/// the quest's EditorID cut to 10 characters and the topic's to what is left of 25; the FormID is the line's own (without the
/// load order byte), n the response number. Which voice types say a line comes from its speaker, its conditions on the
/// subject (GetIsID, GetIsVoiceType, GetIsAliasRef), the quest's dialogue conditions and the scenes that use the topic —
/// as far as those name speakers; a line no condition ties to a speaker gets none (the CK would offer every voice type).
/// </summary>
public static class VoiceFiles
{
    public static string Folder(ModKey plugin, string voiceType) => $@"Sound\Voice\{plugin.FileName}\{voiceType}\";

    public static string BaseName(string? questEditorId, string? topicEditorId, FormKey info, int response)
    {
        var quest = questEditorId ?? "";
        var topic = topicEditorId ?? "";
        if (quest.Length + topic.Length > 25)
        {
            if (quest.Length > 10) quest = quest[..10];
            if (quest.Length + topic.Length > 25) topic = topic[..(25 - quest.Length)];
        }
        return $"{quest}_{topic}_{info.ID:X8}_{response}";
    }

    /// <summary>Every response of every line of the plugin's dialogue (or of one quest), per voice type.</summary>
    public static List<VoiceLine> Lines(Workspace workspace, RecordLookup lookup, DialogueIndex index, IEnumerable<FormKey> quests, Func<IDialogResponsesGetter, bool>? include = null)
    {
        var result = new List<VoiceLine>();
        var speakers = new Speakers(lookup, workspace);
        foreach (var questKey in quests)
        {
            if (lookup.Resolve<IQuestGetter>(questKey) is not { } quest) continue;
            var scenes = SceneSpeakers(lookup, quest);
            var questVoices = speakers.Of(quest.DialogConditions, quest);
            foreach (var topicKey in index.TopicsOf(questKey))
            {
                var topic = lookup.Resolve<IDialogTopicGetter>(topicKey);
                foreach (var (line, _) in DialogueIndex.Lines(workspace, topicKey))
                {
                    if (include is not null && !include(line)) continue;
                    var voices = new HashSet<FormKey>();
                    if (!line.Speaker.IsNull) voices.UnionWith(speakers.OfActor(line.Speaker.FormKey));
                    voices.UnionWith(speakers.Of(line.Conditions, quest));
                    if (voices.Count == 0 && scenes.TryGetValue(topicKey, out var aliases))
                        foreach (var alias in aliases) voices.UnionWith(speakers.OfAlias(quest, alias));
                    if (voices.Count == 0) voices.UnionWith(questVoices);
                    foreach (var response in line.Responses)
                    {
                        var text = response.Text?.String ?? "";
                        if (voices.Count == 0)
                            result.Add(new VoiceLine(questKey, quest.EditorID, topicKey, topic?.EditorID, line.FormKey, response.ResponseNumber, null, null, text, response.Emotion.ToString(), response.ScriptNotes ?? ""));
                        foreach (var voice in voices.OrderBy(v => lookup.Resolve<IVoiceTypeGetter>(v)?.EditorID, StringComparer.OrdinalIgnoreCase))
                            result.Add(new VoiceLine(questKey, quest.EditorID, topicKey, topic?.EditorID, line.FormKey, response.ResponseNumber, voice,
                                lookup.Resolve<IVoiceTypeGetter>(voice)?.EditorID, text, response.Emotion.ToString(), response.ScriptNotes ?? ""));
                    }
                }
            }
        }
        return result;
    }

    /// <summary>Topic → aliases (by ID) of the actors that say it in the quest's scenes.</summary>
    private static Dictionary<FormKey, HashSet<int>> SceneSpeakers(RecordLookup lookup, IQuestGetter quest)
    {
        var result = new Dictionary<FormKey, HashSet<int>>();
        // Scenes name their quest; the quest does not list them.
        foreach (var record in lookup.Catalog.BySignature.GetValueOrDefault("SCEN") ?? [])
        {
            if (lookup.Resolve<ISceneGetter>(record.FormKey) is not { } scene || scene.Quest.FormKeyNullable != quest.FormKey) continue;
            foreach (var action in scene.Actions)
                if (action.Type == SceneAction.TypeEnum.Dialog && !action.Topic.IsNull && action.ActorID is { } actor)
                    (result.TryGetValue(action.Topic.FormKey, out var set) ? set : result[action.Topic.FormKey] = []).Add(actor);
        }
        return result;
    }

    /// <summary>Voice types of actors, voice type lists and quest aliases (cached).</summary>
    private sealed class Speakers(RecordLookup lookup, Workspace workspace)
    {
        private readonly Dictionary<FormKey, HashSet<FormKey>> _actors = new();

        /// <summary>Voice types the conditions say the subject has: positive identity tests (OR-ed or not, they all name candidates).</summary>
        public HashSet<FormKey> Of(IReadOnlyList<IConditionGetter> conditions, IQuestGetter quest)
        {
            var result = new HashSet<FormKey>();
            foreach (var condition in conditions)
            {
                if (condition.Data.RunOnType != Condition.RunOnType.Subject || !Positive(condition)) continue;
                switch (condition.Data)
                {
                    case IGetIsIDConditionDataGetter id when id.Object.Link is { IsNull: false } link:
                        result.UnionWith(OfActor(link.FormKey));
                        break;
                    case IGetIsVoiceTypeConditionDataGetter voice when voice.VoiceTypeOrList.Link is { IsNull: false } link:
                        result.UnionWith(OfVoiceOrList(link.FormKey));
                        break;
                    case IGetIsAliasRefConditionDataGetter alias:
                        result.UnionWith(OfAlias(quest, alias.ReferenceAliasIndex));
                        break;
                }
            }
            if (result.Count > 0) return result;
            // No one named: the voices of the actors in a faction or of a class the subject must be (bards, guards…).
            foreach (var condition in conditions)
            {
                if (condition.Data.RunOnType != Condition.RunOnType.Subject || !Positive(condition)) continue;
                if (condition.Data is IGetInFactionConditionDataGetter { Faction.Link: { IsNull: false } faction })
                    result.UnionWith(Members().Factions.GetValueOrDefault(faction.FormKey) ?? []);
                else if (condition.Data is IGetIsClassConditionDataGetter { Class.Link: { IsNull: false } cls })
                    result.UnionWith(Members().Classes.GetValueOrDefault(cls.FormKey) ?? []);
            }
            return result;
        }

        private (Dictionary<FormKey, HashSet<FormKey>> Factions, Dictionary<FormKey, HashSet<FormKey>> Classes)? _members;

        /// <summary>Faction → voices of its member NPCs, class → voices of NPCs of it (built once).</summary>
        private (Dictionary<FormKey, HashSet<FormKey>> Factions, Dictionary<FormKey, HashSet<FormKey>> Classes) Members()
        {
            if (_members is { } built) return built;
            var factions = new Dictionary<FormKey, HashSet<FormKey>>();
            var classes = new Dictionary<FormKey, HashSet<FormKey>>();
            foreach (var record in lookup.Catalog.BySignature.GetValueOrDefault("NPC_") ?? [])
            {
                if (lookup.Resolve<INpcGetter>(record.FormKey) is not { } npc) continue;
                var voices = OfActor(npc.FormKey);
                if (voices.Count == 0) continue;
                foreach (var rank in npc.Factions)
                    if (!rank.Faction.IsNull) (factions.TryGetValue(rank.Faction.FormKey, out var set) ? set : factions[rank.Faction.FormKey] = []).UnionWith(voices);
                if (!npc.Class.IsNull) (classes.TryGetValue(npc.Class.FormKey, out var c) ? c : classes[npc.Class.FormKey] = []).UnionWith(voices);
            }
            _members = (factions, classes);
            return _members.Value;
        }

        private static bool Positive(IConditionGetter condition) => condition is IConditionFloatGetter f
            && ((condition.CompareOperator == CompareOperator.EqualTo && f.ComparisonValue >= 1)
                || (condition.CompareOperator == CompareOperator.NotEqualTo && f.ComparisonValue == 0)
                || (condition.CompareOperator is CompareOperator.GreaterThan && f.ComparisonValue < 1)
                || (condition.CompareOperator is CompareOperator.GreaterThanOrEqualTo && f.ComparisonValue is > 0 and <= 1));

        public HashSet<FormKey> OfVoiceOrList(FormKey key)
        {
            var result = new HashSet<FormKey>();
            var seen = new HashSet<FormKey>();
            void Walk(FormKey k)
            {
                if (!seen.Add(k)) return;
                if (lookup.Resolve<IVoiceTypeGetter>(k) is not null) result.Add(k);
                else if (lookup.Resolve<IFormListGetter>(k) is { } list)
                    foreach (var item in list.Items) if (!item.IsNull) Walk(item.FormKey);
            }
            Walk(key);
            return result;
        }

        /// <summary>The voice of an NPC (through its template when it uses the template's traits) or talking activator.</summary>
        public HashSet<FormKey> OfActor(FormKey key)
        {
            if (_actors.TryGetValue(key, out var cached)) return cached;
            var result = new HashSet<FormKey>();
            _actors[key] = result;
            var seen = new HashSet<FormKey>();
            for (var at = key; seen.Add(at);)
            {
                if (lookup.Resolve<INpcGetter>(at) is { } npc)
                {
                    var traits = npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits);
                    if (!traits && !npc.Voice.IsNull) { result.Add(npc.Voice.FormKey); break; }
                    if (npc.Template.IsNull) { if (!npc.Voice.IsNull) result.Add(npc.Voice.FormKey); break; }
                    if (lookup.Resolve<ILeveledNpcGetter>(npc.Template.FormKey) is { } leveled)
                    {
                        foreach (var entry in leveled.Entries ?? []) if (entry.Data?.Reference is { IsNull: false } r) result.UnionWith(OfActor(r.FormKey));
                        break;
                    }
                    at = npc.Template.FormKey;
                }
                else if (lookup.Resolve<ILeveledNpcGetter>(at) is { } leveled)
                {
                    foreach (var entry in leveled.Entries ?? []) if (entry.Data?.Reference is { IsNull: false } r) result.UnionWith(OfActor(r.FormKey));
                    break;
                }
                else if (lookup.Resolve<ITalkingActivatorGetter>(at) is { Voice.IsNull: false } talking) { result.Add(talking.Voice.FormKey); break; }
                else break;
            }
            return result;
        }

        /// <summary>A quest alias's speaker: its unique actor, the actor it creates, its own voice type list.</summary>
        public HashSet<FormKey> OfAlias(IQuestGetter quest, int aliasId)
        {
            var result = new HashSet<FormKey>();
            if (quest.Aliases.FirstOrDefault(a => a.ID == aliasId) is not { } alias) return result;
            if (!alias.UniqueActor.IsNull) result.UnionWith(OfActor(alias.UniqueActor.FormKey));
            if (alias.CreateReferenceToObject?.Object is { IsNull: false } created) result.UnionWith(OfActor(created.FormKey));
            // A placed actor: its base (typed lookup of placed NPCs only).
            if (!alias.ForcedReference.IsNull && workspace.LinkCache.TryResolve<IPlacedNpcGetter>(alias.ForcedReference.FormKey, out var placed) && !placed.Base.IsNull)
                result.UnionWith(OfActor(placed.Base.FormKey));
            if (!alias.VoiceTypes.IsNull) result.UnionWith(OfVoiceOrList(alias.VoiceTypes.FormKey));
            if (result.Count == 0) result.UnionWith(Of(alias.Conditions, quest));
            return result;
        }
    }
}
