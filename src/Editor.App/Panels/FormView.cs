using System.Collections;
using System.Reflection;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using StbEditor.App.Ui;
using StbEditor.Core;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>A tab of a record form: its title and the record members it shows, in order.</summary>
internal sealed record FormTab(string Title, IReadOnlyList<PropertyInfo> Members);

/// <summary>
/// Record forms laid out like the Creation Kit's dialogs instead of one long field list: plain
/// fields as a two-column grid, sub-structures as sections, lists (inventory, factions, perks,
/// effects, conditions…) as tables with add / remove. The main record types have tabs named as in
/// the CK; any other type gets "Main", one tab per list of sub-records and per big structure.
/// </summary>
internal static class FormView
{
    /// <summary>Tabs of the main record types (member names as Mutagen has them); the rest go to «Other».</summary>
    private static readonly Dictionary<string, (string Tab, string[] Members)[]> Layouts = new()
    {
        ["NPC_"] =
        [
            ("Traits", ["EditorID", "Name", "ShortName", "Race", "Class", "Voice", "Template", "Height", "Weight", "Configuration",
                "CombatStyle", "GiftFilter", "SoundLevel", "Keywords", "ObjectBounds"]),
            ("Stats", ["PlayerSkills"]),
            ("Factions", ["Factions", "CrimeFaction"]),
            ("Inventory", ["Items", "DefaultOutfit", "SleepingOutfit", "DeathItem"]),
            ("Spells and perks", ["ActorEffect", "Perks"]),
            ("AI", ["AIData", "Packages", "DefaultPackageList", "SpectatorOverridePackageList", "ObserveDeadBodyOverridePackageList",
                "GuardWarnOverridePackageList", "CombatOverridePackageList"]),
            ("Appearance", ["HeadParts", "HairColor", "HeadTexture", "TextureLighting", "FaceMorph", "FaceParts", "TintLayers", "WornArmor", "FarAwayModel"]),
            ("Attacks", ["Attacks", "AttackRace"]),
        ],
        ["ARMO"] =
        [
            ("Main", ["EditorID", "Name", "ArmorRating", "Value", "Weight", "BodyTemplate", "Keywords", "Race", "EquipmentType",
                "ObjectEffect", "EnchantmentAmount", "TemplateArmor", "Description"]),
            ("Models", ["Armature", "WorldModel", "ObjectBounds", "RagdollConstraintTemplate"]),
            ("Sounds", ["PickUpSound", "PutDownSound", "BashImpactDataSet", "AlternateBlockMaterial"]),
        ],
        ["WEAP"] =
        [
            ("Main", ["EditorID", "Name", "BasicStats", "Keywords", "EquipmentType", "ObjectEffect", "EnchantmentAmount", "Template", "Description"]),
            ("Data", ["Data"]),
            ("Critical", ["Critical"]),
            ("Models", ["Model", "FirstPersonModel", "ScopeModel", "Icons", "ObjectBounds"]),
            ("Sounds", ["AttackSound", "AttackSound2D", "AttackLoopSound", "AttackFailSound", "IdleSound", "EquipSound", "UnequipSound",
                "PickUpSound", "PutDownSound", "DetectionSoundLevel", "ImpactDataSet", "BlockBashImpact", "AlternateBlockMaterial"]),
        ],
        ["PERK"] =
        [
            ("Main", ["EditorID", "Name", "Description", "Trait", "Level", "NumRanks", "Playable", "Hidden", "NextPerk", "Icons"]),
            ("Conditions", ["Conditions"]),
            ("Perk entries", ["Effects"]),
        ],
        ["SPEL"] =
        [
            ("Main", ["EditorID", "Name", "Type", "CastType", "TargetType", "BaseCost", "Flags", "ChargeTime", "CastDuration", "Range",
                "HalfCostPerk", "EquipmentType", "MenuDisplayObject", "Keywords", "Description", "ObjectBounds"]),
            ("Effects", ["Effects"]),
        ],
        ["MGEF"] =
        [
            ("Main", ["EditorID", "Name", "Archetype", "CastType", "TargetType", "MagicSkill", "MinimumSkillLevel", "BaseCost", "Flags",
                "ResistValue", "SecondActorValue", "SecondActorValueWeight", "Keywords", "Description", "PerkToApply", "EquipAbility"]),
            ("Visuals", ["CastingLight", "HitShader", "EnchantShader", "Projectile", "Explosion", "CastingArt", "HitEffectArt", "ImpactData",
                "DualCastArt", "DualCastScale", "EnchantArt", "HitVisuals", "EnchantVisuals", "ImageSpaceModifier", "MenuDisplayObject"]),
            ("Sounds", ["Sounds", "CastingSoundLevel"]),
            ("Conditions", ["Conditions"]),
        ],
        ["CONT"] =
        [
            ("Main", ["EditorID", "Name", "Flags", "Weight", "OpenSound", "CloseSound", "Model", "ObjectBounds"]),
            ("Inventory", ["Items"]),
        ],
        ["ALCH"] =
        [
            ("Main", ["EditorID", "Name", "Value", "Weight", "Flags", "Addiction", "AddictionChance", "Keywords", "EquipmentType", "Description"]),
            ("Effects", ["Effects"]),
        ],
        ["INGR"] =
        [
            ("Main", ["EditorID", "Name", "Value", "Weight", "Flags", "IngredientValue", "Keywords", "EquipmentType"]),
            ("Effects", ["Effects"]),
        ],
        ["ENCH"] =
        [
            ("Main", ["EditorID", "Name", "EnchantmentCost", "Flags", "CastType", "EnchantmentAmount", "TargetType", "EnchantType", "ChargeTime",
                "BaseEnchantment", "WornRestrictions"]),
            ("Effects", ["Effects"]),
        ],
        ["BOOK"] =
        [
            ("Main", ["EditorID", "Name", "Value", "Weight", "Flags", "Type", "Teaches", "Keywords", "InventoryArt", "Description"]),
            ("Text", ["BookText"]),
        ],
        ["FACT"] =
        [
            ("Main", ["EditorID", "Name", "Flags", "Ranks"]),
            ("Relations", ["Relations"]),
            ("Crime", ["CrimeValues", "ExteriorJailMarker", "FollowerWaitMarker", "StolenGoodsContainer", "PlayerInventoryContainer",
                "SharedCrimeFactionList", "JailOutfit"]),
            ("Vendor", ["VendorBuySellList", "MerchantContainer", "VendorValues", "VendorLocation", "Conditions"]),
        ],
        ["QUST"] =
        [
            ("Quest Data", ["EditorID", "Name", "Type", "Priority", "Flags", "Event", "Filter", "Description", "TextDisplayGlobals", "NextAliasID"]),
            ("Dialogue Conditions", ["DialogConditions"]),
            ("Event Conditions", ["EventConditions"]),
            ("Quest Stages", ["Stages"]),
            ("Quest Objectives", ["Objectives"]),
            ("Quest Aliases", ["Aliases"]),
        ],
        ["LIGH"] =
        [
            ("Main", ["EditorID", "Name", "Color", "Radius", "FadeValue", "Flags", "FalloffExponent", "FOV", "NearClip", "Time",
                "FlickerPeriod", "FlickerIntensityAmplitude", "FlickerMovementAmplitude", "Value", "Weight", "Sound", "Lens"]),
        ],
    };

