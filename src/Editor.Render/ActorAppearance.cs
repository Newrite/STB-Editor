using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Assets;
using StbEditor.Data;

namespace StbEditor.Render;

/// <summary>What an NPC looks like in the bind pose: the meshes to combine and the height.</summary>
public sealed record ActorLook(IReadOnlyList<string> Models, float Scale, bool Female, FormKey Race, FormKey Traits)
{
    /// <summary>How each part was chosen (armor, addon, biped slots) — for diagnostics.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>MeshCache key for the combined model (see <see cref="MeshCache"/>).</summary>
    public string CompositePath => ActorAppearance.CompositePrefix + string.Join('|', Models);
}

/// <summary>
/// Resolves an NPC_ or LVLN base into body meshes the way the game dresses an actor:
/// templates (traits / inventory), race, default outfit (leveled lists resolved to their first
/// entry), worn armor or race skin for the uncovered slots, and the pre-baked FaceGen head.
/// No skeleton or animation is applied — actors stand in their bind pose.
/// </summary>
public sealed class ActorAppearance(RecordLookup lookup)
{
    public const string CompositePrefix = "actor:";

    /// <summary>In a composite part, "primary.nif?alt1.nif,alt2.nif": the alternatives load when the primary is missing.</summary>
    public const char FallbackSeparator = '?';
    private const int MaxDepth = 8;

    /// <summary>Biped slot 32 (Body) in Mutagen's BipedObjectFlag numbering.</summary>
    private const uint BodySlot = 0x4;

    private readonly Dictionary<FormKey, ActorLook?> _cache = new();

    public ActorLook? Resolve(FormKey baseKey)
    {
        if (_cache.TryGetValue(baseKey, out var cached)) return cached;
        ActorLook? look = null;
        try
        {
            look = Build(baseKey);
        }
        catch
        {
            // A broken template chain or record: the actor stays a marker.
        }
        _cache[baseKey] = look;
        return look;
    }

    private ActorLook? Build(FormKey baseKey)
    {
        var npc = PickNpc(baseKey, 0);
        if (npc is null) return null;

        var traits = FollowTemplate(npc, NpcConfiguration.TemplateFlag.Traits);
        var inventory = FollowTemplate(npc, NpcConfiguration.TemplateFlag.Inventory);
        var race = lookup.Resolve<IRaceGetter>(traits.Race.FormKey);
        if (race is null) return null;

        var female = traits.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
        var races = new HashSet<FormKey> { race.FormKey };
        if (!race.ArmorRace.IsNull) races.Add(race.ArmorRace.FormKey);

        var models = new List<string>();
        var notes = new List<string>();
        var usedSlots = 0u;

        // Outfit first: whatever it covers hides the skin underneath.
        if (!inventory.DefaultOutfit.IsNull && lookup.Resolve<IOutfitGetter>(inventory.DefaultOutfit.FormKey) is { } outfit)
        {
            foreach (var item in outfit.Items ?? [])
                foreach (var armor in Armors(item.FormKey, 0))
                    AddArmor(armor, races, female, models, notes, ref usedSlots, onlyFreeSlots: false);
        }

        var skinKey = !traits.WornArmor.IsNull ? traits.WornArmor.FormKey : race.Skin.FormKey;
        if (lookup.Resolve<IArmorGetter>(skinKey) is { } skin)
            AddArmor(skin, races, female, models, notes, ref usedSlots, onlyFreeSlots: true);

        // Heads (with hair, eyes, brows) are baked per NPC into FaceGen geometry; when the file
        // is missing the NPC's (or the race's default) head parts stand in, unmorphed.
        if (race.Flags.HasFlag(Race.Flag.FaceGenHead))
        {
            var fallback = HeadParts(traits, race, female);
            models.Add(fallback.Count > 0 ? FaceGenPath(traits.FormKey) + FallbackSeparator + string.Join(',', fallback) : FaceGenPath(traits.FormKey));
        }

        if (models.Count == 0) return null;

        var raceHeight = female ? race.Height.Female : race.Height.Male;
        var height = traits.Height > 0 ? traits.Height : 1f;
        var scale = height * (raceHeight > 0 ? raceHeight : 1f);
        return new ActorLook(models, scale, female, race.FormKey, traits.FormKey) { Notes = notes };
    }

    /// <summary>FaceGen head NIF: plugin that defines the NPC, local FormID with a zero load-order byte.</summary>
    public static string FaceGenPath(FormKey npc) =>
        $@"meshes\actors\character\facegendata\facegeom\{npc.ModKey.FileName}\{npc.ID:X8}.nif".ToLowerInvariant();

