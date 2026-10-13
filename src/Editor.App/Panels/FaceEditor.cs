using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Data;
using StbEditor.Nif;
using StbEditor.Render;
using Scene = StbEditor.Render.Scene;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// The NPC's Appearance tab as a face editor (CK: Character Gen Parts, Face Morphs, Tints): head parts by type, the
/// nose / eyes / mouth presets, the 18 face sliders, weight, hair and skin colour, tint layers — with the head built
/// live next to them (the same FaceGen export, in memory). A slider previews while it moves and becomes one undo
/// step when let go. «Export FaceGen» writes the head for the game.
/// </summary>
internal static class FaceEditor
{
    public static bool Draws(FormTab tab) => tab.Title == "Appearance";

    private static FormKey _key;
    private static IMajorRecordGetter? _source;
    private static Npc? _working;
    private static bool _pending;
    private static int _version;
    private static int _builtVersion = -1;
    private static Task<(byte[] Nif, byte[] Tint, int Version, string TintPath, IReadOnlyList<string> Notes)>? _build;
    private static string? _nifPath, _tintPath;
    private static IReadOnlyList<string> _notes = [];
    private static Scene? _scene;
    private static ViewportTarget? _target;
    private static readonly Camera Camera = new();
    private static float _yaw = MathF.PI, _pitch = -0.05f, _zoom = 0.62f;
    private static RecordLookup? _lookup;
    private static object? _lookupFor;
    private static readonly Dictionary<(FormKey Race, bool Female, HeadPart.TypeEnum Type), List<IHeadPartGetter>> Candidates = new();
    private static readonly Dictionary<string, (int Nose, int Eyes, int Lips)> PresetCounts = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HeadPart.TypeEnum[] PartTypes =
        [HeadPart.TypeEnum.Hair, HeadPart.TypeEnum.FacialHair, HeadPart.TypeEnum.Eyebrows, HeadPart.TypeEnum.Eyes, HeadPart.TypeEnum.Scars, HeadPart.TypeEnum.Face];

    /// <summary>Whether a build of the preview head is running (keeps frames coming).</summary>
    public static bool IsBusy => _build is not null;

    /// <summary>The preview shows what is set now (autotests).</summary>
    public static bool PreviewCurrent => _build is null && _builtVersion == _version && _scene is not null;

    private static EditTarget? _lastTarget;

    /// <summary>A slider set as a person would, let go at <paramref name="value"/> (autotests).</summary>
    public static bool SetSliderForTest(string slider, float value)
    {
        if (_working?.FaceMorph is not { } morph || typeof(NpcFaceMorph).GetProperty(slider) is not { } property) return false;
        property.SetValue(morph, value);
        Moved();
        Apply(_lastTarget, T(SliderName(slider)));
        return true;
    }

