using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// The Creation Kit's package dialog: Package Data (template, owner quest, flags and the template's data
/// inputs — location, target, booleans… — named as the template names them), Schedule, and the procedure
/// tree (a package with a template shows the template's tree, read-only). Conditions, idles and the
/// begin / end / change events use the ordinary form parts.
/// </summary>
internal static class PackageEditor
{
    /// <summary>Selected branch of the procedure tree per package.</summary>
    private static readonly Dictionary<FormKey, int> SelectedBranch = new();

    public static bool Draws(FormTab tab) => tab.Title is "Package Data" or "Schedule" or "Procedure Tree";

    /// <summary>Autotests: select a branch of the procedure tree.</summary>
    public static void SelectForTest(FormKey package, int branch) => SelectedBranch[package] = branch;

    /// <summary>The package's template as it is now (an edit of it, else the winning version).</summary>
    public static IPackageGetter? Template(EditorApp app, IPackageGetter package) =>
        package.PackageTemplate.IsNull ? null : Current<IPackageGetter>(app, package.PackageTemplate.FormKey);

    /// <summary>Alias names of the owner quest and the data input names: what conditions of the package refer to.</summary>
    public static (Dictionary<int, string>? Aliases, Dictionary<int, string> Inputs) Context(EditorApp app, IPackageGetter package)
    {
        var quest = package.OwnerQuest.IsNull ? null : Current<IQuestGetter>(app, package.OwnerQuest.FormKey);
        return (quest is null ? null : QuestEditor.AliasNames(quest), Packages.InputNames(package, Template(app, package)));
    }

    private static T? Current<T>(EditorApp app, FormKey key) where T : class, IMajorRecordGetter
    {
        if (app.Document.Override(key)?.Record is T edited) return edited;
        var session = app.Session!;
        return new RecordLookup(session.Workspace, session.Catalog).Resolve<T>(key);
    }

    public static void Draw(EditorApp app, IPackageGetter package, FormTab tab, EditTarget? target)
    {
        switch (tab.Title)
        {
            case "Package Data": DrawData(app, package, target); break;
            case "Schedule": DrawSchedule(package, target); break;
            case "Procedure Tree": DrawTree(app, package, target); break;
        }
    }

    // ------------------------------------------------------------------ package data

