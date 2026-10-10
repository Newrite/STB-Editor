using System.Numerics;
using System.Reflection;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Assets;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// Scripts tab of the Creation Kit, for any record with a plain VMAD (references and base objects):
/// attach / remove scripts, fill properties with the types the compiled .pex declares, auto-fill
/// object properties from EditorIDs. No compiling — scripts must exist as .pex.
/// </summary>
internal static class ScriptsEditor
{
    private static string _addFilter = string.Empty;
    private static ScriptLibrary? _library;
    private static object? _librarySession;

    /// <summary>Papyrus native types → record signatures they accept ("REF" = placed references).</summary>
    private static readonly Dictionary<string, string[]> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ObjectReference"] = ["REF"], ["Actor"] = ["REF"],
        ["Quest"] = ["QUST"], ["Keyword"] = ["KYWD"], ["GlobalVariable"] = ["GLOB"], ["MiscObject"] = ["MISC"],
        ["Weapon"] = ["WEAP"], ["Armor"] = ["ARMO"], ["Spell"] = ["SPEL"], ["Message"] = ["MESG"], ["Sound"] = ["SNDR", "SOUN"],
        ["Location"] = ["LCTN"], ["FormList"] = ["FLST"], ["Faction"] = ["FACT"], ["Static"] = ["STAT"], ["Activator"] = ["ACTI"],
        ["Book"] = ["BOOK"], ["Potion"] = ["ALCH"], ["Key"] = ["KEYM"], ["Container"] = ["CONT"], ["Door"] = ["DOOR"],
        ["Furniture"] = ["FURN"], ["Light"] = ["LIGH"], ["LeveledItem"] = ["LVLI"], ["LeveledActor"] = ["LVLN"], ["ActorBase"] = ["NPC_"],
        ["Race"] = ["RACE"], ["Perk"] = ["PERK"], ["Ingredient"] = ["INGR"], ["Scroll"] = ["SCRL"], ["MagicEffect"] = ["MGEF"],
        ["Enchantment"] = ["ENCH"], ["Package"] = ["PACK"], ["Idle"] = ["IDLE"], ["ImageSpaceModifier"] = ["IMAD"], ["VisualEffect"] = ["RFCT"],
        ["EffectShader"] = ["EFSH"], ["Explosion"] = ["EXPL"], ["Projectile"] = ["PROJ"], ["Hazard"] = ["HAZD"], ["Topic"] = ["DIAL"],
        ["TopicInfo"] = ["INFO"], ["Scene"] = ["SCEN"], ["Outfit"] = ["OTFT"], ["SoundCategory"] = ["SNCT"], ["Shout"] = ["SHOU"],
        ["WordOfPower"] = ["WOOP"], ["Art"] = ["ARTO"], ["EncounterZone"] = ["ECZN"], ["LocationRefType"] = ["LCRT"], ["Class"] = ["CLAS"],
        ["CombatStyle"] = ["CSTY"], ["WorldSpace"] = ["WRLD"], ["Cell"] = ["CELL"], ["VoiceType"] = ["VTYP"], ["Ammo"] = ["AMMO"],
        ["SoulGem"] = ["SLGM"], ["TalkingActivator"] = ["TACT"], ["Flora"] = ["FLOR"], ["Tree"] = ["TREE"], ["Weather"] = ["WTHR"],
        ["ImpactDataSet"] = ["IPDS"], ["Static"] = ["STAT"], ["MusicType"] = ["MUSC"], ["Outfit"] = ["OTFT"], ["AssociationType"] = ["ASTP"],
    };

    /// <summary>Whether a record type carries a plain VMAD (quests/dialogue have fragment variants, not handled here).</summary>
    public static bool Supports(IMajorRecordGetter record) =>
        VmadProperty(record.GetType()) is not null;

    private static PropertyInfo? VmadProperty(Type type) =>
        type.GetProperties().FirstOrDefault(p => p.Name == "VirtualMachineAdapter" && typeof(IVirtualMachineAdapterGetter).IsAssignableFrom(p.PropertyType));

    private static IVirtualMachineAdapterGetter? VmadOf(IMajorRecordGetter record) =>
        VmadProperty(record.GetType())?.GetValue(record) as IVirtualMachineAdapterGetter;

    /// <summary>The mutable VMAD of an edited copy, created when missing.</summary>
    private static VirtualMachineAdapter VmadOf(IMajorRecord copy, bool create)
    {
        var property = VmadProperty(copy.GetType())!;
        if (property.GetValue(copy) is VirtualMachineAdapter existing) return existing;
        if (!create) throw new InvalidOperationException("no scripts");
        var vmad = new VirtualMachineAdapter { Version = 5, ObjectFormat = 2 };
        property.SetValue(copy, vmad);
        return vmad;
    }

    public static void Draw(EditorApp app, IMajorRecordGetter current, Action<string, Action<IMajorRecord>> edit)
    {
        if (app.Session is not { } session) return;
        if (!ReferenceEquals(_librarySession, session))
        {
            _library = new ScriptLibrary(session.Assets);
            _librarySession = session;
        }
        var library = _library!;
        var vmad = VmadOf(current);
        var scripts = vmad?.Scripts ?? [];

        if (!ImGui.TreeNodeEx(TF($"Scripts ({scripts.Count})###scripts"), scripts.Count > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None)) return;

        for (var s = 0; s < scripts.Count; s++)
        {
            var entry = scripts[s];
            var scriptIndex = s;
            ImGui.PushID(s);
            var pex = library.Get(entry.Name);
            ImGui.SeparatorText(entry.Name + (pex is null ? T("  (no .pex found)") : ""));
            if (pex?.Documentation is { Length: > 0 } doc) ImGui.TextColored(Theme.TextDim, doc);

            var declared = pex is null ? [] : library.AllProperties(entry.Name);
            DrawProperties(app, entry, declared, scriptIndex, edit);

            if (ImGui.SmallButton(T("Auto-fill"))) AutoFill(app, entry, declared, scriptIndex, edit);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Fill empty object properties whose name matches an EditorID (as the CK does)"));
            ImGui.SameLine();
            if (ImGui.SmallButton(T("Remove script")))
                edit(TF($"Remove script {entry.Name}"), copy => VmadOf(copy, false).Scripts.RemoveAt(scriptIndex));
            ImGui.PopID();
        }

        ImGui.Separator();
        ImGui.SetNextItemWidth(-ImGui.GetFontSize() * 4);
        ImGui.InputTextWithHint("##addscript", T("Script name to attach"), ref _addFilter, 128);
        ImGui.SameLine();
        var exact = library.Names.FirstOrDefault(n => string.Equals(n, _addFilter.Trim(), StringComparison.OrdinalIgnoreCase));
        ImGui.BeginDisabled(exact is null || scripts.Any(x => string.Equals(x.Name, exact, StringComparison.OrdinalIgnoreCase)));
        if (ImGui.SmallButton(T("Add")) && exact is not null)
        {
            var name = exact;
            edit(TF($"Add script {name}"), copy => VmadOf(copy, true).Scripts.Add(new ScriptEntry { Name = name }));
            _addFilter = string.Empty;
        }
        ImGui.EndDisabled();

        if (_addFilter.Trim().Length >= 2 && exact is null)
        {
            var matches = library.Names.Where(n => n.Contains(_addFilter.Trim(), StringComparison.OrdinalIgnoreCase)).Take(12).ToList();
            foreach (var match in matches)
                if (ImGui.Selectable(match)) _addFilter = match;
            if (matches.Count == 0) ImGui.TextColored(Theme.TextDim, "no script with that name among the .pex files");
        }
        ImGui.TreePop();
    }

    private static void DrawProperties(EditorApp app, IScriptEntryGetter entry, List<(PexProperty Property, string Owner)> declared, int scriptIndex,
        Action<string, Action<IMajorRecord>> edit)
    {
        if (!ImGui.BeginTable("##props", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn(T("Property"), ImGuiTableColumnFlags.WidthStretch, 0.38f);
        ImGui.TableSetupColumn(T("Value"), ImGuiTableColumnFlags.WidthStretch, 0.62f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());

        var names = declared.Select(d => d.Property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (property, owner) in declared)
        {
            var value = entry.Properties.FirstOrDefault(p => string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase));
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.PushID(property.Name);
            ImGui.TextColored(value is null ? Theme.TextDim : Theme.Accent, property.Name);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{property.Type}{(owner != entry.Name ? $" (from {owner})" : "")}{(property.Documentation.Length > 0 ? "\n" + property.Documentation : "")}");
            ImGui.TableNextColumn();
            DrawValue(app, property, value, scriptIndex, edit);
            ImGui.TableNextColumn();
            if (value is not null && ImGui.SmallButton("x"))
                edit($"Clear {property.Name}", copy => Entry(copy, scriptIndex).Properties.RemoveAll(p => string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase)));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Back to the script's default"));
            ImGui.PopID();
        }

        // Values for properties the script no longer declares.
        foreach (var stale in entry.Properties.Where(p => !names.Contains(p.Name)))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.PushID("stale" + stale.Name);
            ImGui.TextColored(Theme.Error, stale.Name);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Set in the plugin but not declared by the script (renamed or removed property)"));
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.TextDim, Describe(app, stale));
            ImGui.TableNextColumn();
            var staleName = stale.Name;
            if (ImGui.SmallButton("x")) edit($"Remove {staleName}", copy => Entry(copy, scriptIndex).Properties.RemoveAll(p => p.Name == staleName));
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private static ScriptEntry Entry(IMajorRecord copy, int index) => VmadOf(copy, false).Scripts[index];

    /// <summary>The property in the copy, made with the right Mutagen type when missing.</summary>
    private static T Property<T>(IMajorRecord copy, int scriptIndex, string name) where T : ScriptProperty, new()
    {
        var entry = Entry(copy, scriptIndex);
        var existing = entry.Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is T typed) return typed;
        if (existing is not null) entry.Properties.Remove(existing);
        var created = new T { Name = name, Flags = ScriptProperty.Flag.Edited };
        entry.Properties.Add(created);
        return created;
    }

    private static void DrawValue(EditorApp app, PexProperty property, IScriptPropertyGetter? value, int scriptIndex, Action<string, Action<IMajorRecord>> edit)
    {
        var name = property.Name;
        ImGui.SetNextItemWidth(-1);
        if (property.IsArray)
        {
            DrawArray(app, property, value, scriptIndex, edit);
            return;
        }

        switch (property.ElementType.ToLowerInvariant())
        {
            case "int":
            {
                if (Fields.Int("##v", (value as IScriptIntPropertyGetter)?.Data ?? 0, out var v))
                    edit(TF($"Set {name}"), copy => Property<ScriptIntProperty>(copy, scriptIndex, name).Data = v);
                break;
            }
            case "float":
            {
                if (Fields.Float("##v", (value as IScriptFloatPropertyGetter)?.Data ?? 0f, out var v))
                    edit(TF($"Set {name}"), copy => Property<ScriptFloatProperty>(copy, scriptIndex, name).Data = v);
                break;
            }
            case "bool":
            {
                var v = (value as IScriptBoolPropertyGetter)?.Data ?? false;
                if (ImGui.Checkbox("##v", ref v))
                    edit(TF($"Set {name}"), copy => Property<ScriptBoolProperty>(copy, scriptIndex, name).Data = v);
                if (value is null) { ImGui.SameLine(); ImGui.TextColored(Theme.TextDim, T("(default)")); }
                break;
            }
            case "string":
            {
                if (Fields.Text("##v", (value as IScriptStringPropertyGetter)?.Data ?? string.Empty, out var v))
                    edit(TF($"Set {name}"), copy => Property<ScriptStringProperty>(copy, scriptIndex, name).Data = v);
                break;
            }
            default:
            {
                FormKey? v = (value as IScriptObjectPropertyGetter)?.Object.FormKeyNullable;
                var accepts = Accepts(property.ElementType);
                if (FormField.Draw(app, $"sp-{scriptIndex}-{name}", ref v, accepts is ["REF"] ? null : accepts, references: accepts is ["REF"]))
                {
                    if (v is null)
                        edit($"Clear {name}", copy => Entry(copy, scriptIndex).Properties.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)));
                    else
                        edit(TF($"Set {name}"), copy =>
                        {
                            var p = Property<ScriptObjectProperty>(copy, scriptIndex, name);
                            p.Object.SetTo(v.Value);
                            p.Alias = -1;
                        });
                }
                break;
            }
        }
    }

    private static void DrawArray(EditorApp app, PexProperty property, IScriptPropertyGetter? value, int scriptIndex, Action<string, Action<IMajorRecord>> edit)
    {
        var name = property.Name;
        var element = property.ElementType.ToLowerInvariant();
        var count = value switch
        {
            IScriptObjectListPropertyGetter o => o.Objects.Count,
            IScriptIntListPropertyGetter i => i.Data.Count,
            IScriptFloatListPropertyGetter f => f.Data.Count,
            IScriptBoolListPropertyGetter b => b.Data.Count,
            IScriptStringListPropertyGetter s => s.Data.Count,
            _ => 0,
        };

        if (!ImGui.TreeNodeEx($"[{count}]###arr", ImGuiTreeNodeFlags.SpanAvailWidth)) return;
        for (var i = 0; i < count; i++)
        {
            var index = i;
            ImGui.PushID(i);
            ImGui.SetNextItemWidth(-ImGui.GetFrameHeight() - 4);
            switch (value)
            {
                case IScriptObjectListPropertyGetter objects:
                {
                    FormKey? v = objects.Objects[i].Object.FormKeyNullable;
                    var accepts = Accepts(property.ElementType);
                    if (FormField.Draw(app, $"sa-{scriptIndex}-{name}-{i}", ref v, accepts is ["REF"] ? null : accepts, references: accepts is ["REF"]) && v is { } set)
                        edit($"Set {name}[{index}]", copy => ((ScriptObjectListProperty)Entry(copy, scriptIndex).Properties.First(p => p.Name == name)).Objects[index].Object.SetTo(set));
                    break;
                }
                case IScriptIntListPropertyGetter ints:
                {
                    if (Fields.Int("##v", ints.Data[i], out var v, 0))
                        edit($"Set {name}[{index}]", copy => ((ScriptIntListProperty)Entry(copy, scriptIndex).Properties.First(p => p.Name == name)).Data[index] = v);
                    break;
                }
                case IScriptFloatListPropertyGetter floats:
                {
                    if (Fields.Float("##v", floats.Data[i], out var v))
                        edit($"Set {name}[{index}]", copy => ((ScriptFloatListProperty)Entry(copy, scriptIndex).Properties.First(p => p.Name == name)).Data[index] = v);
                    break;
                }
                case IScriptBoolListPropertyGetter bools:
                {
                    var v = bools.Data[i];
                    if (ImGui.Checkbox("##v", ref v))
                        edit($"Set {name}[{index}]", copy => ((ScriptBoolListProperty)Entry(copy, scriptIndex).Properties.First(p => p.Name == name)).Data[index] = v);
                    break;
                }
                case IScriptStringListPropertyGetter strings:
                {
                    if (Fields.Text("##v", strings.Data[i], out var v))
                        edit($"Set {name}[{index}]", copy => ((ScriptStringListProperty)Entry(copy, scriptIndex).Properties.First(p => p.Name == name)).Data[index] = v);
                    break;
                }
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("−"))
                edit(TF($"Remove {name}[{index}]"), copy =>
                {
                    switch (Entry(copy, scriptIndex).Properties.First(p => p.Name == name))
                    {
                        case ScriptObjectListProperty o: o.Objects.RemoveAt(index); break;
                        case ScriptIntListProperty l: l.Data.RemoveAt(index); break;
                        case ScriptFloatListProperty l: l.Data.RemoveAt(index); break;
                        case ScriptBoolListProperty l: l.Data.RemoveAt(index); break;
                        case ScriptStringListProperty l: l.Data.RemoveAt(index); break;
                    }
                });
            ImGui.PopID();
        }

        if (ImGui.SmallButton(T("+ item")))
        {
            edit(TF($"Add {name}[]"), copy =>
            {
                switch (element)
                {
                    case "int": Property<ScriptIntListProperty>(copy, scriptIndex, name).Data.Add(0); break;
                    case "float": Property<ScriptFloatListProperty>(copy, scriptIndex, name).Data.Add(0f); break;
                    case "bool": Property<ScriptBoolListProperty>(copy, scriptIndex, name).Data.Add(false); break;
                    case "string": Property<ScriptStringListProperty>(copy, scriptIndex, name).Data.Add(string.Empty); break;
                    default: Property<ScriptObjectListProperty>(copy, scriptIndex, name).Objects.Add(new ScriptObjectProperty { Name = name, Alias = -1 }); break;
                }
            });
        }
        ImGui.TreePop();
    }

    /// <summary>Signatures a Papyrus type takes; custom scripts resolve through their parents.</summary>
    private static string[]? Accepts(string type)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var current = type; current is not null && seen.Add(current); current = _library?.Get(current)?.Parent)
        {
            if (string.Equals(current, "Form", StringComparison.OrdinalIgnoreCase)) return null;
            if (Types.TryGetValue(current, out var signatures)) return signatures;
        }
        return null;
    }

    private static void AutoFill(EditorApp app, IScriptEntryGetter entry, List<(PexProperty Property, string Owner)> declared, int scriptIndex,
        Action<string, Action<IMajorRecord>> edit)
    {
        var session = app.Session!;
        var fills = new List<(string Name, FormKey Key)>();
        foreach (var (property, _) in declared)
        {
            if (!property.IsObject || property.IsArray) continue;
            if (entry.Properties.Any(p => string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase))) continue;

            var accepts = Accepts(property.ElementType);
            FormKey? found;
            if (accepts is ["REF"])
            {
                found = app.RenderWindow.Scene?.Objects.FirstOrDefault(o => string.Equals(o.Record?.EditorID, property.Name, StringComparison.OrdinalIgnoreCase))?.Reference;
            }
            else
            {
                var pool = accepts is null ? session.Catalog.ByKey.Values : accepts.SelectMany(sig => session.Catalog.BySignature.GetValueOrDefault(sig) ?? []);
                found = pool.FirstOrDefault(r => string.Equals(r.EditorId, property.Name, StringComparison.OrdinalIgnoreCase))?.FormKey;
            }
            if (found is { } key) fills.Add((property.Name, key));
        }

        if (fills.Count == 0)
        {
            Log.Info($"auto-fill {entry.Name}: nothing matches by EditorID");
            return;
        }

        edit(TF($"Auto-fill {entry.Name} ({fills.Count})"), copy =>
        {
            foreach (var (name, key) in fills)
            {
                var p = Property<ScriptObjectProperty>(copy, scriptIndex, name);
                p.Object.SetTo(key);
                p.Alias = -1;
            }
        });
        Log.Info($"auto-fill {entry.Name}: {string.Join(", ", fills.Select(f => f.Name))}");
    }

    private static string Describe(EditorApp app, IScriptPropertyGetter property) => property switch
    {
        IScriptObjectPropertyGetter o => FormField.Describe(app, o.Object.FormKey),
        IScriptIntPropertyGetter i => i.Data.ToString(),
        IScriptFloatPropertyGetter f => f.Data.ToString("0.####"),
        IScriptBoolPropertyGetter b => b.Data ? "true" : "false",
        IScriptStringPropertyGetter s => s.Data,
        _ => property.GetType().Name,
    };
}
