using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// World → Filtered dialogue (CK: Character → Filtered Dialogue): every line an NPC could say — conditions on who it is, its
/// voice, race, sex, class and factions checked; lines a condition ties to the NPC first. A click opens the topic.
/// </summary>
internal sealed class FilteredDialogueWindow
{
    private bool _open;
    private string _npc = "";
    private bool _namedOnly = true;
    private string _filter = "";
    private Task<List<FilteredLine>>? _task;
    private volatile string _progress = "";
    private List<FilteredLine> _lines = [];
    private string? _result;

    public bool IsBusy => _task is not null;
    public IReadOnlyList<FilteredLine> Lines => _lines;

    public void Show(EditorApp app)
    {
        _open = true;
        if (_npc.Length == 0 && app.Selected is { } key && app.Session?.Catalog.Get(key)?.Signature == "NPC_") _npc = app.Session.Catalog.Get(key)!.EditorId ?? key.ToString();
    }

    public void Start(EditorApp app, string npcText)
    {
        if (_task is not null || app.Session is not { } session) return;
        var lookup = new RecordLookup(session.Workspace, session.Catalog);
        var key = session.Workspace.ParseFormReference(npcText.Trim())
            ?? session.Catalog.BySignature.GetValueOrDefault("NPC_")?.FirstOrDefault(r => string.Equals(r.EditorId, npcText.Trim(), StringComparison.OrdinalIgnoreCase))?.FormKey;
        if (key is not { } k || lookup.Resolve<INpcGetter>(k) is not { } npc) { _result = T("No such NPC."); return; }
        _npc = npc.EditorID ?? npcText;
        var actor = FilteredDialogue.Actor.Of(npc, lookup);
        var progress = new Progress<string>(m => _progress = m);
        _result = null;
        _task = Task.Run(async () =>
        {
            var index = await session.Dialogue;
            return FilteredDialogue.Lines(session.Workspace, lookup, index, actor, progress)
                .OrderByDescending(l => l.ById).ThenByDescending(l => l.Named).ThenBy(l => l.QuestEditorId, StringComparer.OrdinalIgnoreCase).ToList();
        });
    }

    public void Draw(EditorApp app)
    {
        if (_task is { IsCompleted: true } done)
        {
            _task = null;
            if (done.IsCompletedSuccessfully) { _lines = done.Result; _result = TF($"{_lines.Count} line(s), {_lines.Count(l => l.Named)} tied to the NPC by a condition"); }
            else _result = TF($"Failed: {done.Exception?.GetBaseException().Message}");
            Log.Info($"filtered dialogue {_npc}: {_result}");
        }
        if (!_open || app.Session is not { } session) return;
        ImGui.SetNextWindowSize(app.Scaled(900, 560), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Filtered dialogue") + "###filtereddialogue", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 14);
            ImGui.InputTextWithHint("##fdnpc", T("NPC: EditorID or FormID"), ref _npc, 128);
            ImGui.SameLine();
            if (ImGui.SmallButton(T("selected")) && app.Selected is { } sel) _npc = session.Catalog.Get(sel)?.EditorId ?? session.Workspace.FormIdText(sel);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("The record selected in the Object Window"));
            ImGui.SameLine();
            ImGui.BeginDisabled(_task is not null || _npc.Length == 0);
            if (ImGui.Button(_task is not null ? T("listing…") : T("List"))) Start(app, _npc);
            ImGui.EndDisabled();
            if (_task is not null) { ImGui.SameLine(); ImGui.TextColored(Theme.Warning, _progress); }
            ImGui.SameLine();
            ImGui.Checkbox(T("Only lines a condition ties to the NPC"), ref _namedOnly);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12);
            ImGui.InputTextWithHint("##fdfilter", T("Filter text"), ref _filter, 128);
            if (_result is not null) ImGui.TextWrapped(_result);
            ImGui.TextColored(Theme.TextDim, T("Conditions on the NPC's ID, voice, race, sex, class and factions are checked; the rest (quest stages, globals, places…) count as passing, as the CK does."));

            var shown = _lines.Where(l => (!_namedOnly || l.Named) && (_filter.Length == 0 || l.Text.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || (l.TopicEditorId?.Contains(_filter, StringComparison.OrdinalIgnoreCase) ?? false))).ToList();
            if (ImGui.BeginTable("##fdlines", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn(T("Quest / topic"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 16);
                ImGui.TableSetupColumn("FormID", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 5);
                ImGui.TableSetupColumn(T("Text"), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();
                var clipper = new ImGuiListClipper();
                clipper.Begin(shown.Count);
                while (clipper.Step())
                    for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        var l = shown[i];
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        if (ImGui.Selectable($"{l.QuestEditorId} / {l.TopicEditorId}##fd{i}", false, ImGuiSelectableFlags.SpanAllColumns)) app.FormWindows.Open(l.Topic);
                        ImGui.TableNextColumn();
                        ImGui.TextColored(Theme.TextDim, session.Workspace.FormIdText(l.Info));
                        ImGui.TableNextColumn();
                        if (l.Named) ImGui.TextUnformatted(l.Text.Length > 0 ? l.Text : T("(no text)"));
                        else ImGui.TextColored(Theme.TextDim, l.Text.Length > 0 ? l.Text : T("(no text)"));
                    }
                clipper.End();
                ImGui.EndTable();
            }
        }
        ImGui.End();
    }
}
