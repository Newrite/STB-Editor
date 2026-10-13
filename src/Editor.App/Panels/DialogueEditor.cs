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
/// A quest's dialogue (CK: the quest's Player Dialogue / Misc tabs and the Topic Info window): branches
/// with their topics and the quest's other topics by category on the left; the selected topic, its
/// lines (INFO) and the selected line on the right — responses with emotion, conditions, prompt,
/// speaker, links. Lines are saved under their topic (the topic itself only as a header when only its
/// lines changed).
/// </summary>
internal static partial class DialogueEditor
{
    private static readonly Dictionary<FormKey, FormKey> SelectedTopic = new();
    private static readonly Dictionary<FormKey, FormKey> SelectedLine = new();

    /// <summary>Autotests: select a topic of a quest and a line of it.</summary>
    public static void SelectForTest(FormKey quest, FormKey topic, FormKey? line = null)
    {
        SelectedTopic[quest] = topic;
        if (line is { } l) SelectedLine[topic] = l;
    }

    /// <summary>Autotests: what the last drawn topic listed.</summary>
    public static List<string> LastLines { get; } = [];

    // ------------------------------------------------------------------ records as they are now

    private static T? Current<T>(EditorApp app, FormKey key) where T : class, IMajorRecordGetter =>
        app.Document.Override(key)?.Record as T ?? new RecordLookup(app.Session!.Workspace, app.Session.Catalog).Resolve<T>(key);

