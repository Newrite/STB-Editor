using System.Numerics;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Assets;
using StbEditor.Data;
using StbEditor.Nif;

namespace StbEditor.Render;

/// <summary>An NPC's FaceGen files, built: the head NIF and the tint DDS, with notes on what was used or skipped.</summary>
public sealed record FaceGenFiles(byte[] Nif, byte[] Tint, IReadOnlyList<string> Parts, IReadOnlyList<string> Notes);

/// <summary>
/// Export FaceGen (CK: Ctrl+F4) for one NPC: the head parts it ends up with (the race's defaults for its sex, each
/// replaced by the NPC's part of the same type, plus extra parts), morphed by <see cref="FaceGenMorphs"/> and put
/// together by <see cref="FaceGenNif"/>; textures from the NPC's head texture set or the parts' own; hair colour on
/// hair tint shaders, skin colour on skin tint shaders; the tint texture from <see cref="FaceGenTint"/>.
/// </summary>
public static class FaceGenExport
{
    public static string NifPath(FormKey npc) => ActorAppearance.FaceGenPath(npc);

    public static string TintPath(FormKey npc) =>
        $@"textures\actors\character\facegendata\facetint\{npc.ModKey.FileName}\{npc.ID:X8}.dds".ToLowerInvariant();

    /// <summary>What the face shape's tint slot names (the CK's spelling).</summary>
    public static string TintSlot(FormKey npc) => $@"data\Textures\Actors\Character\FaceGenData\FaceTint\{npc.ModKey.FileName}\{npc.ID:X8}.dds";