    public static void Draw(EditorApp app, INpcGetter npc, FormTab tab, EditTarget? target, Action drawFields)
    {
        var session = app.Session!;
        if (!ReferenceEquals(_lookupFor, session)) { _lookup = new RecordLookup(session.Workspace, session.Catalog); _lookupFor = session; Candidates.Clear(); }
        var lookup = _lookup!;
        if (_key != npc.FormKey || (!_pending && !ReferenceEquals(_source, npc)))
        {
            _key = npc.FormKey;
            _source = npc;
            _working = (Npc)npc.DeepCopy();
            _version++;
        }
        var working = _working!;
        _lastTarget = target;
        var race = lookup.Resolve<IRaceGetter>(working.Race.FormKey);
        var female = working.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
        var head = female ? race?.HeadData?.Female : race?.HeadData?.Male;
        var why = FaceGenExport.Unsupported(working, race);

        if (why is not null)
        {
            var reason = race is null ? T("race not found")
                : !race.Flags.HasFlag(Race.Flag.FaceGenHead) ? TF($"race {race.EditorID} has no FaceGen head")
                : T("takes its face from a template (Use Traits): no FaceGen of its own");
            ImGui.TextColored(Theme.TextDim, TF($"No face editor for this NPC: {reason}."));
            drawFields();
            return;
        }

        UpdatePreview(app, lookup);
        var width = ImGui.GetContentRegionAvail().X;
        var previewWidth = Math.Clamp(width * 0.42f, 160f, 520f);
        if (ImGui.BeginTable("##face", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("controls", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("preview", ImGuiTableColumnFlags.WidthFixed, previewWidth);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawControls(app, working, race!, head, female, target, lookup);
            ImGui.TableNextColumn();
            DrawPreview(app, previewWidth);
            ImGui.EndTable();
        }

        if (ImGui.CollapsingHeader(T("All appearance fields") + "##rawface")) drawFields();
    }

    // ------------------------------------------------------------------ controls

    private static void DrawControls(EditorApp app, Npc working, IRaceGetter race, IHeadDataGetter? head, bool female, EditTarget? target, RecordLookup lookup)
    {
        var editable = target is not null;
        if (!editable) ImGui.TextColored(Theme.TextDim, T("Read only here: this record is not edited in the active file."));
        ImGui.BeginDisabled(!editable);

        ImGui.SeparatorText(T("Head parts"));
        var chosen = FaceGenExport.HeadParts(working, head, lookup);
        foreach (var type in PartTypes)
        {
            var current = chosen.FirstOrDefault(p => p.Type == type && !p.Flags.HasFlag(HeadPart.Flag.IsExtraPart));
            var own = working.HeadParts.Select(h => lookup.Resolve<IHeadPartGetter>(h.FormKey)).FirstOrDefault(p => p?.Type == type);
            var label = current is null ? T("(none)") : current.EditorID + (own is null ? T("  (race default)") : "");
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - ImGui.GetFontSize() * 7);
            if (ImGui.BeginCombo(T(TypeName(type)) + "##part" + type, label, ImGuiComboFlags.HeightLarge))
            {
                if (ImGui.Selectable(T("(race default)"), own is null))
                    Change(target, T("Head part"), () => working.HeadParts.RemoveAll(h => lookup.Resolve<IHeadPartGetter>(h.FormKey)?.Type == type));
                if (type is HeadPart.TypeEnum.FacialHair or HeadPart.TypeEnum.Scars && ImGui.Selectable(T("(none)"), false))
                    Change(target, T("Head part"), () => working.HeadParts.RemoveAll(h => lookup.Resolve<IHeadPartGetter>(h.FormKey)?.Type == type));
                foreach (var part in CandidatesFor(app, lookup, race, female, type))
                {
                    if (!ImGui.Selectable(part.EditorID + "##" + part.FormKey, current?.FormKey == part.FormKey)) continue;
                    var pick = part.FormKey;
                    Change(target, T("Head part"), () =>
                    {
                        working.HeadParts.RemoveAll(h => lookup.Resolve<IHeadPartGetter>(h.FormKey)?.Type == type);
                        working.HeadParts.Add(new FormLink<IHeadPartGetter>(pick));
                    });
                }
                ImGui.EndCombo();
            }
        }

        // Colours.
        var hair = lookup.Resolve<IColorRecordGetter>(working.HairColor.FormKey);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - ImGui.GetFontSize() * 7);
        if (ImGui.BeginCombo(T("Hair colour") + "##hair", hair?.EditorID ?? T("(race default)"), ImGuiComboFlags.HeightLarge))
        {
            foreach (var record in app.Session!.Catalog.BySignature.GetValueOrDefault("CLFM") ?? [])
            {
                if (record.EditorId?.Contains("Hair", StringComparison.OrdinalIgnoreCase) != true) continue;
                if (!ImGui.Selectable(record.EditorId + "##" + record.FormKey, record.FormKey == working.HairColor.FormKey)) continue;
                var pick = record.FormKey;
                Change(target, T("Hair colour"), () => working.HairColor.SetTo(pick));
            }
            ImGui.EndCombo();
        }

        var skin = working.TextureLighting ?? System.Drawing.Color.FromArgb(0, 255, 255, 255);
        var skinColor = new Vector3(skin.R, skin.G, skin.B) / 255f;
        if (ImGui.ColorEdit3(T("Skin colour") + "##skin", ref skinColor, ImGuiColorEditFlags.NoInputs))
        {
            var c = System.Drawing.Color.FromArgb(0, (int)(skinColor.X * 255), (int)(skinColor.Y * 255), (int)(skinColor.Z * 255));
            working.TextureLighting = c;
            // The skin tone layer follows (the CK keeps them together).
            var tone = head?.TintMasks.FirstOrDefault(m => m.MaskType == TintAssets.TintMaskType.SkinTone);
            if (tone?.Index is { } toneIndex && working.TintLayers.FirstOrDefault(l => l.Index == toneIndex) is { } toneLayer) toneLayer.Color = c;
            Moved();
        }
        Commit(target, T("Skin colour"));
        ImGui.SameLine();
        var weight = working.Weight;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 8);
        if (ImGui.SliderFloat(T("Weight") + "##weight", ref weight, 0, 100, "%.0f")) { working.Weight = weight; Moved(); }
        Commit(target, T("Weight"));