    /// <summary>--expand-forms: list rows start open (screenshots).</summary>
    private static readonly bool ExpandAll = Environment.GetCommandLineArgs().Contains("--expand-forms");

    /// <summary>Header members: identity and bookkeeping, shown last.</summary>
    private static readonly HashSet<string> Header = ["FormKey", "FormVersion", "VersionControl", "Version2", "MajorRecordFlagsRaw",
        "IsCompressed", "IsDeleted", "SkyrimMajorRecordFlags", "MajorFlags"];

    public static List<FormTab> Tabs(IMajorRecordGetter record, string signature, bool showHeader)
    {
        var members = RecordFields.PropertiesOf(record)
            .Where(p => p.Name != "VirtualMachineAdapter" && (showHeader || !RecordFields.IsRecordHeader(p.Name))).ToList();
        var byName = members.ToDictionary(p => p.Name);
        var tabs = new List<FormTab>();
        var used = new HashSet<string>();

        if (Layouts.TryGetValue(signature, out var layout))
        {
            foreach (var (title, names) in layout)
            {
                var list = names.Where(byName.ContainsKey).Select(n => byName[n]).ToList();
                if (list.Count == 0) continue;
                tabs.Add(new FormTab(title, list));
                used.UnionWith(list.Select(p => p.Name));
            }
        }

        // Everything else: plain fields and small structures on «Main» (or «Other» after a layout),
        // each list of sub-records and each big structure on a tab of its own.
        var main = new List<PropertyInfo>();
        var header = new List<PropertyInfo>();
        foreach (var member in members.Where(p => !used.Contains(p.Name)))
        {
            if (Header.Contains(member.Name)) { header.Add(member); continue; }
            object? value;
            try { value = member.GetValue(record); } catch { value = null; }
            if (OwnTab(value)) tabs.Add(new FormTab(FieldEditors.Label(member.Name) + Count(value), [member]));
            else main.Add(member);
        }
        main.AddRange(header);
        if (main.Count > 0)
        {
            if (layout is null) tabs.Insert(0, new FormTab("Main", main));
            else tabs.Add(new FormTab("Other", main));
        }
        return tabs;
    }