    /// <summary>Why an NPC gets no FaceGen of its own (its face comes from a template, no race), or null.</summary>
    public static string? Unsupported(INpcGetter npc, IRaceGetter? race)
    {
        if (npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) && !npc.Template.IsNull) return "takes its traits (face) from a template";
        if (race is null) return "race not found";
        if (!race.Flags.HasFlag(Race.Flag.FaceGenHead)) return $"race {race.EditorID} has no FaceGen head";
        return null;
    }

    /// <param name="read">Asset bytes by path (meshes\… / textures\…), as the game would find them.</param>
    public static FaceGenFiles Build(INpcGetter npc, RecordLookup lookup, Func<string, byte[]?> read)
    {
        var race = lookup.Resolve<IRaceGetter>(npc.Race.FormKey) ?? throw new InvalidOperationException("race not found");
        var female = npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
        var head = female ? race.HeadData?.Female : race.HeadData?.Male;
        var morphRace = (race.MorphRace.IsNull ? race : lookup.Resolve<IRaceGetter>(race.MorphRace.FormKey) ?? race).EditorID;
        var vampire = (race.Keywords ?? []).Any(k => lookup.Resolve<IKeywordGetter>(k.FormKey)?.EditorID == "Vampire");
        var notes = new List<string>();

        var parts = HeadParts(npc, head, lookup);
        var hairColor = lookup.Resolve<IColorRecordGetter>(npc.HairColor.FormKey)?.Color
            ?? lookup.Resolve<IColorRecordGetter>((female ? race.DefaultHairColors?.Female : race.DefaultHairColors?.Male)?.FormKey ?? FormKey.Null)?.Color;
        var skin = npc.TextureLighting;
        var headTexture = lookup.Resolve<ITextureSetGetter>(npc.HeadTexture.FormKey);

        var built = new List<FaceGenPart>();
        foreach (var part in parts)
        {
            var file = part.Model?.File.GivenPath;
            if (string.IsNullOrEmpty(file)) continue; // placeholder parts ("no scar") have no mesh
            if (read(AssetFileSystem.MeshPath(file)) is not { } bytes)
            {
                notes.Add($"{part.EditorID}: mesh {file} not found, left out");
                continue;
            }
            NifDocument source;
            Vector3[] positions;
            try
            {
                source = NifDocument.Read(bytes);
                var shape = NifFile.Load(bytes, file, raw: true).Shapes.FirstOrDefault();
                if (shape is null) { notes.Add($"{part.EditorID}: no shape in {file}, left out"); continue; }
                positions = FaceGenMorphs.Apply(shape.Positions, npc, morphRace, vampire, part, read, out _);
            }
            catch (Exception ex)
            {
                notes.Add($"{part.EditorID}: {ex.Message}, left out");
                continue;
            }

            var set = part.Type == HeadPart.TypeEnum.Face && headTexture is not null ? headTexture : lookup.Resolve<ITextureSetGetter>(part.TextureSet.FormKey);
            var textures = set is null ? new string?[9] : Slots(set);
            if (part.Type == HeadPart.TypeEnum.Face) textures[6] = TintSlot(npc.FormKey);

            Vector3? tint = null;
            if (part.Type is HeadPart.TypeEnum.Hair or HeadPart.TypeEnum.FacialHair or HeadPart.TypeEnum.Eyebrows or HeadPart.TypeEnum.Misc && hairColor is { } hc)
                tint = new Vector3(hc.R, hc.G, hc.B) * 2f / 255f;
            if (part.Type == HeadPart.TypeEnum.Scars && skin is { } sc) tint = new Vector3(sc.R, sc.G, sc.B) / 255f;

            built.Add(new FaceGenPart
            {
                Name = part.EditorID ?? file, Source = source, Positions = positions, Textures = textures, TintColor = tint,
                TexturesAsGiven = part.Type == HeadPart.TypeEnum.Face,
            });
        }

        var nif = FaceGenNif.Build($"{npc.FormKey.ID:X8}.NIF", built);
        var tintImage = FaceGenTint.Compose(npc, head, read);
        return new FaceGenFiles(nif, tintImage.EncodeDxt5(), built.Select(b => b.Name).ToList(), notes);
    }

    /// <summary>
    /// The head parts of a FaceGen head: the race's default parts for the sex, each one replaced by the NPC's part of
    /// the same type, and every part's extra parts after it.
    /// </summary>
    public static List<IHeadPartGetter> HeadParts(INpcGetter npc, IHeadDataGetter? head, RecordLookup lookup)
    {
        var chosen = new List<IHeadPartGetter>();
        var own = npc.HeadParts.Select(h => lookup.Resolve<IHeadPartGetter>(h.FormKey)).OfType<IHeadPartGetter>().ToList();
        var ownTypes = own.Select(p => p.Type).ToHashSet();
        foreach (var reference in head?.HeadParts ?? [])
        {
            if (lookup.Resolve<IHeadPartGetter>(reference.Head.FormKey) is not { } part) continue;
            if (ownTypes.Contains(part.Type)) continue;
            chosen.Add(part);
        }
        chosen.AddRange(own);

        var result = new List<IHeadPartGetter>();
        var seen = new HashSet<FormKey>();
        void Add(IHeadPartGetter part, int depth)
        {
            if (depth > 4 || !seen.Add(part.FormKey)) return;
            result.Add(part);
            foreach (var extra in part.ExtraParts)
                if (lookup.Resolve<IHeadPartGetter>(extra.FormKey) is { } e) Add(e, depth + 1);
        }
        foreach (var part in chosen) Add(part, 0);
        return result;
    }

    /// <summary>
    /// A texture set's maps in NIF slot order: diffuse, normal, glow / skin (TX03), height / detail (TX04), environment
    /// (TX05), environment mask (TX02), multilayer / tint (TX06), backlight / specular (TX07).
    /// </summary>
    private static string?[] Slots(ITextureSetGetter set) =>
    [
        set.Diffuse?.GivenPath, set.NormalOrGloss?.GivenPath, set.GlowOrDetailMap?.GivenPath, set.Height?.GivenPath,
        set.Environment?.GivenPath, set.EnvironmentMaskOrSubsurfaceTint?.GivenPath, set.Multilayer?.GivenPath, set.BacklightMaskOrSpecular?.GivenPath, null,
    ];
}
