using Hexa.NET.ImGui;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// File → Texts for translation: every text the player sees in a plugin exported to a JSON file (one entry per text
/// with an empty "translation"), and a filled file imported back into the active file — only where the text is still
/// the exported one unless asked otherwise; one undo step.
/// </summary>
internal sealed class TextsWindow
{
    private bool _open;
    private string _plugin = "";
    private string _exportPath = "";
    private string _importPath = "";
    private bool _force;
    private Task<string>? _export;
    private volatile string _progress = "";
    private string? _exportResult, _importResult;

    public string? LastExport => _exportResult;
    public string? LastImport => _importResult;
    public bool IsBusy => _export is not null;

    private static string DefaultPath(string plugin) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "STB Editor", "Texts", Path.GetFileNameWithoutExtension(plugin) + ".texts.json");

    public void Show(EditorApp app)
    {
        _open = true;
        if (_plugin.Length == 0 && app.Session is { } session)
            _plugin = app.ActivePlugin?.Name is { } active && session.Workspace.Active.Any(p => p.Name == active) ? active : "";
        if (_exportPath.Length == 0 && _plugin.Length > 0) _exportPath = _importPath = DefaultPath(_plugin);
    }

    /// <summary>Exports in the background (autotests wait on <see cref="IsBusy"/>).</summary>
    public void StartExport(EditorApp app, string plugin, string path)
    {
        if (_export is not null || app.Session is not { } session) return;
        var info = session.Workspace.Active.FirstOrDefault(p => p.Name.Equals(plugin, StringComparison.OrdinalIgnoreCase));
        if (info is null) { _exportResult = TF($"{plugin} is not in the load order"); return; }
        var progress = new Progress<string>(m => _progress = m);
        _export = Task.Run(() =>
        {
            var hits = TextSearch.DisplayTexts(info, session.Catalog, progress);
            var file = TextTranslation.FromHits(info.Name, hits);
            TextTranslation.Write(path, file);
            return TF($"{file.Strings.Count} texts written to {path}");
        });
    }

    /// <summary>Imports a filled file into the active file; returns the summary.</summary>
    public string Import(EditorApp app, string path, bool force)
    {
        if (app.ActivePlugin is null)
        {
            app.ShowActiveFileDialog(T("Imported texts go into the active file — choose it first."));
            return T("No active file.");
        }
        TextFile file;
        try { file = TextTranslation.Read(path); }
        catch (Exception ex) { return TF($"Cannot read {path}: {ex.Message}"); }
        var outcomes = new Dictionary<TextTranslation.Outcome, int>();
        var edits = new TextEdits(app, TF($"Import texts {Path.GetFileName(path)}"));
        var missing = 0;
        foreach (var group in TextTranslation.Translated(file).GroupBy(e => e.Form))
        {
            var first = group.First();
            if (edits.Current(first.Key, first.TopicKey) is null) { missing += group.Count(); continue; }
            edits.Edit(first.Key, first.TopicKey, copy =>
            {
                var changed = false;
                foreach (var entry in group)
                {
                    TextTranslation.Outcome outcome;
                    try { outcome = TextTranslation.Apply(copy, entry, force); }
                    catch (Exception ex) { Log.Warn($"import {entry.EditorId} {string.Join("/", entry.Path)}: {ex.Message}"); outcome = TextTranslation.Outcome.Missing; }
                    outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
                    changed |= outcome == TextTranslation.Outcome.Applied;
                }
                return changed;
            });
        }
        edits.Commit();
        var summary = TF($"{outcomes.GetValueOrDefault(TextTranslation.Outcome.Applied)} text(s) in {edits.Records} record(s) translated; already translated {outcomes.GetValueOrDefault(TextTranslation.Outcome.AlreadyTranslated)}; text differs from the export {outcomes.GetValueOrDefault(TextTranslation.Outcome.SourceChanged)}; not found {missing + outcomes.GetValueOrDefault(TextTranslation.Outcome.Missing)}");
        Log.Info($"texts import: {summary}");
        return summary;
    }

    public void Draw(EditorApp app)
    {
        if (_export is { IsCompleted: true } done)
        {
            _export = null;
            _exportResult = done.IsCompletedSuccessfully ? done.Result : TF($"Export failed: {done.Exception?.GetBaseException().Message}");
            Log.Info($"texts export: {_exportResult}");
        }
        if (!_open || app.Session is not { } session) return;
        ImGui.SetNextWindowSize(app.Scaled(680, 400), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Texts for translation") + "###texts", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.TextWrapped(T("Every text the player sees in a plugin — names, descriptions, books, dialogue, journal, messages — as a JSON file with an empty \"translation\" for each. Fill it in (by hand or with a tool) and import it back."));
            ImGui.SeparatorText(T("Export"));
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##plugin", _plugin.Length > 0 ? _plugin : T("(plugin)")))
            {
                foreach (var plugin in session.Workspace.Active)
                    if (ImGui.Selectable(plugin.Name, plugin.Name == _plugin))
                    {
                        _plugin = plugin.Name;
                        _exportPath = _importPath = DefaultPath(plugin.Name);
                    }
                ImGui.EndCombo();
            }
            ImGui.SetNextItemWidth(-ImGui.GetFontSize() * 7);
            ImGui.InputText("##exportpath", ref _exportPath, 1024);
            ImGui.SameLine();
            ImGui.BeginDisabled(_export is not null || _plugin.Length == 0);
            if (ImGui.Button(_export is not null ? T("exporting…") : T("Export"))) StartExport(app, _plugin, _exportPath);
            ImGui.EndDisabled();
            if (_export is not null) ImGui.TextColored(Theme.Warning, _progress);
            if (_exportResult is not null) ImGui.TextWrapped(_exportResult);

            ImGui.SeparatorText(T("Import"));
            ImGui.SetNextItemWidth(-ImGui.GetFontSize() * 7);
            ImGui.InputText("##importpath", ref _importPath, 1024);
            ImGui.SameLine();
            if (ImGui.Button(T("Import"))) _importResult = Import(app, _importPath, _force);
            ImGui.Checkbox(T("Also where the text is not the exported one (a later plugin changed it)"), ref _force);
            if (_importResult is not null) ImGui.TextWrapped(_importResult);
            ImGui.TextColored(Theme.TextDim, app.ActivePlugin is { } active
                ? TF($"Translations go into {active.Name} (overrides, or its own records); one Ctrl+Z undoes the import.")
                : T("Choose the active file first: translations go into it."));
        }
        ImGui.End();
    }
}