    /// <summary>Head part meshes (head, eyes, hair, brows…) with their extra parts.</summary>
    private List<string> HeadParts(INpcGetter npc, IRaceGetter race, bool female)
    {
        var links = npc.HeadParts.Select(h => h.FormKey).ToList();
        if (links.Count == 0)
        {
            var data = female ? race.HeadData?.Female : race.HeadData?.Male;
            links.AddRange((data?.HeadParts ?? []).Where(h => !h.Head.IsNull).Select(h => h.Head.FormKey));
        }

        var result = new List<string>();
        var seen = new HashSet<FormKey>();
        var queue = new Queue<FormKey>(links);
        while (queue.Count > 0 && result.Count < 16)
        {
            var key = queue.Dequeue();
            if (!seen.Add(key) || lookup.Resolve<IHeadPartGetter>(key) is not { } part) continue;
            if (part.Model?.File.GivenPath is { Length: > 0 } file) result.Add(AssetFileSystem.MeshPath(file));
            foreach (var extra in part.ExtraParts) queue.Enqueue(extra.FormKey);
        }
        return result;
    }

    /// <summary>An NPC_ for a base form; leveled actors give their first entry.</summary>
    private INpcGetter? PickNpc(FormKey key, int depth)
    {
        if (depth > MaxDepth || key.IsNull) return null;
        if (lookup.Resolve<INpcGetter>(key) is { } npc) return npc;
        if (lookup.Resolve<ILeveledNpcGetter>(key) is { } leveled)
        {
            foreach (var entry in (leveled.Entries ?? []).OrderBy(e => e.Data?.Level ?? 0))
                if (entry.Data?.Reference.FormKey is { } next && PickNpc(next, depth + 1) is { } found)
                    return found;
        }
        return null;
    }

    /// <summary>The NPC whose data is used for <paramref name="flag"/> (the template chain).</summary>
    private INpcGetter FollowTemplate(INpcGetter npc, NpcConfiguration.TemplateFlag flag)
    {
        var current = npc;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            if (!current.Configuration.TemplateFlags.HasFlag(flag) || current.Template.IsNull) break;
            if (PickNpc(current.Template.FormKey, depth + 1) is not { } next || next.FormKey == current.FormKey) break;
            current = next;
        }
        return current;
    }

    /// <summary>Armors an outfit entry stands for: an ARMO, or a leveled item's entries.</summary>
    private IEnumerable<IArmorGetter> Armors(FormKey key, int depth)
    {
        if (depth > MaxDepth || key.IsNull) yield break;
        if (lookup.Resolve<IArmorGetter>(key) is { } armor)
        {
            yield return armor;
            yield break;
        }

        if (lookup.Resolve<ILeveledItemGetter>(key) is not { } leveled) yield break;
        var entries = (leveled.Entries ?? []).Where(e => e.Data is not null).OrderBy(e => e.Data!.Level).ToList();
        if (entries.Count == 0) yield break;

        var all = leveled.Flags.HasFlag(LeveledItem.Flag.UseAll);
        foreach (var entry in all ? entries : entries.Take(1))
            foreach (var inner in Armors(entry.Data!.Reference.FormKey, depth + 1))
                yield return inner;
    }

    private void AddArmor(IArmorGetter armor, HashSet<FormKey> races, bool female, List<string> models, List<string> notes, ref uint usedSlots, bool onlyFreeSlots)
    {
        // Enchanted variants often only point at their template.
        for (var depth = 0; armor.Armature.Count == 0 && !armor.TemplateArmor.IsNull && depth < MaxDepth; depth++)
        {
            if (lookup.Resolve<IArmorGetter>(armor.TemplateArmor.FormKey) is not { } template) break;
            armor = template;
        }

        var armorSlots = (uint)(armor.BodyTemplate?.FirstPersonFlags ?? 0);
        if (!onlyFreeSlots && (armorSlots & usedSlots) != 0 && armorSlots != 0) return;

        foreach (var link in armor.Armature)
        {
            if (lookup.Resolve<IArmorAddonGetter>(link.FormKey) is not { } addon) continue;
            if (!Fits(addon, races)) continue;

            var slots = (uint)(addon.BodyTemplate?.FirstPersonFlags ?? 0);
            // A skin part goes when its main slot is worn: the body slot if it has one (naked torso
            // under a shirt; a draugr body under just a helmet stays), else its lowest slot.
            var main = (slots & BodySlot) != 0 ? BodySlot : slots & (~slots + 1);
            if (onlyFreeSlots && (main & usedSlots) != 0)
            {
                notes.Add($"{armor.EditorID} / {addon.EditorID}: slots 0x{slots:X} covered by 0x{usedSlots:X}");
                continue;
            }

            var model = female ? addon.WorldModel?.Female ?? addon.WorldModel?.Male : addon.WorldModel?.Male ?? addon.WorldModel?.Female;
            var file = model?.File.GivenPath;
            if (string.IsNullOrWhiteSpace(file)) continue;

            var path = AssetFileSystem.MeshPath(file);
            if (!models.Contains(path, StringComparer.OrdinalIgnoreCase)) models.Add(path);
            notes.Add($"{armor.EditorID} / {addon.EditorID}: slots 0x{slots:X} (used before 0x{usedSlots:X}) -> {path}");
            usedSlots |= slots;
        }
    }

    private static bool Fits(IArmorAddonGetter addon, HashSet<FormKey> races) =>
        races.Contains(addon.Race.FormKey) || addon.AdditionalRaces.Any(r => races.Contains(r.FormKey));
}