        // Presets.
        ImGui.SeparatorText(T("Face"));
        var counts = Presets(app, chosen.FirstOrDefault(p => p.Type == HeadPart.TypeEnum.Face));
        working.FaceParts ??= new NpcFaceParts();
        var parts = working.FaceParts;
        Preset(T("Nose type") + "##nose", parts.Nose, counts.Nose, v => parts.Nose = v, target);
        Preset(T("Eyes type") + "##eyes", parts.Eyes, counts.Eyes, v => parts.Eyes = v, target);
        Preset(T("Mouth type") + "##mouth", parts.Mouth, counts.Lips, v => parts.Mouth = v, target);

        // The 18 sliders.
        working.FaceMorph ??= new NpcFaceMorph();
        var morph = working.FaceMorph;
        foreach (var (slider, _, _) in FaceGenMorphs.Sliders)
        {
            var property = typeof(NpcFaceMorph).GetProperty(slider)!;
            var value = (float)property.GetValue(morph)!;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - ImGui.GetFontSize() * 11);
            if (ImGui.SliderFloat(T(SliderName(slider)) + "##" + slider, ref value, -1f, 1f, "%.2f")) { property.SetValue(morph, value); Moved(); }
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) { property.SetValue(morph, 0f); Moved(); }
            Commit(target, T(SliderName(slider)));
        }

        // Tint layers.
        ImGui.SeparatorText(T("Tints"));
        foreach (var mask in head?.TintMasks ?? [])
        {
            if (mask.Index is not { } index || mask.MaskType == TintAssets.TintMaskType.SkinTone) continue;
            ImGui.PushID(index);
            var layer = working.TintLayers.FirstOrDefault(l => l.Index == index);
            var on = layer is { InterpolationValue: > 0 };
            if (ImGui.Checkbox("##on", ref on))
            {
                var flip = on;
                Change(target, T("Tint layer"), () =>
                {
                    if (flip)
                    {
                        if (working.TintLayers.FirstOrDefault(l => l.Index == index) is { } existing) existing.InterpolationValue = Math.Max(existing.InterpolationValue ?? 0, 0.5f);
                        else working.TintLayers.Add(new TintLayer { Index = index, Color = DefaultColor(lookup, mask), InterpolationValue = 0.5f, Preset = -1 });
                    }
                    else working.TintLayers.RemoveAll(l => l.Index == index);
                });
            }
            ImGui.SameLine();
            var name = $"{T(mask.MaskType?.ToString() ?? "Tint")} · {Path.GetFileNameWithoutExtension(mask.FileName?.GivenPath ?? "")}";
            if (layer is null || !on)
            {
                ImGui.TextColored(Theme.TextDim, name);
            }
            else
            {
                var color = layer.Color ?? System.Drawing.Color.White;
                var c = new Vector3(color.R, color.G, color.B) / 255f;
                if (ImGui.ColorEdit3("##color", ref c, ImGuiColorEditFlags.NoInputs))
                {
                    layer.Color = System.Drawing.Color.FromArgb(0, (int)(c.X * 255), (int)(c.Y * 255), (int)(c.Z * 255));
                    Moved();
                }
                Commit(target, T("Tint layer"));
                ImGui.SameLine();
                var amount = layer.InterpolationValue ?? 0;
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7);
                if (ImGui.SliderFloat("##value", ref amount, 0, 1, "%.2f")) { layer.InterpolationValue = amount; Moved(); }
                Commit(target, T("Tint layer"));
                ImGui.SameLine();
                ImGui.Text(name);
            }
            ImGui.PopID();
        }

        ImGui.EndDisabled();
        ImGui.Spacing();
        if (ImGui.Button(T("Export FaceGen") + "  (Ctrl+F4)")) ObjectWindow.ExportFaceGen(app, [_key]);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Writes the head mesh and tint texture for the game next to the active file; the preview here is not saved anywhere"));
    }

    private static void Preset(string label, uint value, int count, Action<uint> set, EditTarget? target)
    {
        var v = (int)value;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - ImGui.GetFontSize() * 11);
        if (ImGui.SliderInt(label, ref v, 0, Math.Max(count, 1))) { set((uint)v); Moved(); }
        Commit(target, label.Split("##")[0]);
    }

    private static void Moved()
    {
        _pending = true;
        _version++;
    }

    /// <summary>A control let go after moving: the working copy's appearance into the record, one undo step.</summary>
    private static void Commit(EditTarget? target, string name)
    {
        if (!ImGui.IsItemDeactivatedAfterEdit()) return;
        Apply(target, name);
    }

    private static void Change(EditTarget? target, string name, Action change)
    {
        change();
        _version++;
        Apply(target, name);
    }

    private static void Apply(EditTarget? target, string name)
    {
        _pending = false;
        if (target is null || _working is not { } working) return;
        target.Apply(TF($"Face: {name}"), record =>
        {
            if (record is not Npc npc) return;
            npc.HeadParts.Clear();
            npc.HeadParts.AddRange(working.HeadParts.Select(h => new FormLink<IHeadPartGetter>(h.FormKey)));
            npc.HairColor.SetTo(working.HairColor.FormKey);
            npc.TextureLighting = working.TextureLighting;
            npc.Weight = working.Weight;
            npc.FaceMorph = working.FaceMorph?.DeepCopy();
            npc.FaceParts = working.FaceParts?.DeepCopy();
            npc.TintLayers.Clear();
            npc.TintLayers.AddRange(working.TintLayers.Select(l => l.DeepCopy()));
        });
    }

    // ------------------------------------------------------------------ preview

    private static void UpdatePreview(EditorApp app, RecordLookup lookup)
    {
        var session = app.Session!;
        if (_build is { IsCompleted: true } done)
        {
            _build = null;
            if (done.IsCompletedSuccessfully)
            {
                var (nif, tint, version, tintPath, notes) = done.Result;
                var nifPath = $@"meshes\stbeditor\facepreview\{_key.ID:X8}_{version}.nif";
                session.Assets.AddMemory(nifPath, nif);
                session.Assets.AddMemory(tintPath, tint);
                if (_nifPath is not null) { session.Assets.RemoveMemory(_nifPath); Forget(app, _nifPath); }
                if (_tintPath is not null) { session.Assets.RemoveMemory(_tintPath); Forget(app, _tintPath); }
                _nifPath = nifPath;
                _tintPath = tintPath;
                _notes = notes;
                _scene = new Scene { Title = "face", IsInterior = true, Lighting = ObjectPreview.Studio };
                _scene.Objects.Add(new SceneObject
                {
                    Reference = FormKey.Null, Base = _key, BaseSignature = "NPC_", Kind = SceneObjectKind.Mesh, ModelPath = nifPath,
                    Position = Vector3.Zero, Rotation = Vector3.Zero, Scale = 1f, Transform = SceneBuilder.ReferenceTransform(Vector3.Zero, Vector3.Zero, 1f), World = Matrix4x4.Identity,
                });
            }
            else
            {
                _notes = [done.Exception?.GetBaseException().Message ?? "failed"];
            }
        }

        if (_build is null && _version != _builtVersion && _working is not null)
        {
            var snapshot = (Npc)_working.DeepCopy();
            var version = _version;
            _builtVersion = version;
            var tintPath = $@"textures\stbeditor\facepreview\{_key.ID:X8}_{version}.dds";
            var read = session.Assets.ReadAllBytes;
            _build = Task.Run(() =>
            {
                var files = FaceGenExport.Build(snapshot, lookup, read, tintPath);
                return (files.Nif, files.Tint, version, tintPath, files.Notes);
            });
        }
    }

    private static void Forget(EditorApp app, string path)
    {
        app.Preview?.Renderer.Meshes.Forget(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        app.Preview?.Renderer.Textures.Forget(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    private static void DrawPreview(EditorApp app, float width)
    {
        var size = new Vector2(width - 8, Math.Min(width * 1.25f, ImGui.GetContentRegionAvail().Y > 100 ? ImGui.GetContentRegionAvail().Y : width * 1.25f));
        if (app.Preview is not { } preview || _scene is null)
        {
            ImGui.TextColored(Theme.TextDim, _build is not null ? T("building the head…") : T("no preview"));
            return;
        }
        preview.IsReady(_scene);
        _target ??= new ViewportTarget(app.Graphics.Device);
        _target.EnsureSize((int)size.X, (int)size.Y);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##facepreview", size, ImGuiButtonFlags.MouseButtonRight);
        var io = ImGui.GetIO();
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Right, 0))
        {
            _yaw += io.MouseDelta.X * 0.01f;
            _pitch = Math.Clamp(_pitch - io.MouseDelta.Y * 0.01f, -1.2f, 1.2f);
        }
        if (ImGui.IsItemHovered() && io.MouseWheel != 0) _zoom = Math.Clamp(_zoom * (io.MouseWheel > 0 ? 0.88f : 1.14f), 0.2f, 3f);
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) { _yaw = MathF.PI; _pitch = -0.05f; _zoom = 0.62f; }
        ObjectPreview.Frame(Camera, _scene, _yaw, _pitch, _zoom);
        preview.Render(_scene, Camera, _target.ColorView!, _target.DepthView!, _target.Width, _target.Height, new Vector4(0.07f, 0.07f, 0.08f, 1));
        unsafe
        {
            ImGui.GetWindowDrawList().AddImage(new ImTextureRef(null, new ImTextureID((ulong)_target.ShaderView!.NativePointer)), origin, origin + size);
        }
        ImGui.TextColored(Theme.TextDim, _build is not null ? T("updating…") : T("RMB drag to turn, wheel to zoom"));
        foreach (var note in _notes.Take(3)) ImGui.TextColored(Theme.Warning, note);
    }

    // ------------------------------------------------------------------ data

    private static List<IHeadPartGetter> CandidatesFor(EditorApp app, RecordLookup lookup, IRaceGetter race, bool female, HeadPart.TypeEnum type)
    {
        var key = (race.FormKey, female, type);
        if (Candidates.TryGetValue(key, out var list)) return list;
        list = [];
        foreach (var record in app.Session!.Catalog.BySignature.GetValueOrDefault("HDPT") ?? [])
        {
            if (lookup.Resolve<IHeadPartGetter>(record.FormKey) is not { } part || part.Type != type || part.Flags.HasFlag(HeadPart.Flag.IsExtraPart)) continue;
            if (!part.Flags.HasFlag(female ? HeadPart.Flag.Female : HeadPart.Flag.Male)) continue;
            if (!part.ValidRaces.IsNull && lookup.Resolve<IFormListGetter>(part.ValidRaces.FormKey) is { } races
                && !races.Items.Any(r => r.FormKey == race.FormKey || (!race.MorphRace.IsNull && r.FormKey == race.MorphRace.FormKey))) continue;
            list.Add(part);
        }
        list.Sort((a, b) => string.Compare(a.EditorID, b.EditorID, StringComparison.OrdinalIgnoreCase));
        Candidates[key] = list;
        return list;
    }

    /// <summary>How many nose / eye / mouth presets the face's chargen morphs have.</summary>
    private static (int Nose, int Eyes, int Lips) Presets(EditorApp app, IHeadPartGetter? face)
    {
        var tri = face?.Parts.FirstOrDefault(p => p.PartType == Part.PartTypeEnum.ChargenMorph)?.FileName?.GivenPath;
        if (tri is null) return (40, 40, 40);
        if (PresetCounts.TryGetValue(tri, out var counts)) return counts;
        try
        {
            var file = app.Session!.Assets.ReadAllBytes(StbEditor.Assets.AssetFileSystem.MeshPath(tri)) is { } bytes ? TriFile.Load(bytes) : null;
            int Max(string prefix) => file?.Morphs.Select(m => m.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(m.Name[prefix.Length..], out var n) ? n : 0).DefaultIfEmpty(0).Max() ?? 0;
            counts = (Max("NoseType"), Max("EyesType"), Max("LipType"));
        }
        catch { counts = (40, 40, 40); }
        PresetCounts[tri] = counts;
        return counts;
    }

    private static System.Drawing.Color DefaultColor(RecordLookup lookup, ITintAssetsGetter mask) =>
        lookup.Resolve<IColorRecordGetter>(mask.PresetDefault.FormKey)?.Color
        ?? (mask.Presets.FirstOrDefault() is { } first ? lookup.Resolve<IColorRecordGetter>(first.Color.FormKey)?.Color : null)
        ?? System.Drawing.Color.FromArgb(0, 128, 64, 64);

    private static string TypeName(HeadPart.TypeEnum type) => type switch
    {
        HeadPart.TypeEnum.FacialHair => "Facial hair",
        HeadPart.TypeEnum.Eyebrows => "Eyebrows",
        HeadPart.TypeEnum.Eyes => "Eyes",
        HeadPart.TypeEnum.Scars => "Scars",
        HeadPart.TypeEnum.Face => "Head",
        _ => "Hair",
    };

    /// <summary>"NoseLongVsShort" → "Nose: long / short".</summary>
    public static string SliderName(string slider)
    {
        var at = slider.IndexOf("Vs", StringComparison.Ordinal);
        var first = slider[..at];
        var second = slider[(at + 2)..];
        var split = 1;
        while (split < first.Length && !char.IsUpper(first[split])) split++;
        var feature = first[..split];
        var a = first[split..];
        return $"{feature}: {a.ToLowerInvariant()} / {second.ToLowerInvariant()}";
    }
}
