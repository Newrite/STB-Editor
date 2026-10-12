using System.Collections;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Core;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Ui;

/// <summary>
/// The Creation Kit's condition list (CTDA) in forms: one line per condition as the CK shows it
/// (Subject.GetIsID(Hulda) == 1 AND), operator, value and OR in the row; the selected condition's
/// function (searchable list of every function), Run On, parameters, Use Global and Swap below.
/// Copy / paste between records, reorder, add, remove. Every change is one undo step.
/// </summary>
internal static class ConditionsEditor
{
    /// <summary>Selected row per list (by path).</summary>
    private static readonly Dictionary<string, int> Selected = new();
    private static List<Condition> _clipboard = [];
    private static string _functionFilter = string.Empty;

    private static readonly Condition.RunOnType[] RunOnTypes = Enum.GetValues<Condition.RunOnType>();
    private static readonly CompareOperator[] Operators = Enum.GetValues<CompareOperator>();

    /// <summary>Autotests: what the last drawn list showed.</summary>
    public static List<string> LastLines { get; } = [];
    public static int ClipboardCount => _clipboard.Count;

    /// <summary>Alias ID → name of the quest whose conditions are drawn (set by the quest form), for Run On and parameters.</summary>
    public static Dictionary<int, string>? Aliases { get; set; }

    /// <summary>Data inputs of the package whose conditions are drawn (key → name), for «package data» indices.</summary>
    public static Dictionary<int, string>? PackageData { get; set; }

    private static string IndexText(int id, IReadOnlyDictionary<int, string>? names) => names is not null && names.TryGetValue(id, out var name) ? $"{id}: {name}" : id.ToString();

    /// <summary>Autotests and screenshots: select this row in the lists drawn next.</summary>
    public static int? ForceSelect { get; set; }

    /// <summary>Autotests: the copy button.</summary>
    public static void CopyForTest(IEnumerable<IConditionGetter> conditions) => _clipboard = Conditions.Copy(conditions);

