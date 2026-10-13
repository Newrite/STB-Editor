using System.Collections.Concurrent;
using System.Numerics;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Nif;

namespace StbEditor.Render;

/// <summary>
/// A head part's vertices as the Creation Kit bakes them into an NPC's FaceGen head: the part's mesh plus the morphs of
/// its TRI files — the race morph (the NPC's race, or the race it morphs as), the weight morph (SkinnyMorph at
/// 1 − weight/100), the face presets (nose, eyes, mouth types) at full weight and each face slider on the morph of its
/// direction. Checked against the CK's own files with <c>stbedit-cli facegencheck</c>.
/// </summary>
public static class FaceGenMorphs
{
    /// <summary>Face slider (NPC_ NAM9 order) → morph for a positive value, morph for a negative one.</summary>
    public static readonly (string Slider, string Plus, string Minus)[] Sliders =
    [
        ("NoseLongVsShort", "NoseLong", "NoseShort"),
        ("NoseUpVsDown", "NoseUp", "NoseDown"),
        ("JawUpVsDown", "JawDown", "JawUp"),
        ("JawNarrowVsWide", "JawWide", "JawNarrow"),
        ("JawForwardVsBack", "JawForward", "JawBack"),
        ("CheeksUpVsDown", "CheeksUp", "CheeksDown"),
        ("CheeksForwardVsBack", "CheeksOut", "CheeksIn"),
        ("EyesUpVsDown", "EyesMoveUp", "EyesMoveDown"),
        ("EyesInVsOut", "EyesMoveOut", "EyesMoveIn"),
        ("BrowsUpVsDown", "BrowUp", "BrowDown"),
        ("BrowsInVsOut", "BrowOut", "BrowIn"),
        ("BrowsForwardVsBack", "BrowForward", "BrowBack"),
        ("LipsUpVsDown", "LipMoveUp", "LipMoveDown"),
        ("LipsInVsOut", "LipMoveOut", "LipMoveIn"),
        ("ChinNarrowVsWide", "ChinWide", "ChinThin"),
        ("ChinUpVsDown", "ChinMoveDown", "ChinMoveUp"),
        ("ChinUnderbiteVsOverbite", "Underbite", "Overbite"),
        ("EyesForwardVsBack", "EyesForward", "EyesBack"),
    ];

    private static readonly ConcurrentDictionary<string, TriFile?> Tris = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="read">Asset bytes by path under meshes\.</param>
    /// <param name="morphRace">EditorID of the race whose race morph applies: the NPC's race, or its Morph Race (vampire, afflicted…).</param>
    /// <param name="vampire">The race is a vampire race (keyword Vampire): the chargen VampireMorph applies at full weight.</param>
    public static Vector3[] Apply(Vector3[] positions, INpcGetter npc, string? morphRace, bool vampire, IHeadPartGetter part, Func<string, byte[]?> read, out string used)
    {
        var result = (Vector3[])positions.Clone();
        var notes = new List<string>();

        foreach (var p in part.Parts)
        {
            if (p.FileName?.GivenPath is not { Length: > 0 } path) continue;
            var tri = Tris.GetOrAdd(path, f =>
            {
                try { return read(StbEditor.Assets.AssetFileSystem.MeshPath(f)) is { } bytes ? TriFile.Load(bytes) : null; }
                catch { return null; }
            });
            if (tri is null || tri.Vertices.Length != result.Length) continue;

            switch (p.PartType)
            {
                case Part.PartTypeEnum.RaceMorph:
                    Add(tri, morphRace, 1f);
                    break;
                case Part.PartTypeEnum.Tri:
                    Add(tri, "SkinnyMorph", 1f - npc.Weight / 100f);
                    break;
                case Part.PartTypeEnum.ChargenMorph:
                    if (vampire) Add(tri, "VampireMorph", 1f);
                    if (npc.FaceParts is { } presets)
                    {
                        Add(tri, $"NoseType{presets.Nose}", 1f);
                        Add(tri, $"EyesType{presets.Eyes}", 1f);
                        Add(tri, $"LipType{presets.Mouth}", 1f);
                    }
                    if (npc.FaceMorph is { } morph)
                    {
                        foreach (var (slider, plus, minus) in Sliders)
                        {
                            var value = (float)(typeof(INpcFaceMorphGetter).GetProperty(slider)?.GetValue(morph) ?? 0f);
                            if (value > 0) Add(tri, plus, value);
                            else if (value < 0) Add(tri, minus, -value);
                        }
                    }
                    break;
            }
        }

        used = string.Join(" ", notes);
        return result;

        void Add(TriFile tri, string? name, float weight)
        {
            if (name is null || weight == 0 || tri.Morph(name) is not { } morph) return;
            for (var i = 0; i < result.Length; i++) result[i] += morph.Deltas[i] * weight;
            notes.Add($"{name}x{weight:0.###}");
        }
    }
}
