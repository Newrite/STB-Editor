using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace StbEditor.Core;

/// <summary>A place that uses a quest alias: the record and what in it.</summary>
public sealed record AliasUse(FormKey Record, string Where);

/// <summary>
/// The Creation Kit's alias Use Report: where a quest alias is used — the quest's conditions (run on the alias,
/// alias parameters such as GetIsAliasRef / GetLocationAliasCleared), objectives, other aliases (fill from this
/// alias), script properties and alias scripts; the quest's scenes (actors, actions), packages (alias targets,
/// conditions) and dialogue lines (conditions).
/// </summary>
public static class AliasUses
{
    /// <summary>How a condition uses the alias (null: it does not).</summary>
    public static string? InCondition(IConditionGetter condition, int alias)
    {
        var data = condition.Data;
        if (data.RunOnType == Condition.RunOnType.QuestAlias && data.RunOnTypeIndex == alias) return $"{Conditions.FunctionName(data)} (run on)";
        foreach (var parameter in Conditions.Parameters(data))
        {
            object? value;
            try { value = parameter.GetValue(data); } catch { continue; }
            if (parameter.Name.EndsWith("AliasIndex", StringComparison.Ordinal) && value is int index && index == alias) return Conditions.FunctionName(data);
            if (data.UseAliases && value is not null && Conditions.IsLinkOrIndex(value.GetType())
                && value.GetType().GetProperty("Index")?.GetValue(value) is uint number && number == alias)
                return Conditions.FunctionName(data);
        }
        return null;
    }

    private static IEnumerable<string> InConditions(IEnumerable<IConditionGetter>? conditions, int alias, string where) =>
        (conditions ?? []).Select(c => InCondition(c, alias)).OfType<string>().Select(f => $"{where}: {f}");

    public static List<AliasUse> InQuest(IQuestGetter quest, int alias)
    {
        var uses = new List<string>();
        uses.AddRange(InConditions(quest.DialogConditions, alias, "Dialogue conditions"));
        uses.AddRange(InConditions(quest.EventConditions, alias, "Event conditions"));
        foreach (var stage in quest.Stages)
            for (var i = 0; i < stage.LogEntries.Count; i++)
                uses.AddRange(InConditions(stage.LogEntries[i].Conditions, alias, $"Stage {stage.Index} log entry {i + 1}"));
        foreach (var objective in quest.Objectives)
            foreach (var target in objective.Targets)
            {
                if (target.AliasID == alias) uses.Add($"Objective {objective.Index}: target");
                uses.AddRange(InConditions(target.Conditions, alias, $"Objective {objective.Index} target conditions"));
            }
        foreach (var other in quest.Aliases)
        {
            var name = $"Alias {other.ID} {other.Name}";
            uses.AddRange(InConditions(other.Conditions, alias, $"{name} conditions"));
            if (other.ID == alias) continue;
            // Fill from this alias: near it, in its location, created at it… (another quest's alias is not ours).
            foreach (var leaf in RecordFields.Leaves(other, maxDepth: 4))
            {
                var path = leaf.PathText;
                if (path.Contains("External", StringComparison.Ordinal) || path.Contains("Conditions", StringComparison.Ordinal)) continue;
                if (leaf.Path[^1] is string member && member.Contains("Alias", StringComparison.Ordinal) && member != "ID"
                    && leaf.Value is int or uint or short or ushort or byte or sbyte && Convert.ToInt64(leaf.Value) == alias)
                    uses.Add($"{name}: {path}");
            }
        }
        if (quest.VirtualMachineAdapter is { } vmad)
        {
            foreach (var script in vmad.Scripts)
                foreach (var property in script.Properties.OfType<IScriptObjectPropertyGetter>())
                    if (property.Alias == alias && property.Object.FormKey == quest.FormKey) uses.Add($"Script {script.Name}: property {property.Name}");
            foreach (var aliasScripts in vmad.Aliases)
                if (aliasScripts.Property.Alias == alias && aliasScripts.Scripts.Count > 0)
                    uses.Add($"Alias scripts: {string.Join(", ", aliasScripts.Scripts.Select(s => s.Name))}");
        }
        return uses.Select(w => new AliasUse(quest.FormKey, w)).ToList();
    }

    public static IEnumerable<AliasUse> InScene(ISceneGetter scene, int alias)
    {
        var uses = new List<string>();
        if (scene.Actors.Any(a => a.ID == alias)) uses.Add("Actor");
        foreach (var action in scene.Actions)
        {
            if (action.Type != SceneAction.TypeEnum.Timer && action.ActorID == alias) uses.Add($"Action {action.Index} ({action.Type})");
            if (action.HeadtrackActorID == alias) uses.Add($"Action {action.Index}: head-tracks");
        }
        uses.AddRange(InConditions(scene.Conditions, alias, "Conditions"));
        for (var i = 0; i < scene.Phases.Count; i++)
        {
            uses.AddRange(InConditions(scene.Phases[i].StartConditions, alias, $"Phase {i + 1} start conditions"));
            uses.AddRange(InConditions(scene.Phases[i].CompletionConditions, alias, $"Phase {i + 1} completion conditions"));
        }
        return uses.Select(w => new AliasUse(scene.FormKey, w));
    }

    public static IEnumerable<AliasUse> InPackage(IPackageGetter package, int alias)
    {
        var uses = new List<string>();
        foreach (var (key, data) in package.Data)
            if (data is IPackageDataTargetGetter { Target: IPackageTargetAliasGetter target } && target.Alias == alias) uses.Add($"Data input {key}: target");
        uses.AddRange(InConditions(package.Conditions, alias, "Conditions"));
        for (var i = 0; i < package.ProcedureTree.Count; i++)
            uses.AddRange(InConditions(package.ProcedureTree[i].Conditions, alias, $"Procedure {i + 1} conditions"));
        return uses.Select(w => new AliasUse(package.FormKey, w));
    }

    public static IEnumerable<AliasUse> InLine(IDialogResponsesGetter line, int alias) =>
        InConditions(line.Conditions, alias, "Conditions").Select(w => new AliasUse(line.FormKey, w));
}