    public static void Draw(EditorApp app, IEnumerable list, IReadOnlyList<object> path, EditTarget? target)
    {
        var items = list.Cast<IConditionGetter>().ToList();
        var key = FieldEditors.PathKey(path);
        var at = path.ToArray();
        string Name(FormKey? k) => NameOf(app, k);

        if (target is not null)
        {
            if (ImGui.SmallButton(T("+ condition")))
            {
                target.Apply(T("Add condition"), copy => Conditions.ListAt(copy, at).Add(Conditions.New()));
                Selected[key] = items.Count;
            }
            ImGui.SameLine();
        }
        if (ImGui.SmallButton(T("Copy conditions")) && items.Count > 0)
        {
            _clipboard = Conditions.Copy(items);
            Services.Log.Info(TF($"{items.Count} condition(s) copied"));
        }
        if (target is not null && _clipboard.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(TF($"Paste ({_clipboard.Count})")))
            {
                var pasted = Conditions.Copy(_clipboard);
                target.Apply(T("Paste conditions"), copy =>
                {
                    var into = Conditions.ListAt(copy, at);
                    foreach (var c in Conditions.Copy(pasted)) into.Add(c);
                });
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Appends the copied conditions to this list"));
        }

        LastLines.Clear();
        LastLines.AddRange(items.Select(c => Conditions.Describe(c, Name)));
        if (items.Count == 0)
        {
            ImGui.TextColored(Theme.TextDim, T("(no conditions: always true)"));
            return;
        }

        if (ForceSelect is { } forced && forced < items.Count) Selected[key] = forced;
        var selected = Selected.TryGetValue(key, out var s) && s < items.Count ? s : -1;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable;
        if (ImGui.BeginTable("##conditions", target is null ? 5 : 6, flags))
        {
            ImGui.TableSetupColumn(T("Run on"), ImGuiTableColumnFlags.WidthStretch, 0.7f);
            ImGui.TableSetupColumn(T("Function"), ImGuiTableColumnFlags.WidthStretch, 2.2f);
            ImGui.TableSetupColumn(T("Comp"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 2.8f);
            ImGui.TableSetupColumn(T("Value"), ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn(T("Or"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFrameHeight() * 1.2f);
            if (target is not null) ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 3.6f);
            ImGui.TableHeadersRow();
            for (var i = 0; i < items.Count; i++)
            {
                var condition = items[i];
                var index = i;
                ImGui.PushID(i);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                var runOn = condition.Data.RunOnType switch
                {
                    Condition.RunOnType.QuestAlias => TF($"Alias {IndexText(condition.Data.RunOnTypeIndex, Aliases)}"),
                    Condition.RunOnType.PackageData when PackageData is not null => TF($"Package data {IndexText(condition.Data.RunOnTypeIndex, PackageData)}"),
                    _ => Conditions.RunOn(condition.Data, Name),
                };
                if (ImGui.Selectable(runOn + "##row", i == selected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap))
                    Selected[key] = selected = i == selected ? -1 : i;
                ImGui.TableNextColumn();
                var call = Conditions.Call(condition.Data, Name);
                if (condition.Flags.HasFlag(Condition.Flag.SwapSubjectAndTarget)) call += "  [swap]";
                ImGui.TextColored(Theme.Accent, call);

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                if (target is null) ImGui.Text(Conditions.Operator(condition.CompareOperator));
                else if (ImGui.BeginCombo("##op", Conditions.Operator(condition.CompareOperator), ImGuiComboFlags.NoArrowButton))
                {
                    foreach (var op in Operators)
                        if (ImGui.Selectable(Conditions.Operator(op), op == condition.CompareOperator))
                            Change(target, at, index, T("Condition operator"), c => c.CompareOperator = op);
                    ImGui.EndCombo();
                }

                ImGui.TableNextColumn();
                var valuePath = path.Append(i).Append("ComparisonValue").ToList();
                object value = condition is IConditionGlobalGetter g ? g.ComparisonValue : ((IConditionFloatGetter)condition).ComparisonValue;
                FieldEditors.Draw(app, target, T("Comparison value"), valuePath, value, value.GetType(), true);

                ImGui.TableNextColumn();
                var or = condition.Flags.HasFlag(Condition.Flag.OR);
                if (target is null) ImGui.Text(or ? "OR" : "AND");
                else if (ImGui.Checkbox("##or", ref or))
                    Change(target, at, index, T("Condition OR"), c => c.Flags = or ? c.Flags | Condition.Flag.OR : c.Flags & ~Condition.Flag.OR);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("OR with the next condition (otherwise AND). As in the game: ORs group before ANDs."));

                if (target is not null)
                {
                    ImGui.TableNextColumn();
                    if (i > 0 && ImGui.SmallButton("^")) Move(target, at, index, -1);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Move up"));
                    ImGui.SameLine(ImGui.GetFontSize() * 1.1f);
                    if (i < items.Count - 1 && ImGui.SmallButton("v")) Move(target, at, index, 1);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Move down"));
                    ImGui.SameLine(ImGui.GetFontSize() * 2.3f);
                    if (ImGui.SmallButton("x"))
                    {
                        target.Apply(T("Remove condition"), copy => Conditions.ListAt(copy, at).RemoveAt(index));
                        Selected.Remove(key);
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Remove this condition"));
                }
                ImGui.PopID();
            }
            ImGui.EndTable();
        }

        if (selected >= 0) DrawDetails(app, items[selected], path, selected, target);
        else ImGui.TextColored(Theme.TextDim, T("Click a condition to change its function, run on and parameters."));
    }

    /// <summary>The selected condition: function, Run On (with its reference or index), parameters, switches.</summary>
    private static void DrawDetails(EditorApp app, IConditionGetter condition, IReadOnlyList<object> path, int index, EditTarget? target)
    {
        var at = path.ToArray();
        var data = condition.Data;
        var dataPath = path.Append(index).Append("Data").ToList();
        ImGui.PushID("details");
        ImGui.SeparatorText(TF($"Condition {index + 1}"));
        if (!ImGui.BeginTable("##detail", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.PopID();
            return;
        }
        ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch, 0.3f);
        ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch, 0.7f);

        Row(T("Function"));
        var function = Conditions.FunctionName(data);
        ImGui.SetNextItemWidth(-1);
        if (target is null) ImGui.Text(function);
        else if (ImGui.BeginCombo("##function", function, ImGuiComboFlags.HeightLargest))
        {
            if (ImGui.IsWindowAppearing()) { _functionFilter = string.Empty; ImGui.SetKeyboardFocusHere(); }
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##filter", T("Search functions"), ref _functionFilter, 64);
            var matches = Conditions.Functions.Where(f => _functionFilter.Length == 0 || f.ToString().Contains(_functionFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (ImGui.BeginChild("##functions", new System.Numerics.Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 16)))
            {
                var clipper = new ImGuiListClipper();
                clipper.Begin(matches.Count);
                while (clipper.Step())
                {
                    for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        var f = matches[i];
                        if (ImGui.Selectable(f.ToString(), f.ToString() == function))
                        {
                            Change(target, at, index, TF($"Condition function {f}"), c => Conditions.SetFunction(c, f));
                            ImGui.CloseCurrentPopup();
                        }
                    }
                }
                clipper.End();
            }
            ImGui.EndChild();
            ImGui.EndCombo();
        }

        Row(T("Run on"));
        ImGui.SetNextItemWidth(-1);
        if (target is null) ImGui.Text(data.RunOnType.ToString());
        else if (ImGui.BeginCombo("##runon", RunOnLabel(data.RunOnType)))
        {
            foreach (var type in RunOnTypes)
                if (ImGui.Selectable(RunOnLabel(type), type == data.RunOnType))
                    Change(target, at, index, T("Condition run on"), c => c.Data.RunOnType = type);
            ImGui.EndCombo();
        }
        switch (data.RunOnType)
        {
            case Condition.RunOnType.Reference:
                Row(T("Reference"));
                FieldEditors.Draw(app, target, T("Run on reference"), dataPath.Append("Reference").ToList(), data.Reference, data.Reference.GetType(), true);
                break;
            case Condition.RunOnType.QuestAlias when Aliases is not null:
                Row(T("Alias"));
                IndexCombo("##runonalias", data.RunOnTypeIndex, Aliases, target, id => Change(target!, at, index, T("Condition alias"), c => c.Data.RunOnTypeIndex = id));
                break;
            case Condition.RunOnType.PackageData when PackageData is not null:
                Row(T("Package data"));
                IndexCombo("##runondata", data.RunOnTypeIndex, PackageData, target, id => Change(target!, at, index, T("Condition package data"), c => c.Data.RunOnTypeIndex = id));
                break;
            case Condition.RunOnType.QuestAlias:
            case Condition.RunOnType.PackageData:
                Row(data.RunOnType == Condition.RunOnType.QuestAlias ? T("Alias index") : T("Package data index"));
                FieldEditors.Draw(app, target, T("Run on index"), dataPath.Append("RunOnTypeIndex").ToList(), data.RunOnTypeIndex, typeof(int), true);
                break;
        }

        foreach (var parameter in Conditions.Parameters(data))
        {
            Row(FieldEditors.Label(parameter.Name));
            object? value;
            try { value = parameter.GetValue(data); } catch { value = null; }
            var parameterPath = dataPath.Append(parameter.Name).ToList();
            if (value is not null && Conditions.IsLinkOrIndex(value.GetType()))
            {
                // A form, or with "parameters are aliases / package data" an index.
                if (data.UseAliases && Aliases is not null)
                {
                    var number = value.GetType().GetProperty("Index")?.GetValue(value) as uint? ?? 0;
                    var indexPath = parameterPath.Append("Index").ToArray();
                    IndexCombo("##paramalias", (int)number, Aliases, target, id => target!.Apply(T("Condition parameter alias"), copy => RecordFields.Set(copy, indexPath, (uint)id)));
                }
                else if (data.UsePackageData && PackageData is not null)
                {
                    var number = value.GetType().GetProperty("Index")?.GetValue(value) as uint? ?? 0;
                    var indexPath = parameterPath.Append("Index").ToArray();
                    IndexCombo("##paramdata", (int)number, PackageData, target, id => target!.Apply(T("Condition parameter package data"), copy => RecordFields.Set(copy, indexPath, (uint)id)));
                }
                else if (data.UseAliases || data.UsePackageData)
                {
                    var number = value.GetType().GetProperty("Index")?.GetValue(value);
                    FieldEditors.Draw(app, target, FieldEditors.Label(parameter.Name), parameterPath.Append("Index").ToList(), number, typeof(uint?), true);
                }
                else
                {
                    var link = value.GetType().GetProperty("Link")?.GetValue(value);
                    FieldEditors.Draw(app, target, FieldEditors.Label(parameter.Name), parameterPath.Append("Link").ToList(), link, link?.GetType(), true);
                }
            }
            else
            {
                var (editable, declared) = RecordFields.Editability(data, parameter.Name, value);
                FieldEditors.Draw(app, target, FieldEditors.Label(parameter.Name), parameterPath, value, declared ?? parameter.PropertyType, editable);
            }
        }

        if (Conditions.HasFormParameter(data))
        {
            Row(T("Parameters are"));
            var mode = data.UseAliases ? 1 : data.UsePackageData ? 2 : 0;
            ImGui.SetNextItemWidth(-1);
            string[] modes = [T("forms"), T("quest aliases"), T("package data")];
            if (target is null) ImGui.Text(modes[mode]);
            else if (ImGui.Combo("##parammode", ref mode, modes, modes.Length))
            {
                var chosen = mode;
                Change(target, at, index, T("Condition parameters"), c => { c.Data.UseAliases = chosen == 1; c.Data.UsePackageData = chosen == 2; });
            }
        }

        Row(T("Use global"));
        var global = condition is IConditionGlobalGetter;
        if (target is null) ImGui.Text(global ? T("yes") : T("no"));
        else if (ImGui.Checkbox("##global", ref global))
        {
            var on = global;
            target.Apply(T("Condition use global"), copy =>
            {
                var list = Conditions.ListAt(copy, at);
                list[index] = Conditions.WithGlobal((Condition)list[index]!, on);
            });
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Compare with a global variable instead of a number"));

        Row(T("Swap subject and target"));
        var swap = condition.Flags.HasFlag(Condition.Flag.SwapSubjectAndTarget);
        if (target is null) ImGui.Text(swap ? T("yes") : T("no"));
        else if (ImGui.Checkbox("##swap", ref swap))
            Change(target, at, index, T("Condition swap subject and target"),
                c => c.Flags = swap ? c.Flags | Condition.Flag.SwapSubjectAndTarget : c.Flags & ~Condition.Flag.SwapSubjectAndTarget);

        ImGui.EndTable();
        ImGui.PopID();
    }

    /// <summary>A quest alias or a package data input by name (<paramref name="names"/>: index → name).</summary>
    public static void IndexCombo(string id, int current, IReadOnlyDictionary<int, string>? names, EditTarget? target, Action<int> set)
    {
        ImGui.SetNextItemWidth(-1);
        if (target is null || names is null) { ImGui.Text(IndexText(current, names)); return; }
        if (!ImGui.BeginCombo(id, IndexText(current, names))) return;
        foreach (var (index, _) in names.OrderBy(a => a.Key))
            if (ImGui.Selectable(IndexText(index, names), index == current)) set(index);
        ImGui.EndCombo();
    }

    private static void Row(string label)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.TableNextColumn();
    }

    private static string RunOnLabel(Condition.RunOnType type) => type switch
    {
        Condition.RunOnType.Subject => T("Subject"),
        Condition.RunOnType.Target => T("Target"),
        Condition.RunOnType.Reference => T("Reference"),
        Condition.RunOnType.CombatTarget => T("Combat Target"),
        Condition.RunOnType.LinkedReference => T("Linked Reference"),
        Condition.RunOnType.QuestAlias => T("Quest Alias"),
        Condition.RunOnType.PackageData => T("Package Data"),
        Condition.RunOnType.EventData => T("Event Data"),
        _ => type.ToString(),
    };

    private static void Change(EditTarget target, object[] path, int index, string name, Action<Condition> change) =>
        target.Apply(name, copy => change((Condition)Conditions.ListAt(copy, path)[index]!));

    private static void Move(EditTarget target, object[] path, int index, int by)
    {
        target.Apply(T("Move condition"), copy =>
        {
            var list = Conditions.ListAt(copy, path);
            var item = list[index];
            list.RemoveAt(index);
            list.Insert(index + by, item);
        });
        var key = FieldEditors.PathKey(path);
        if (Selected.TryGetValue(key, out var s) && s == index) Selected[key] = index + by;
    }

    /// <summary>A form for condition lines: EditorID, PlayerRef for the player, else the FormID.</summary>
    public static string NameOf(EditorApp app, FormKey? key)
    {
        if (key is not { IsNull: false } k) return "NONE";
        if (k.ID == 0x14 && k.ModKey.FileName.String.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase)) return "PlayerRef";
        var session = app.Session!;
        return session.Catalog.Get(k)?.EditorId ?? session.Workspace.FormIdText(k);
    }
}