    /// <summary>The quest's topics: from the index, plus topics made or moved to it in this session.</summary>
    private static List<IDialogTopicGetter> Topics(EditorApp app, IQuestGetter quest, DialogueIndex index)
    {
        var keys = index.TopicsOf(quest.FormKey).ToHashSet();
        foreach (var draft in app.Document.Drafts.Values.OfType<OverrideDraft>())
            if (draft.Record is IDialogTopicGetter t) keys.Add(t.FormKey);
        return keys.Select(k => Current<IDialogTopicGetter>(app, k)).OfType<IDialogTopicGetter>()
            .Where(t => t.Quest.FormKeyNullable == quest.FormKey)
            .OrderBy(t => t.EditorID, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<IDialogBranchGetter> Branches(EditorApp app, IQuestGetter quest, DialogueIndex index)
    {
        var keys = index.BranchesOf(quest.FormKey).ToHashSet();
        foreach (var draft in app.Document.Drafts.Values.OfType<OverrideDraft>())
            if (draft.Record is IDialogBranchGetter b) keys.Add(b.FormKey);
        return keys.Select(k => Current<IDialogBranchGetter>(app, k)).OfType<IDialogBranchGetter>()
            .Where(b => b.Quest.FormKeyNullable == quest.FormKey)
            .OrderBy(b => b.EditorID, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A topic's lines as they are now: merged from the plugins, edited ones replaced, new ones added.</summary>
    public static List<(IDialogResponsesGetter Line, string Plugin)> Lines(EditorApp app, FormKey topic)
    {
        var lines = DialogueIndex.Lines(app.Session!.Workspace, topic)
            .Select(l => (app.Document.Override(l.Line.FormKey)?.Record as IDialogResponsesGetter ?? l.Line, l.Plugin)).ToList();
        foreach (var draft in app.Document.Drafts.Values.OfType<OverrideDraft>())
            if (draft.Place == RecordPlace.DialogInfo && draft.Cell == topic && lines.All(l => l.Item1.FormKey != draft.Key))
                lines.Add(((IDialogResponsesGetter)draft.Record, app.ActivePlugin?.Name ?? "?"));
        // Removed lines go; the rest in the order of their «previous line» chain (as the game places them).
        lines.RemoveAll(l => l.Item1 is null || app.Document.IsRemoved(l.Item1.FormKey));
        return SiblingChains.Ordered(lines, l => l.Item1.FormKey, l => l.Item1?.PreviousDialog?.FormKeyNullable is { IsNull: false } p ? p : null);
    }

    /// <summary>
    /// Puts a line right after <paramref name="after"/> (null: first) by its «previous line» (PNAM) and the neighbours': one
    /// undo step; lines of other plugins become overrides in the active file.
    /// </summary>
    public static void MoveLine(EditorApp app, FormKey topic, List<(IDialogResponsesGetter Line, string Plugin)> lines, FormKey moved, FormKey? after)
    {
        var keys = lines.Select(l => l.Line.FormKey).ToList();
        var edits = SiblingChains.Move(keys, moved, topic, keys, after, _ => topic);
        CommitPrevious(app, topic, lines, edits.ToDictionary(e => e.Key, e => e.Value.Previous), T("Move dialogue line"));
    }

    private static void CommitPrevious(EditorApp app, FormKey topic, List<(IDialogResponsesGetter Line, string Plugin)> lines, Dictionary<FormKey, FormKey?> previous, string name,
        params IEditCommand[] extra)
    {
        var steps = new List<IEditCommand>();
        foreach (var (key, before) in previous)
        {
            if (lines.FirstOrDefault(l => l.Line.FormKey == key).Line is not { } line) continue;
            if (line.PreviousDialog.FormKeyNullable is { IsNull: false } now ? now == before : before is null) continue;
            var copy = (DialogResponses)app.Document.WorkingCopy(key, line);
            if (before is { } p) copy.PreviousDialog.SetTo(p); else copy.PreviousDialog.Clear();
            var old = app.Document.Override(key);
            steps.Add(new RecordEditCommand(name, old?.With((Mutagen.Bethesda.Plugins.Records.IMajorRecord)old.Record.DeepCopy()), old?.With(copy) ?? new OverrideDraft(copy, RecordPlace.DialogInfo, null, topic)));
        }
        steps.AddRange(extra);
        if (steps.Count > 0) app.Document.Record(steps.Count == 1 ? steps[0] : new CompositeCommand(name, steps));
    }

    /// <summary>
    /// Removes a line of the active file (a new one, or one it has on disk); the line after it then follows the removed one's
    /// previous. Lines of other plugins are not removed (the CK would mark them deleted): give them a condition instead.
    /// </summary>
    public static bool DeleteLine(EditorApp app, FormKey topic, List<(IDialogResponsesGetter Line, string Plugin)> lines, IDialogResponsesGetter line)
    {
        var onDisk = app.ActivePlugin is { } active && line.FormKey.ModKey.FileName.String.Equals(active.Name, StringComparison.OrdinalIgnoreCase) && !active.IsNew
            && app.Session!.Workspace.Active.Any(p => p.Name.Equals(active.Name, StringComparison.OrdinalIgnoreCase));
        var draft = app.Document.Draft(line.FormKey);
        var isNew = draft is OverrideDraft && DialogueIndex.Lines(app.Session!.Workspace, topic).All(l => l.Line.FormKey != line.FormKey);
        if (!onDisk && !isNew)
        {
            Log.Warn(T("Lines of other plugins are not removed (the game would see them deleted): give the line a condition that never passes instead"));
            return false;
        }
        var at = lines.FindIndex(l => l.Line.FormKey == line.FormKey);
        var previous = new Dictionary<FormKey, FormKey?>();
        if (at >= 0 && at + 1 < lines.Count) previous[lines[at + 1].Line.FormKey] = at > 0 ? lines[at - 1].Line.FormKey : null;
        var removal = new RemoveRecordCommand(T("Delete dialogue line"), draft, onDisk ? new RemovalDraft(line.FormKey, "INFO", line.EditorID, true) : null, line.FormKey);
        CommitPrevious(app, topic, lines, previous, T("Delete dialogue line"), removal);
        return true;
    }

    private static string FirstText(IDialogResponsesGetter line) =>
        line.Responses.FirstOrDefault()?.Text?.String is { Length: > 0 } text ? text.ReplaceLineEndings(" ") : T("(no text)");

    private static string Short(string text, int length = 70) => text.Length <= length ? text : text[..length] + "…";

    // ------------------------------------------------------------------ drawing

    public static void Draw(EditorApp app, IQuestGetter quest, bool editable)
    {
        if (app.Session is not { } session) return;
        var task = session.Dialogue;
        if (!task.IsCompleted)
        {
            ImGui.TextColored(Theme.Warning, T("Listing the quests' topics (once per session)…"));
            return;
        }
        if (!task.IsCompletedSuccessfully)
        {
            ImGui.TextColored(Theme.Error, TF($"Dialogue index failed: {task.Exception?.GetBaseException().Message}"));
            return;
        }
        var topics = Topics(app, quest, task.Result);
        var branches = Branches(app, quest, task.Result);
        var selected = SelectedTopic.TryGetValue(quest.FormKey, out var s) && topics.Any(t => t.FormKey == s) ? s : topics.FirstOrDefault()?.FormKey;

        // CK: Dialogue Views — the same topics as a graph of where lines lead.
        if (ImGui.RadioButton(T("List"), !_graph)) _graph = false;
        ImGui.SameLine();
        if (ImGui.RadioButton(T("Graph"), _graph)) _graph = true;
        if (_graph)
        {
            DrawGraph(app, quest, topics, branches);
            return;
        }

        var height = Math.Max(ImGui.GetContentRegionAvail().Y, ImGui.GetFontSize() * 14);
        if (!ImGui.BeginTable("##dialogue", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("tree", ImGuiTableColumnFlags.WidthStretch, 0.3f);
        ImGui.TableSetupColumn("topic", ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (ImGui.BeginChild("##tree", new Vector2(0, height)))
            DrawTree(app, quest, topics, branches, selected, editable);
        ImGui.EndChild();
        ImGui.TableNextColumn();
        if (ImGui.BeginChild("##topic", new Vector2(0, height)) && selected is { } topicKey && topics.FirstOrDefault(t => t.FormKey == topicKey) is { } topic)
            DrawTopic(app, quest, topic, editable);
        ImGui.EndChild();
        ImGui.EndTable();
    }

    private static void DrawTree(EditorApp app, IQuestGetter quest, List<IDialogTopicGetter> topics, List<IDialogBranchGetter> branches, FormKey? selected, bool editable)
    {
        if (editable)
        {
            if (ImGui.SmallButton(T("+ branch"))) NewBranch(app, quest);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("A player dialogue branch with its starting topic"));
            ImGui.SameLine();
            if (ImGui.SmallButton(T("+ topic"))) NewTopic(app, quest, topics.FirstOrDefault(t => t.FormKey == selected)?.Branch.FormKeyNullable);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("A topic in the selected topic's branch (or a misc topic)"));
        }
        void TopicRow(IDialogTopicGetter topic, bool start)
        {
            var text = topic.Name?.String is { Length: > 0 } prompt ? $"{topic.EditorID}  \"{Short(prompt, 40)}\"" : topic.EditorID ?? topic.FormKey.ToString();
            if (start) text += T("  (start)");
            if (ImGui.Selectable($"{text}##{topic.FormKey}", topic.FormKey == selected)) SelectedTopic[quest.FormKey] = topic.FormKey;
        }

        var inBranch = new HashSet<FormKey>();
        foreach (var branch in branches)
        {
            var flags = branch.Flags is { } f && f.HasFlag(DialogBranch.Flag.TopLevel) ? T(" [top level]") : "";
            if (!ImGui.TreeNodeEx($"{branch.EditorID}{flags}##{branch.FormKey}", ImGuiTreeNodeFlags.DefaultOpen))
            {
                inBranch.UnionWith(topics.Where(t => t.Branch.FormKeyNullable == branch.FormKey).Select(t => t.FormKey));
                continue;
            }
            foreach (var topic in topics.Where(t => t.Branch.FormKeyNullable == branch.FormKey)
                         .OrderBy(t => t.FormKey == branch.StartingTopic.FormKeyNullable ? 0 : 1).ThenBy(t => t.EditorID, StringComparer.OrdinalIgnoreCase))
            {
                inBranch.Add(topic.FormKey);
                TopicRow(topic, topic.FormKey == branch.StartingTopic.FormKeyNullable);
            }
            ImGui.TreePop();
        }
        foreach (var group in topics.Where(t => !inBranch.Contains(t.FormKey)).GroupBy(t => t.Category).OrderBy(g => g.Key))
        {
            if (!ImGui.TreeNodeEx($"{T(group.Key.ToString())} ({group.Count()})##cat{group.Key}", ImGuiTreeNodeFlags.DefaultOpen)) continue;
            foreach (var topic in group) TopicRow(topic, false);
            ImGui.TreePop();
        }
        if (topics.Count == 0) ImGui.TextColored(Theme.TextDim, T("This quest has no dialogue."));
    }

    internal static void DrawTopic(EditorApp app, IQuestGetter quest, IDialogTopicGetter topic, bool editable)
    {
        var document = app.Document;
        var topicTarget = editable
            ? new EditTarget(() => document.WorkingCopy(topic.FormKey, topic), (name, edited) => document.CommitEdit(name, edited, RecordPlace.TopLevel))
            : null;
        ImGui.SeparatorText(TF($"Topic {topic.EditorID}"));
        FormView.DrawObject(app, topic, ["EditorID", "Name", "Priority", "Category", "Subtype", "Branch", "TopicFlags"], [], topicTarget);

        var lines = Lines(app, topic.FormKey);
        LastLines.Clear();
        LastLines.AddRange(lines.Select(l => FirstText(l.Line)));
        ImGui.SeparatorText(TF($"Lines ({lines.Count})"));
        if (editable)
        {
            if (ImGui.SmallButton(T("+ line"))) NewLine(app, topic, lines);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("A new line (INFO) at the end of the topic"));
        }
        var selected = SelectedLine.TryGetValue(topic.FormKey, out var s) && lines.Any(l => l.Line.FormKey == s) ? s : lines.FirstOrDefault().Line?.FormKey;
        var rows = Math.Clamp(lines.Count, 1, 10);
        if (ImGui.BeginTable("##lines", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, (rows + 1.3f) * ImGui.GetTextLineHeightWithSpacing())))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 1.6f);
            ImGui.TableSetupColumn(T("Text"), ImGuiTableColumnFlags.WidthStretch, 3f);
            ImGui.TableSetupColumn(T("Conditions"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 4.5f);
            ImGui.TableSetupColumn(T("Plugin"), ImGuiTableColumnFlags.WidthStretch, 0.9f);
            ImGui.TableHeadersRow();
            for (var i = 0; i < lines.Count; i++)
            {
                var (line, plugin) = lines[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, (i + 1).ToString());
                ImGui.TableNextColumn();
                var changed = document.Override(line.FormKey) is not null;
                if (ImGui.Selectable($"{Short(FirstText(line), 90)}{(changed ? " *" : "")}##{line.FormKey}", line.FormKey == selected, ImGuiSelectableFlags.SpanAllColumns))
                    SelectedLine[topic.FormKey] = line.FormKey;
                if (editable) LineDragAndMenu(app, topic, lines, i);
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, line.Conditions.Count == 0 ? "—" : line.Conditions.Count.ToString());
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, plugin);
            }
            ImGui.EndTable();
        }

        if (selected is { } lineKey && lines.FirstOrDefault(l => l.Line.FormKey == lineKey).Line is { } current)
            DrawLine(app, topic, current, editable);
    }