    /// <summary>Lists of sub-records, and structures with many fields, deserve a tab.</summary>
    private static bool OwnTab(object? value)
    {
        if (value is null || !RecordFields.IsComposite(value)) return false;
        if (value is IEnumerable list && !RecordFields.IsGendered(value))
            return list.Cast<object?>().Any(RecordFields.IsComposite) || (RecordFields.ElementType(value) is { } e && RecordFields.Creatable(e) is { } c && !typeof(IFormLinkGetter).IsAssignableFrom(c));
        return RecordFields.Leaves(value, maxDepth: 4).Count() > 14;
    }

    private static string Count(object? value) =>
        value is IEnumerable list && !RecordFields.IsGendered(value) ? $" ({list.Cast<object?>().Count()})" : "";

    // ------------------------------------------------------------------ drawing

    public static void DrawTab(EditorApp app, IMajorRecordGetter record, FormTab tab, EditTarget? target)
    {
        if (record is Mutagen.Bethesda.Skyrim.IQuestGetter quest)
        {
            // Conditions of a quest name its aliases; stages, objectives and aliases have their own editors.
            ConditionsEditor.Aliases = QuestEditor.AliasNames(quest);
            try
            {
                if (QuestEditor.Draws(tab)) QuestEditor.Draw(app, quest, tab, target);
                else DrawMembers(app, record, tab.Members, [], target, 0);
            }
            finally { ConditionsEditor.Aliases = null; }
            return;
        }
        var path = new List<object>();
        DrawMembers(app, record, tab.Members, path, target, 0);
    }

    /// <summary>
    /// Fields of a part of a record (a quest stage, an alias…) at <paramref name="path"/> from the record,
    /// drawn as in a form tab: plain fields as a grid, structures and lists below.
    /// </summary>
    public static void DrawObject(EditorApp app, object owner, IEnumerable<string> members, IReadOnlyList<object> path, EditTarget? target)
    {
        var all = RecordFields.PropertiesOf(owner);
        var chosen = members.Select(n => all.FirstOrDefault(p => p.Name == n)).OfType<PropertyInfo>().ToList();
        DrawMembers(app, owner, chosen, path.ToList(), target, 1);
    }

