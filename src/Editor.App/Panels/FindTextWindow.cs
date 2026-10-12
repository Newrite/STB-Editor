using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// Edit → Find text (CK: Find Text; xEdit: Search and Replace): in every text field of one plugin's records
/// (names, descriptions, dialogue, log entries, books…) or by EditorID and name over the whole load order;
/// double-click opens a record; Replace changes the found texts in the records as they are now (one undo step).
/// </summary>
internal sealed class FindTextWindow
{
    private bool _open;
    private string _query = string.Empty;
    private string _replacement = string.Empty;
    private bool _matchCase;
    private bool _editorIds;
    private string? _scope; // null: the whole load order by EditorID and name
    private Task<List<TextHit>>? _task;
    private List<TextHit>? _hits;
    private volatile string _progress = "";

    public bool IsBusy => _task is not null;
    public IReadOnlyList<TextHit>? Hits => _hits;

    public void Show() => _open = true;

    public void Start(EditorApp app, string query, string? plugin, bool matchCase)
    {
        if (app.Session is not { } session || _task is not null || query.Length == 0) return;
        _query = query;
        _scope = plugin;
        _matchCase = matchCase;
        var progress = new Progress<string>(m => _progress = m);
        var target = plugin is null ? null : session.Workspace.Active.FirstOrDefault(p => p.Name == plugin);
        _task = Task.Run(() => target is null
            ? TextSearch.InCatalog(session.Catalog, query, matchCase)
            : TextSearch.InPlugin(target, session.Catalog, query, matchCase, progress));
    }

    public void Draw(EditorApp app)
    {
        if (_task is { IsCompleted: true } done)
        {
            _task = null;
            _hits = done.IsCompletedSuccessfully ? done.Result : [];
            if (!done.IsCompletedSuccessfully) Log.Error($"find text: {done.Exception?.GetBaseException().Message}");
        }
        if (!_open || app.Session is not { } session) return;
        ImGui.SetNextWindowSize(app.Scaled(900, 560), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Find text") + "###findtext", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 18);
            var enter = ImGui.InputTextWithHint("##query", T("Text to find"), ref _query, 256, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 16);
            if (ImGui.BeginCombo("##scope", _scope ?? T("All plugins: EditorID and name")))
            {
                if (ImGui.Selectable(T("All plugins: EditorID and name"), _scope is null)) _scope = null;
                foreach (var plugin in session.Workspace.Active.Where(p => p.IsLoaded))
                    if (ImGui.Selectable(plugin.Name, plugin.Name == _scope)) _scope = plugin.Name;
                ImGui.EndCombo();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("A plugin: every text field of its records (dialogue, descriptions, books…); all plugins: EditorIDs and names only (instant)"));
            ImGui.SameLine();
            ImGui.Checkbox(T("Match case"), ref _matchCase);
            ImGui.SameLine();
            ImGui.BeginDisabled(_task is not null || _query.Length == 0);
            if (ImGui.Button(_task is not null ? T("searching…") : T("Find")) || (enter && _task is null && _query.Length > 0)) Start(app, _query, _scope, _matchCase);
            ImGui.EndDisabled();
            if (_task is not null) { ImGui.SameLine(); ImGui.TextColored(Theme.Warning, _progress); }

            if (_hits is { } hits)
            {
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 18);
                ImGui.InputTextWithHint("##replace", T("Replace with"), ref _replacement, 256);
                ImGui.SameLine();
                var editable = hits.Where(h => Replaceable(h, _editorIds)).ToList();
                ImGui.BeginDisabled(editable.Count == 0 || _task is not null);
                if (ImGui.Button(TF($"Replace in {editable.Count} result(s)"))) ReplaceAll(app, editable, _query, _replacement, _matchCase, _editorIds);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Text the player sees (names, dialogue, descriptions, books…) in the records as they are now; saved into the active file (Ctrl+S). Script variables, file paths and other technical strings are never replaced; references, cells and navmeshes are not changed here."));
                ImGui.SameLine();
                ImGui.Checkbox(T("also EditorIDs"), ref _editorIds);
                ImGui.SameLine();
                ImGui.TextColored(Theme.TextDim, TF($"{hits.Count} result(s)"));
                DrawHits(app, session, hits);
            }
        }
        ImGui.End();
    }

    private static bool Replaceable(TextHit hit, bool editorIds) =>
        hit.Signature is not ("REFR" or "ACHR" or "CELL" or "WRLD" or "NAVM" or "LAND" or "?") && (hit.Display || (editorIds && hit.IsEditorId));

    private static void DrawHits(EditorApp app, EditorSession session, List<TextHit> hits)
    {
        if (hits.Count == 0) { ImGui.TextColored(Theme.TextDim, T("Nothing found.")); return; }
        if (!ImGui.BeginTable("##hits", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("Type"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 3);
        ImGui.TableSetupColumn(T("Editor ID"), ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(T("Field"), ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn(T("Text"), ImGuiTableColumnFlags.WidthStretch, 2.4f);
        ImGui.TableHeadersRow();
        var clipper = new ImGuiListClipper();
        clipper.Begin(hits.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var hit = hits[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, hit.Signature);
                ImGui.TableNextColumn();
                ImGui.PushID(i);
                var open = hit.Topic ?? hit.Key;
                if (ImGui.Selectable(hit.EditorId ?? session.Workspace.FormIdText(hit.Key), false, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick)
                    && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && session.Catalog.Get(open) is not null)
                {
                    app.Select(open);
                    app.FormWindows.Open(open);
                }
                ImGui.PopID();
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, hit.PathText);
                ImGui.TableNextColumn();
                var text = hit.Text.ReplaceLineEndings(" ");
                ImGui.Text(text.Length > 160 ? text[..160] + "…" : text);
            }
        }
        clipper.End();
        ImGui.EndTable();
    }

    /// <summary>The found texts replaced in the records as they are now: one undo step; returns how many records changed.</summary>
    public static int ReplaceAll(EditorApp app, IReadOnlyList<TextHit> hits, string query, string replacement, bool matchCase, bool editorIds = false)
    {
        if (app.Session is null || query.Length == 0) return 0;
        var edits = new TextEdits(app, TF($"Replace \"{query}\" with \"{replacement}\""));
        foreach (var group in hits.Where(h => Replaceable(h, editorIds)).GroupBy(h => h.Key))
        {
            var first = group.First();
            edits.Edit(first.Key, first.Topic, copy =>
            {
                var changed = false;
                foreach (var hit in group)
                {
                    object? value;
                    try { value = RecordFields.Get(copy, hit.Path); } catch { continue; }
                    var text = value switch { string s => s, Mutagen.Bethesda.Strings.ITranslatedStringGetter t => t.String, _ => null };
                    if (text is null || !text.Contains(query, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        RecordFields.Set(copy, hit.Path, TextSearch.Replace(text, query, replacement, matchCase));
                        changed = true;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"replace {hit.EditorId} {hit.PathText}: {ex.Message}");
                    }
                }
                return changed;
            });
        }
        edits.Commit();
        Log.Info(TF($"replaced in {edits.Records} record(s)"));
        return edits.Records;
    }
}