    private static void LineDragAndMenu(EditorApp app, IDialogTopicGetter topic, List<(IDialogResponsesGetter Line, string Plugin)> lines, int i)
    {
        var line = lines[i].Line;
        if (ImGui.BeginDragDropSource())
        {
            unsafe
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(line.FormKey.ToString());
                fixed (byte* p = bytes) ImGui.SetDragDropPayload("STB_INFO", p, (nuint)bytes.Length);
            }
            ImGui.Text(Short(FirstText(line), 50));
            ImGui.EndDragDropSource();
        }
        if (ImGui.BeginDragDropTarget())
        {
            unsafe
            {
                var payload = ImGui.AcceptDragDropPayload("STB_INFO");
                if (payload.Handle != null && payload.Data != null && FormKey.TryFactory(System.Text.Encoding.UTF8.GetString((byte*)payload.Data, payload.DataSize), out var key)
                    && key != line.FormKey && lines.Any(l => l.Line.FormKey == key))
                    MoveLine(app, topic.FormKey, lines, key, line.FormKey);
            }
            ImGui.EndDragDropTarget();
        }
        if (!ImGui.BeginPopupContextItem($"##linectx{line.FormKey}")) return;
        if (ImGui.MenuItem(T("Move up"), (string?)null, false, i > 0)) MoveLine(app, topic.FormKey, lines, line.FormKey, i > 1 ? lines[i - 2].Line.FormKey : null);
        if (ImGui.MenuItem(T("Move down"), (string?)null, false, i + 1 < lines.Count)) MoveLine(app, topic.FormKey, lines, line.FormKey, lines[i + 1].Line.FormKey);
        ImGui.Separator();
        if (ImGui.MenuItem(T("Delete line"))) DeleteLine(app, topic.FormKey, lines, line);
        ImGui.EndPopup();
    }

    private static void DrawLine(EditorApp app, IDialogTopicGetter topic, IDialogResponsesGetter line, bool editable)
    {
        var document = app.Document;
        var target = editable
            ? new EditTarget(() => document.WorkingCopy(line.FormKey, line), (name, edited) => document.CommitEdit(name, edited, RecordPlace.DialogInfo, cell: topic.FormKey))
            : null;
        ImGui.PushID(line.FormKey.ToString());
        ImGui.SeparatorText(TF($"Line {app.Session!.Workspace.FormIdText(line.FormKey)}"));

        // Flags as checkboxes, reset hours next to them.
        var raw = (long)(line.Flags?.Flags ?? 0);
        if (ImGui.BeginTable("##lineflags", 3))
        {
            foreach (var flag in Enum.GetValues<DialogResponses.Flag>())
            {
                ImGui.TableNextColumn();
                var on = (raw & (long)flag) != 0;
                if (target is null) ImGui.BeginDisabled();
                if (ImGui.Checkbox(FieldEditors.Label(flag.ToString()), ref on) && target is not null)
                {
                    var set = (DialogResponses.Flag)(on ? raw | (long)flag : raw & ~(long)flag);
                    target.Apply(T("Line flags"), copy => RecordFields.Set(copy, ["Flags", "Flags"], set));
                }
                if (target is null) ImGui.EndDisabled();
            }
            ImGui.EndTable();
        }

        ImGui.SeparatorText(TF($"Responses ({line.Responses.Count})"));
        for (var r = 0; r < line.Responses.Count; r++)
        {
            var response = line.Responses[r];
            ImGui.PushID(r);
            if (ImGui.BeginTable("##response", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersOuterH))
            {
                ImGui.TableSetupColumn("left", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 10);
                ImGui.TableSetupColumn("right", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, TF($"Response {r + 1}"));
                if (target is not null)
                {
                    ImGui.SameLine();
                    var at = r;
                    if (ImGui.SmallButton("x")) target.Apply(T("Remove response"), copy => RecordFields.RemoveItem(copy, ["Responses"], at));
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Remove this response"));
                }
                ImGui.SetNextItemWidth(-1);
                FieldEditors.Draw(app, target, T("Emotion"), ["Responses", r, "Emotion"], response.Emotion, typeof(Emotion), true);
                ImGui.SetNextItemWidth(-1);
                FieldEditors.Draw(app, target, T("Emotion value"), ["Responses", r, "EmotionValue"], response.EmotionValue, typeof(uint), true);
                ImGui.TableNextColumn();
                FieldEditors.Draw(app, target, T("Text"), ["Responses", r, "Text"], response.Text, typeof(Mutagen.Bethesda.Strings.TranslatedString), true);
                if (ImGui.TreeNode(T("Notes, sound, animations") + "##more"))
                {
                    FormView.DrawObject(app, response, ["ScriptNotes", "Edits", "Sound", "Flags", "SpeakerIdleAnimation", "ListenerIdleAnimation"], ["Responses", r], target);
                    ImGui.TreePop();
                }
                ImGui.EndTable();
            }
            ImGui.PopID();
        }
        if (target is not null && ImGui.SmallButton(T("+ response")))
        {
            var number = (byte)(line.Responses.Count + 1);
            target.Apply(T("Add response"), copy => ((DialogResponses)copy).Responses.Add(new DialogResponse { ResponseNumber = number, Emotion = Emotion.Neutral, EmotionValue = 50 }));
        }

        ImGui.SeparatorText(T("Line data"));
        FormView.DrawObject(app, line, ["Prompt", "Speaker", "Conditions", "LinkTo", "ResponseData", "WalkAwayTopic", "FavorLevel", "AudioOutputOverride", "PreviousDialog"], [], target);
        DrawFragments(app, line, target);
        ImGui.PopID();
    }

    // ------------------------------------------------------------------ new records

    internal static bool NeedsActive(EditorApp app)
    {
        if (app.ActivePlugin is not null) return false;
        Log.Warn(T("Choose the active file first: new dialogue goes into it"));
        return true;
    }

    internal static string FreeEditorId(EditorApp app, string stem)
    {
        var catalog = app.Session!.Catalog;
        for (var i = 1; ; i++)
        {
            var id = $"{stem}{i:00}";
            if (!catalog.ByKey.Values.Any(r => string.Equals(r.EditorId, id, StringComparison.OrdinalIgnoreCase))) return id;
        }
    }

    public static FormKey? NewBranch(EditorApp app, IQuestGetter quest)
    {
        if (NeedsActive(app)) return null;
        var active = app.ActivePlugin!;
        var release = app.Session!.Game.Release;
        var branchId = FreeEditorId(app, $"{quest.EditorID}Branch");
        var branch = new DialogBranch(active.Allocate(), release) { EditorID = branchId, Flags = DialogBranch.Flag.TopLevel, Category = DialogBranch.CategoryType.Player };
        branch.Quest.SetTo(quest.FormKey);
        var topic = new DialogTopic(active.Allocate(), release) { EditorID = branchId + "Topic", Priority = 50, Category = DialogTopic.CategoryEnum.Topic, Subtype = DialogTopic.SubtypeEnum.Custom };
        topic.Quest.SetTo(quest.FormKey);
        topic.Branch.SetTo(branch.FormKey);
        branch.StartingTopic.SetTo(topic.FormKey);
        app.Document.CommitEdits(TF($"New branch {branchId}"), [(branch, RecordPlace.TopLevel, null), (topic, RecordPlace.TopLevel, null)]);
        SelectedTopic[quest.FormKey] = topic.FormKey;
        return branch.FormKey;
    }

    public static FormKey? NewTopic(EditorApp app, IQuestGetter quest, FormKey? branch)
    {
        if (NeedsActive(app)) return null;
        var topic = new DialogTopic(app.ActivePlugin!.Allocate(), app.Session!.Game.Release)
        {
            EditorID = FreeEditorId(app, $"{quest.EditorID}Topic"),
            Priority = 50,
            Category = branch is null ? DialogTopic.CategoryEnum.Misc : DialogTopic.CategoryEnum.Topic,
            Subtype = DialogTopic.SubtypeEnum.Custom,
        };
        topic.Quest.SetTo(quest.FormKey);
        if (branch is { } b) topic.Branch.SetTo(b);
        app.Document.CommitEdit(TF($"New topic {topic.EditorID}"), topic, RecordPlace.TopLevel);
        SelectedTopic[quest.FormKey] = topic.FormKey;
        return topic.FormKey;
    }

    public static FormKey? NewLine(EditorApp app, IDialogTopicGetter topic, List<(IDialogResponsesGetter Line, string Plugin)> lines)
    {
        if (NeedsActive(app)) return null;
        var line = new DialogResponses(app.ActivePlugin!.Allocate(), app.Session!.Game.Release)
        {
            Flags = new DialogResponseFlags(),
        };
        line.Responses.Add(new DialogResponse { ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50, Text = "" });
        if (lines.Count > 0) line.PreviousDialog.SetTo(lines[^1].Line.FormKey);
        app.Document.CommitEdit(T("New line"), line, RecordPlace.DialogInfo, cell: topic.FormKey);
        SelectedLine[topic.FormKey] = line.FormKey;
        return line.FormKey;
    }
}