    private static void DrawMembers(EditorApp app, object owner, IReadOnlyList<PropertyInfo> members, List<object> path, EditTarget? target, int depth)
    {
        // Plain fields first, as one grid; then the structures and lists in order.
        var leaves = new List<(PropertyInfo Member, object? Value)>();
        var composites = new List<(PropertyInfo Member, object? Value)>();
        foreach (var member in members)
        {
            // Padding the game never reads: kept in «All fields», not in forms.
            if (member.Name.StartsWith("Unused", StringComparison.Ordinal) || member.Name.StartsWith("Unknown", StringComparison.Ordinal)) continue;
            object? value;
            try { value = member.GetValue(owner); }
            catch (TargetInvocationException) { continue; }
            if (RecordFields.IsComposite(value) || (value is null && IsStructType(owner, member))) composites.Add((member, value));
            else leaves.Add((member, value));
        }

        if (leaves.Count > 0 && ImGui.BeginTable("##grid", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch, 0.36f);
            ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch, 0.64f);
            foreach (var (member, value) in leaves)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                var header = Header.Contains(member.Name);
                ImGui.TextColored(header ? Theme.TextDim : Theme.Text, FieldEditors.Label(member.Name));
                ImGui.TableNextColumn();
                path.Add(member.Name);
                var (editable, declared) = RecordFields.Editability(owner, member.Name, value);
                FieldEditors.Draw(app, target, FieldEditors.Label(member.Name), path, value, declared ?? member.PropertyType, editable);
                path.RemoveAt(path.Count - 1);
            }
            ImGui.EndTable();
        }

        foreach (var (member, value) in composites)
        {
            path.Add(member.Name);
            ImGui.PushID(member.Name);
            DrawComposite(app, owner, member, value, path, target, depth);
            ImGui.PopID();
            path.RemoveAt(path.Count - 1);
        }
    }

    private static bool IsStructType(object owner, PropertyInfo member)
    {
        var type = RecordFields.ClassProperty(owner, member.Name)?.PropertyType ?? member.PropertyType;
        return RecordFields.KindOf(null, type) == FieldKind.None && RecordFields.Creatable(type) is not null;
    }

    private static void DrawComposite(EditorApp app, object owner, PropertyInfo member, object? value, List<object> path, EditTarget? target, int depth)
    {
        var title = FieldEditors.Label(member.Name);
        if (value is null)
        {
            ImGui.SeparatorText(title);
            ImGui.TextColored(Theme.TextDim, T("(none)"));
            if (target is not null && RecordFields.ClassProperty(owner, member.Name) is { CanWrite: true })
            {
                ImGui.SameLine();
                var at = path.ToArray();
                if (ImGui.SmallButton(T("+ add")))
                {
                    var isList = typeof(IEnumerable).IsAssignableFrom(member.PropertyType) && member.PropertyType != typeof(string);
                    target.Apply(TF($"Add {title}"), copy => { if (isList) RecordFields.AddItem(copy, at); else RecordFields.Create(copy, at); });
                }
            }
            return;
        }

        var isEnumerable = value is IEnumerable && !RecordFields.IsGendered(value);
        var open = ImGui.CollapsingHeader(isEnumerable ? title + Count(value) : title, ImGuiTreeNodeFlags.DefaultOpen);
        // An optional structure can be removed again.
        if (!isEnumerable && target is not null && Nullable(owner, member))
        {
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - ImGui.GetFontSize() * 4);
            var at = path.ToArray();
            if (ImGui.SmallButton(T("remove"))) target.Apply(TF($"Remove {title}"), copy => RecordFields.Clear(copy, at));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("This part is optional: remove it from the record"));
        }
        if (!open) return;

        ImGui.Indent(ImGui.GetFontSize() * 0.4f);
        if (isEnumerable && StbEditor.Core.Conditions.IsConditionList(value)) ConditionsEditor.Draw(app, (IEnumerable)value, path, target);
        else if (isEnumerable) DrawList(app, (IEnumerable)value, path, target, depth);
        else DrawMembers(app, value, RecordFields.PropertiesOf(value), path, target, depth + 1);
        ImGui.Unindent(ImGui.GetFontSize() * 0.4f);
    }

    private static bool Nullable(object owner, PropertyInfo member) =>
        RecordFields.ClassProperty(owner, member.Name) is { CanWrite: true } p
        && new NullabilityInfoContext().Create(p).WriteState == NullabilityState.Nullable;

    /// <summary>
    /// A list: plain items (links, numbers) one per row; sub-records with a few flat fields as a grid
    /// (one column per field, as the CK's inventory and faction lists); deeper ones as expandable rows.
    /// </summary>
    private static void DrawList(EditorApp app, IEnumerable list, List<object> path, EditTarget? target, int depth)
    {
        if (RecordFields.IsDictionary(list))
        {
            DrawMap(app, list, path, target);
            return;
        }
        var items = list.Cast<object?>().ToList();
        var element = RecordFields.ElementType(list);
        var at = path.ToArray();
        var columns = GridColumns(items, element);

        if (items.Count > 0)
        {
            if (columns is not null)
            {
                var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable;
                if (ImGui.BeginTable("##list", columns.Count + 1 + (target is null ? 0 : 1), flags))
                {
                    ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 1.6f);
                    foreach (var column in columns) ImGui.TableSetupColumn(column.Count == 0 ? T("Value") : FieldEditors.Label(column[^1].ToString()!));
                    if (target is not null) ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight());
                    ImGui.TableHeadersRow();
                    for (var i = 0; i < items.Count; i++)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextColored(Theme.TextDim, (i + 1).ToString());
                        ImGui.PushID(i);
                        foreach (var column in columns)
                        {
                            ImGui.TableNextColumn();
                            var cellPath = path.Append(i).Concat(column).ToList();
                            var holder = column.Count == 0 ? (object)list : RecordFields.Get(items[i]!, column.Take(column.Count - 1).ToList()) ?? items[i]!;
                            var value = column.Count == 0 ? items[i] : RecordFields.Get(items[i]!, column);
                            var (editable, declared) = column.Count == 0
                                ? (RecordFields.KindOf(value, element) is not (FieldKind.None or FieldKind.Bytes), element)
                                : RecordFields.Editability(holder, column[^1].ToString()!, value);
                            FieldEditors.Draw(app, target, column.Count == 0 ? $"[{i + 1}]" : FieldEditors.Label(column[^1].ToString()!), cellPath, value, declared, editable);
                        }
                        if (target is not null)
                        {
                            ImGui.TableNextColumn();
                            var index = i;
                            if (ImGui.SmallButton("x")) target.Apply(T("Remove item"), copy => RecordFields.RemoveItem(copy, at, index));
                            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Remove this row"));
                        }
                        ImGui.PopID();
                    }
                    ImGui.EndTable();
                }
            }
            else
            {
                for (var i = 0; i < items.Count; i++)
                {
                    ImGui.PushID(i);
                    var open = ImGui.TreeNodeEx($"{i + 1}. {Summary(app, items[i])}###item", ImGuiTreeNodeFlags.SpanAvailWidth | (items.Count <= 3 || ExpandAll ? ImGuiTreeNodeFlags.DefaultOpen : 0));
                    if (target is not null)
                    {
                        ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - ImGui.GetFrameHeight() - 4);
                        var index = i;
                        if (ImGui.SmallButton("x")) target.Apply(T("Remove item"), copy => RecordFields.RemoveItem(copy, at, index));
                    }
                    if (open)
                    {
                        path.Add(i);
                        if (RecordFields.IsComposite(items[i])) DrawMembers(app, items[i]!, RecordFields.PropertiesOf(items[i]!), path, target, depth + 1);
                        else FieldEditors.Draw(app, target, $"[{i + 1}]", path, items[i], element, true);
                        path.RemoveAt(path.Count - 1);
                        ImGui.TreePop();
                    }
                    ImGui.PopID();
                }
            }
        }
        else
        {
            ImGui.TextColored(Theme.TextDim, T("(empty)"));
        }

        if (target is not null && RecordFields.CanGrow(list))
        {
            // Abstract items (conditions, perk entries): pick which kind to add.
            var kinds = element is null || RecordFields.Creatable(element) is not null ? [] : RecordFields.ConcreteTypes(element);
            if (kinds.Count > 1)
            {
                if (ImGui.SmallButton(T("+ add") + " ...")) ImGui.OpenPopup("##addkind");
                if (ImGui.BeginPopup("##addkind"))
                {
                    foreach (var kind in kinds)
                        if (ImGui.Selectable(kind.Name))
                        {
                            var chosen = kind;
                            target.Apply(TF($"Add {kind.Name}"), copy => RecordFields.AddItem(copy, at, chosen));
                        }
                    ImGui.EndPopup();
                }
            }
            else if (ImGui.SmallButton(T("+ add"))) target.Apply(T("Add item"), copy => RecordFields.AddItem(copy, at));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Add a row at the end"));
        }
    }

    /// <summary>Key → value map (skill values): one row per key, the value editable.</summary>
    private static void DrawMap(EditorApp app, object map, List<object> path, EditTarget? target)
    {
        var valueType = RecordFields.ValueType(map);
        if (!ImGui.BeginTable("##map", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn(T("Key"), ImGuiTableColumnFlags.WidthStretch, 0.45f);
        ImGui.TableSetupColumn(T("Value"), ImGuiTableColumnFlags.WidthStretch, 0.55f);
        ImGui.TableHeadersRow();
        foreach (var (key, value) in RecordFields.Entries(map))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(FieldEditors.Label(key.ToString()!));
            ImGui.TableNextColumn();
            path.Add(key);
            var kind = RecordFields.KindOf(value, valueType);
            FieldEditors.Draw(app, target, key.ToString()!, path, value, valueType, kind is not (FieldKind.None or FieldKind.Bytes));
            path.RemoveAt(path.Count - 1);
        }
        ImGui.EndTable();
    }

    /// <summary>
    /// Columns of a grid for list items: their leaf fields up to two levels deep (Item / Count of an
    /// inventory entry). Null when items hold lists or too many fields — those get expandable rows.
    /// An empty column path means the item itself (plain lists).
    /// </summary>
    private static List<List<object>>? GridColumns(List<object?> items, Type? element)
    {
        var sample = items.FirstOrDefault(i => i is not null)
            ?? (element is not null && RecordFields.Creatable(element) is { } c && !typeof(IFormLinkGetter).IsAssignableFrom(c) ? Activator.CreateInstance(c) : null);
        if (sample is null || !RecordFields.IsComposite(sample)) return [[]];

        var columns = new List<List<object>>();
        foreach (var member in RecordFields.PropertiesOf(sample))
        {
            object? value;
            try { value = member.GetValue(sample); } catch { continue; }
            if (value is IEnumerable and not string && !RecordFields.IsBytes(value) && !RecordFields.IsGendered(value)) return null;
            // Optional sub-records that are empty in the sample (extra data of an inventory entry) get no column.
            if (value is null && RecordFields.KindOf(null, member.PropertyType) == FieldKind.None) continue;
            if (RecordFields.IsComposite(value))
            {
                foreach (var inner in RecordFields.PropertiesOf(value!))
                {
                    object? innerValue;
                    try { innerValue = inner.GetValue(value); } catch { continue; }
                    if (RecordFields.IsComposite(innerValue)) return null;
                    columns.Add([member.Name, inner.Name]);
                }
            }
            else columns.Add([member.Name]);
        }
        return columns.Count is > 0 and <= 7 ? columns : null;
    }

    /// <summary>One-line description of a list item for its row: its first link or name.</summary>
    private static string Summary(EditorApp app, object? item)
    {
        if (item is null) return "—";
        if (item is IFormLinkGetter link) return link.FormKeyNullable is { IsNull: false } k ? FormField.Describe(app, k) : "NULL";
        foreach (var leaf in RecordFields.Leaves(item, maxDepth: 2).Take(12))
        {
            if (leaf.Value is IFormLinkGetter { FormKeyNullable: { IsNull: false } key }) return FormField.Describe(app, key);
            if (leaf.Value is string { Length: > 0 } s) return s;
        }
        return item.GetType().Name.Replace("BinaryOverlay", "");
    }
}