    private static void DrawData(EditorApp app, IPackageGetter package, EditTarget? target)
    {
        var template = Template(app, package);
        FormView.DrawObject(app, package, ["EditorID", "Type", "OwnerQuest", "CombatStyle", "PreferredSpeed", "InterruptOverride"], [], target);

        // The template: picking one brings its data inputs (the CK fills them the same way).
        if (ImGui.BeginTable("##template", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch, 0.36f);
            ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch, 0.64f);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(T("Package template"));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("The template owns the procedure tree and names the data inputs; picking one fills the inputs with its defaults"));
            ImGui.TableNextColumn();
            if (target is null) FieldEditors.DrawLink(app, package.PackageTemplate.IsNull ? null : package.PackageTemplate.FormKey);
            else
            {
                FormKey? key = package.PackageTemplate.IsNull ? null : package.PackageTemplate.FormKey;
                if (FormField.Draw(app, "##packagetemplate", ref key, ["PACK"])) SetTemplate(app, target, key);
            }
            ImGui.EndTable();
        }

        ImGui.SeparatorText(T("Flags"));
        QuestEditor.FlagBoxes(target, T("Package flags"), ["Flags"], package.Flags, typeof(Package.Flag), 3);
        ImGui.SeparatorText(T("Interrupt flags"));
        QuestEditor.FlagBoxes(target, T("Interrupt flags"), ["InterruptFlags"], package.InterruptFlags, typeof(Package.InterruptFlag), 3);

        ImGui.SeparatorText(template is null ? T("Data inputs") : TF($"Data inputs (template {template.EditorID})"));
        DrawInputs(app, package, template, target);
    }

    /// <summary>Sets the template; a real template also brings its inputs (<see cref="Packages.UseTemplate"/>).</summary>
    public static void SetTemplate(EditorApp app, EditTarget target, FormKey? key)
    {
        if (key is null)
        {
            target.Apply(T("Clear package template"), copy => ((Package)copy).PackageTemplate.SetTo(FormKey.Null));
            return;
        }
        if (Current<IPackageGetter>(app, key.Value) is not { } template)
        {
            Log.Warn(TF($"{key}: not a package"));
            return;
        }
        if (template.Type != Package.Types.PackageTemplate)
        {
            Log.Warn(TF($"{template.EditorID} is a package, not a package template"));
            return;
        }
        target.Apply(TF($"Use package template {template.EditorID}"), copy => Packages.UseTemplate((Package)copy, template));
    }

    private static void DrawInputs(EditorApp app, IPackageGetter package, IPackageGetter? template, EditTarget? target)
    {
        var inputs = Packages.Inputs(package, template);
        if (inputs.Count == 0)
        {
            ImGui.TextColored(Theme.TextDim, template is null && package.Type != Package.Types.PackageTemplate ? T("No data inputs: pick a package template.") : T("(none)"));
            return;
        }
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("##inputs", 3, flags)) return;
        ImGui.TableSetupColumn(T("Name"), ImGuiTableColumnFlags.WidthStretch, 0.28f);
        ImGui.TableSetupColumn(T("Kind"), ImGuiTableColumnFlags.WidthStretch, 0.12f);
        ImGui.TableSetupColumn(T("Value"), ImGuiTableColumnFlags.WidthStretch, 0.60f);
        ImGui.TableHeadersRow();
        foreach (var input in inputs)
        {
            ImGui.TableNextRow();
            ImGui.PushID(input.Key);
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(input.Name);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(TF($"Data input {input.Key}"));
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextDim, T(Packages.KindOf(input.Data)));
            ImGui.TableNextColumn();
            DrawInput(app, input, ["Data", input.Key], target);
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private static void DrawInput(EditorApp app, Packages.Input input, IReadOnlyList<object> path, EditTarget? target)
    {
        switch (input.Data)
        {
            case IPackageDataBoolGetter b:
                FieldEditors.Draw(app, target, input.Name, [.. path, "Data"], b.Data, typeof(bool), true);
                break;
            case IPackageDataIntGetter i:
                FieldEditors.Draw(app, target, input.Name, [.. path, "Data"], i.Data, typeof(uint), true);
                break;
            case IPackageDataFloatGetter f:
                FieldEditors.Draw(app, target, input.Name, [.. path, "Data"], f.Data, typeof(float), true);
                break;
            case IPackageDataObjectListGetter o:
                FieldEditors.Draw(app, target, input.Name, [.. path, "Data"], o.Data, typeof(float?), true);
                break;
            case IPackageDataLocationGetter l:
                DrawLocation(app, input.Name, l.Location, [.. path, "Location"], target);
                break;
            case IPackageDataTargetGetter t:
                DrawTarget(app, input.Name, t.Target, [.. path, "Target"], target);
                break;
            case IPackageDataTopicGetter:
                FormView.DrawObject(app, input.Data, ["Topics"], path, target);
                break;
            default:
                ImGui.TextColored(Theme.TextDim, "—");
                break;
        }
    }

    private static readonly Dictionary<string, string> LocationKinds = new()
    {
        ["LocationTarget"] = "Near reference or location",
        ["LocationCell"] = "In cell",
        ["LocationObjectId"] = "Near object ID",
        ["LocationObjectType"] = "Near object type",
        ["LocationKeyword"] = "Near linked reference",
        ["LocationFallback"] = "Special location",
    };

    private static readonly Dictionary<string, string> TargetKinds = new()
    {
        ["PackageTargetSpecificReference"] = "Specific reference",
        ["PackageTargetObjectID"] = "Object ID",
        ["PackageTargetObjectType"] = "Object type",
        ["PackageTargetLinkedReference"] = "Linked reference",
        ["PackageTargetAlias"] = "Alias",
        ["PackageTargetSelf"] = "Self",
        ["PackageTargetUnknown"] = "Unknown",
    };

    /// <summary>A location input: its kind (near reference, in cell…), what it points at, and the radius.</summary>
    private static void DrawLocation(EditorApp app, string name, ILocationTargetRadiusGetter? location, IReadOnlyList<object> path, EditTarget? target)
    {
        if (location?.Target is null)
        {
            ImGui.TextColored(Theme.TextDim, T("(none)"));
            return;
        }
        KindCombo("##lockind", location.Target, typeof(ALocationTarget), LocationKinds, [.. path, "Target"], target);
        ImGui.PushID("target");
        FormView.DrawObject(app, location.Target, RecordFields.PropertiesOf(location.Target).Select(p => p.Name), [.. path, "Target"], target);
        ImGui.PopID();
        FormView.DrawObject(app, location, ["Radius"], path, target);
    }

    /// <summary>A target input: its kind (specific reference, object type, alias…), what it is, and the count or distance.</summary>
    private static void DrawTarget(EditorApp app, string name, IAPackageTargetGetter? packageTarget, IReadOnlyList<object> path, EditTarget? target)
    {
        if (packageTarget is null)
        {
            ImGui.TextColored(Theme.TextDim, T("(none)"));
            return;
        }
        KindCombo("##targetkind", packageTarget, typeof(APackageTarget), TargetKinds, path, target);
        if (packageTarget is IPackageTargetAliasGetter alias && ConditionsEditor.Aliases is { } aliases)
        {
            var at = path.Append("Alias").ToArray();
            ConditionsEditor.IndexCombo("##alias", alias.Alias, aliases, target, id => target!.Apply(T("Set package target alias"), copy => RecordFields.Set(copy, at, id)));
            FormView.DrawObject(app, packageTarget, ["CountOrDistance"], path, target);
        }
        else FormView.DrawObject(app, packageTarget, RecordFields.PropertiesOf(packageTarget).Select(p => p.Name), path, target);
    }

    /// <summary>
    /// The kind of an abstract part (a location or a package target): switching makes a fresh one of the
    /// chosen class in its place.
    /// </summary>
    private static void KindCombo(string id, object current, Type abstractType, IReadOnlyDictionary<string, string> labels, IReadOnlyList<object> path, EditTarget? target)
    {
        var name = current.GetType().Name.Replace("BinaryOverlay", "");
        string Label(string kind) => labels.TryGetValue(kind, out var label) ? T(label) : kind;
        ImGui.SetNextItemWidth(-1);
        if (target is null) { ImGui.Text(Label(name)); return; }
        if (!ImGui.BeginCombo(id, Label(name))) return;
        foreach (var kind in RecordFields.ConcreteTypes(abstractType))
        {
            if (!ImGui.Selectable(Label(kind.Name), kind.Name == name) || kind.Name == name) continue;
            var at = path.ToArray();
            var chosen = kind;
            target.Apply(TF($"Set kind {Label(kind.Name)}"), copy =>
            {
                var fresh = System.Activator.CreateInstance(chosen)!;
                RecordFields.FillRequired(fresh, includeOptional: true);
                RecordFields.Set(copy, at, fresh);
            });
        }
        ImGui.EndCombo();
    }

    // ------------------------------------------------------------------ schedule

    private static readonly string[] Months =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    private static void DrawSchedule(IPackageGetter package, EditTarget? target)
    {
        ImGui.TextColored(Theme.Accent, Summary(package));
        ImGui.Spacing();
        if (!ImGui.BeginTable("##schedule", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch, 0.36f);
        ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch, 0.64f);

        Row(T("Month"));
        Choice("##month", package.ScheduleMonth, -1, [T("Any"), .. Months.Select(T)], target, v => ("ScheduleMonth", (sbyte)v));
        Row(T("Day of week"));
        var days = Enum.GetValues<Package.DayOfWeek>();
        var day = Array.IndexOf(days, package.ScheduleDayOfWeek);
        ImGui.SetNextItemWidth(-1);
        var dayNames = days.Select(d => d == Package.DayOfWeek.Any ? T("Any") : FieldEditors.Label(d.ToString())).ToArray();
        if (target is null) ImGui.Text(day >= 0 ? dayNames[day] : package.ScheduleDayOfWeek.ToString());
        else if (ImGui.Combo("##day", ref day, dayNames, dayNames.Length))
        {
            var chosen = days[day];
            target.Apply(T("Set schedule day"), copy => RecordFields.Set(copy, ["ScheduleDayOfWeek"], chosen));
        }
        Row(T("Date"));
        Choice("##date", package.ScheduleDate, 0, [T("Any"), .. Enumerable.Range(1, 31).Select(d => d.ToString())], target, v => ("ScheduleDate", (byte)(v + 1)));
        Row(T("Hour"));
        Choice("##hour", package.ScheduleHour, -1, [T("Any"), .. Enumerable.Range(0, 24).Select(h => $"{h}:00")], target, v => ("ScheduleHour", (sbyte)v));
        Row(T("Minute"));
        Choice("##minute", package.ScheduleMinute, -1, [T("Any"), .. Enumerable.Range(0, 60).Select(m => $":{m:00}")], target, v => ("ScheduleMinute", (sbyte)v));
        Row(T("Duration"));
        var hours = package.ScheduleDurationInMinutes / 60;
        var minutes = package.ScheduleDurationInMinutes % 60;
        if (target is null) ImGui.Text(TF($"{hours} h {minutes} min"));
        else
        {
            var width = Math.Max((ImGui.GetContentRegionAvail().X - ImGui.GetFontSize() * 5) / 2, ImGui.GetFontSize() * 6);
            ImGui.SetNextItemWidth(width);
            if (Fields.Int("##hours", hours, out var h) && h >= 0)
            {
                var total = h * 60 + minutes;
                target.Apply(T("Set schedule duration"), copy => RecordFields.Set(copy, ["ScheduleDurationInMinutes"], total));
            }
            ImGui.SameLine();
            ImGui.Text(T("h"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(width);
            if (Fields.Int("##minutes", minutes, out var m) && m is >= 0 and < 60)
            {
                var total = hours * 60 + m;
                target.Apply(T("Set schedule duration"), copy => RecordFields.Set(copy, ["ScheduleDurationInMinutes"], total));
            }
            ImGui.SameLine();
            ImGui.Text(T("min"));
        }
        ImGui.EndTable();
    }

    /// <summary>
    /// A schedule field as a list: «Any» first (stored as <paramref name="any"/>), then the values; <paramref name="set"/>
    /// gives the member and the value to store for a list position (0 = first value after «Any»).
    /// </summary>
    private static void Choice(string id, int current, int any, string[] options, EditTarget? target, Func<int, (string Member, object Value)> set)
    {
        // Position in the list: «Any» is 0, a value v sits where set(v) would put it.
        var position = 0;
        for (var i = 1; i < options.Length; i++)
            if (Convert.ToInt32(set(i - 1).Value) == current) { position = i; break; }
        // A value outside the list (hand-made plugins) is shown as it is and cannot be picked again.
        var odd = current != any && position == 0;
        if (odd) { options = [.. options, current.ToString()]; position = options.Length - 1; }
        ImGui.SetNextItemWidth(-1);
        if (target is null) { ImGui.Text(options[position]); return; }
        if (!ImGui.Combo(id, ref position, options, options.Length) || (odd && position == options.Length - 1)) return;
        var (member, value) = position == 0 ? (set(0).Member, Convert.ChangeType(any, set(0).Value.GetType())) : set(position - 1);
        target.Apply(TF($"Set {FieldEditors.Label(member)}"), copy => RecordFields.Set(copy, [member], value));
    }

    private static void Row(string label)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.TableNextColumn();
    }

    /// <summary>The schedule in words: «Weekdays, 8:00, for 10 h» / «Any time».</summary>
    public static string Summary(IPackageGetter package)
    {
        var parts = new List<string>();
        if (package.ScheduleMonth >= 0 && package.ScheduleMonth < 12) parts.Add(T(Months[package.ScheduleMonth]));
        if (package.ScheduleDate > 0) parts.Add(TF($"day {package.ScheduleDate}"));
        if (package.ScheduleDayOfWeek != Package.DayOfWeek.Any) parts.Add(FieldEditors.Label(package.ScheduleDayOfWeek.ToString()));
        if (package.ScheduleHour >= 0) parts.Add($"{package.ScheduleHour}:{Math.Max((int)package.ScheduleMinute, 0):00}");
        var duration = package.ScheduleDurationInMinutes;
        if (duration > 0) parts.Add(duration % 60 == 0 ? TF($"for {duration / 60} h") : TF($"for {duration / 60} h {duration % 60} min"));
        return parts.Count == 0 ? T("Any time") : string.Join(", ", parts);
    }

    // ------------------------------------------------------------------ procedure tree

    private static void DrawTree(EditorApp app, IPackageGetter package, EditTarget? target)
    {
        // A package with a template runs the template's tree.
        var template = Template(app, package);
        var owner = package;
        if (package.ProcedureTree.Count == 0 && template is not null)
        {
            owner = template;
            target = null;
            ImGui.TextColored(Theme.TextDim, TF($"The procedure tree of the template {template.EditorID} (edit it in the template)."));
        }
        var nodes = Packages.Tree(owner.ProcedureTree);
        if (nodes.Count == 0)
        {
            ImGui.TextColored(Theme.TextDim, T("No procedure tree."));
            return;
        }
        var names = Packages.InputNames(owner, owner == package ? template : null);
        var row = Math.Clamp(SelectedBranch.GetValueOrDefault(package.FormKey), 0, nodes.Count - 1);
        QuestEditor.Split("##tree", () =>
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                // Indent(0) would indent by the style's default.
                if (node.Depth > 0) ImGui.Indent(node.Depth * ImGui.GetFontSize());
                if (ImGui.Selectable($"{BranchText(node.Branch)}##branch{i}", i == row)) SelectedBranch[package.FormKey] = i;
                if (node.Depth > 0) ImGui.Unindent(node.Depth * ImGui.GetFontSize());
            }
        }, () =>
        {
            var node = nodes[row];
            var branch = node.Branch;
            var path = new List<object> { "ProcedureTree", node.Index };
            FormView.DrawObject(app, branch, ["BranchType", "ProcedureType"], path, target);
            QuestEditor.FlagBoxes(target, T("Branch flags"), [.. path, "Flags"], branch.Flags, typeof(PackageBranch.Flag), 2);
            if (branch.Root is { } root)
            {
                ImGui.TextColored(Theme.TextDim, TF($"Sub-branches: {root.BranchCount}"));
                QuestEditor.FlagBoxes(target, T("Root flags"), [.. path, "Root", "Flags"], root.Flags, typeof(PackageRoot.Flag), 2);
            }
            if (branch.DataInputIndices.Count > 0)
            {
                ImGui.SeparatorText(T("Data inputs used"));
                foreach (var index in branch.DataInputIndices)
                    ImGui.BulletText(index == 255 ? T("(none)") : names.TryGetValue(index, out var name) ? $"{name} ({index})" : $"#{index}");
            }
            FormView.DrawObject(app, branch, ["Conditions"], path, target);
        });
    }

    private static string BranchText(IPackageBranchGetter branch) =>
        string.IsNullOrEmpty(branch.ProcedureType) ? branch.BranchType ?? "?" : branch.ProcedureType + (branch.BranchType is "Procedure" or null ? "" : $" ({branch.BranchType})");
}
