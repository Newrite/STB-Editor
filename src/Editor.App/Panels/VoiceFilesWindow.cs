using System.Numerics;
using System.Text;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// World → Voice files (CK: the Voice Assets lists of quests, Character → Export Dialogue, Generate LIP): every spoken response
/// of a plugin's dialogue lines for each voice type that says it, with the file the game looks for and what the build has of
/// it (.fuz, .xwm / .wav with or without a .lip). Exports the list as tab-separated text for voice actors; makes the missing
/// .lip files from WAVs with the CK's LipGenerator and packs .fuz files, next to the WAVs.
/// </summary>
internal sealed class VoiceFilesWindow
{
    public enum State { Fuz, AudioAndLip, WavNoLip, XwmNoLip, Missing, NoVoice }

    public sealed record Row(VoiceLine Line, State State, string? Audio, string? Lip);

    private bool _open;
    private string _plugin = "";
    private string _quest = "";
    private int _show;
    private Task<List<Row>>? _listing;
    private List<Row> _rows = [];
    private Task<string>? _working;
    private volatile string _progress = "";
    private string? _result;
    private string _exportPath = "";
    private bool _useText = true;

    public IReadOnlyList<Row> Rows => _rows;
    public bool IsBusy => _listing is not null || _working is not null;
    public string? Result => _result;

    private static string DefaultExport(string plugin) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "STB Editor", "Voice", Path.GetFileNameWithoutExtension(plugin) + ".voice.txt");

    public void Show(EditorApp app)
    {
        _open = true;
        if (_plugin.Length == 0) _plugin = app.ActivePlugin?.Name ?? "";
        if (_exportPath.Length == 0 && _plugin.Length > 0) _exportPath = DefaultExport(_plugin);
    }

    /// <summary>Lists the plugin's lines in the background (all quests; lines of other plugins' topics count when the line is the plugin's).</summary>
    public void List(EditorApp app, string plugin, string questFilter)
    {
        if (_listing is not null || app.Session is not { } session || plugin.Length == 0) return;
        var assets = session.Assets;
        _result = null;
        _listing = Task.Run(async () =>
        {
            _progress = T("indexing dialogue…");
            var index = await session.Dialogue;
            var lookup = new RecordLookup(session.Workspace, session.Catalog);
            var quests = (session.Catalog.BySignature.GetValueOrDefault("QUST") ?? [])
                .Where(q => questFilter.Length == 0 || (q.EditorId?.Contains(questFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                .Select(q => q.FormKey).ToList();
            _progress = TF($"reading the lines of {quests.Count} quest(s)…");
            var lines = VoiceFiles.Lines(session.Workspace, lookup, index, quests,
                line => line.FormKey.ModKey.FileName.String.Equals(plugin, StringComparison.OrdinalIgnoreCase));
            return lines.Select(l => Status(assets, l)).ToList();
        });
    }

    public static Row Status(StbEditor.Assets.AssetFileSystem assets, VoiceLine line)
    {
        if (line.PathStem is not { } stem) return new Row(line, State.NoVoice, null, null);
        if (assets.Exists(stem + ".fuz")) return new Row(line, State.Fuz, stem + ".fuz", null);
        var lip = assets.Exists(stem + ".lip") ? stem + ".lip" : null;
        foreach (var ext in new[] { ".xwm", ".wav" })
            if (assets.Exists(stem + ext))
                return new Row(line, lip is not null ? State.AudioAndLip : ext == ".wav" ? State.WavNoLip : State.XwmNoLip, stem + ext, lip);
        return new Row(line, State.Missing, null, null);
    }

    public static string StateText(State state) => state switch
    {
        State.Fuz => T("fuz"),
        State.AudioAndLip => T("audio + lip"),
        State.WavNoLip => T("wav, no lip"),
        State.XwmNoLip => T("xwm, no lip"),
        State.Missing => T("missing"),
        _ => T("no voice type"),
    };

    /// <summary>Tab-separated list as the CK's dialogue export has it, one row per response and voice type.</summary>
    public static string Export(IEnumerable<Row> rows)
    {
        var text = new StringBuilder("FILE\tVOICE TYPE\tQUEST\tTOPIC\tFORMID\tRESPONSE\tEMOTION\tTEXT\tSCRIPT NOTES\tSTATE\r\n");
        static string Clean(string s) => s.Replace('\t', ' ').Replace("\r", " ").Replace("\n", " ");
        foreach (var row in rows)
        {
            var l = row.Line;
            text.Append(l.PathStem is { } stem ? stem + ".wav" : "").Append('\t').Append(l.VoiceTypeEditorId).Append('\t').Append(l.QuestEditorId).Append('\t')
                .Append(l.TopicEditorId).Append('\t').Append(l.Info.ID.ToString("X8")).Append('\t').Append(l.Response).Append('\t').Append(l.Emotion).Append('\t')
                .Append(Clean(l.Text)).Append('\t').Append(Clean(l.ScriptNotes)).Append('\t').Append(row.State).Append("\r\n");
        }
        return text.ToString();
    }

    /// <summary>
    /// Makes a .lip for every loose WAV without one (LipGenerator, the response's text as the hint) and, with
    /// <paramref name="fuz"/>, packs .fuz files from loose audio + lip. Writes next to the audio file it found.
    /// </summary>
    public void Process(EditorApp app, bool lips, bool fuz)
    {
        if (_working is not null || app.Session is not { } session) return;
        var tools = new VoiceTools(session.Game.GameFolder);
        var rows = _rows.ToList();
        var assets = session.Assets;
        var useText = _useText;
        _working = Task.Run(() =>
        {
            var work = Path.Combine(Path.GetTempPath(), "STBEditor", "voice");
            int made = 0, packed = 0, failed = 0, archived = 0;
            var done = 0;
            foreach (var row in rows)
            {
                done++;
                _progress = TF($"{done} / {rows.Count}");
                if (row.Audio is null || row.State is State.Fuz or State.Missing or State.NoVoice) continue;
                if (assets.Find(row.Audio)?.LoosePath is not { } audio) { archived++; continue; }
                var lip = row.Lip is not null ? assets.Find(row.Lip)?.LoosePath : null;
                if (lip is null && lips && row.State == State.WavNoLip)
                {
                    var target = Path.ChangeExtension(audio, ".lip");
                    if (tools.MakeLip(audio, useText && Latin(row.Line.Text) ? row.Line.Text : "", target, work) is { } problem) { failed++; Log.Warn($"lip {row.Audio}: {problem}"); continue; }
                    assets.AddLoose(row.Line.PathStem + ".lip", target, assets.Find(row.Audio)?.Container ?? "");
                    lip = target;
                    made++;
                }
                if (fuz && lip is not null)
                {
                    var target = Path.ChangeExtension(audio, ".fuz");
                    if (tools.MakeFuz(lip, audio, target, work) is { } problem) { failed++; Log.Warn($"fuz {row.Audio}: {problem}"); continue; }
                    assets.AddLoose(row.Line.PathStem + ".fuz", target, assets.Find(row.Audio)?.Container ?? "");
                    packed++;
                }
            }
            return TF($"{made} .lip made, {packed} .fuz packed, {failed} failed") + (archived > 0 ? TF($"; {archived} in archives skipped") : "");
        });
    }

    /// <summary>LipGenerator knows English phonemes only: other scripts are no help as a hint.</summary>
    private static bool Latin(string text) => text.Count(char.IsLetter) is var letters && letters > 0 && text.Count(c => c < 128 && char.IsLetter(c)) * 10 >= letters * 9;

    public void Draw(EditorApp app)
    {
        if (_listing is { IsCompleted: true } listed)
        {
            _listing = null;
            if (listed.IsCompletedSuccessfully) { _rows = listed.Result; Log.Info($"voice files: {_rows.Count} rows for {_plugin}; {Summary()}"); }
            else { _result = TF($"Listing failed: {listed.Exception?.GetBaseException().Message}"); Log.Error(_result); }
        }
        if (_working is { IsCompleted: true } worked)
        {
            _working = null;
            _result = worked.IsCompletedSuccessfully ? worked.Result : TF($"Failed: {worked.Exception?.GetBaseException().Message}");
            Log.Info($"voice files: {_result}");
            if (app.Session is { } s) _rows = _rows.Select(r => Status(s.Assets, r.Line)).ToList();
        }
        if (!_open || app.Session is not { } session) return;
        if (_exportPath.Length == 0 && _plugin.Length > 0) _exportPath = DefaultExport(_plugin);
        ImGui.SetNextWindowSize(app.Scaled(980, 620), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Voice files") + "###voicefiles", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 16);
            if (ImGui.BeginCombo("##voiceplugin", _plugin.Length > 0 ? _plugin : T("(plugin)")))
            {
                foreach (var plugin in session.Workspace.Active)
                    if (ImGui.Selectable(plugin.Name, plugin.Name == _plugin)) { _plugin = plugin.Name; _exportPath = DefaultExport(plugin.Name); }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
            ImGui.InputTextWithHint("##voicequest", T("quest filter"), ref _quest, 128);
            ImGui.SameLine();
            ImGui.BeginDisabled(IsBusy || _plugin.Length == 0);
            if (ImGui.Button(_listing is not null ? T("listing…") : T("List"))) List(app, _plugin, _quest);
            ImGui.EndDisabled();
            if (_listing is not null || _working is not null) { ImGui.SameLine(); ImGui.TextColored(Theme.Warning, _progress); }
            ImGui.TextColored(Theme.TextDim, T("Each response of the plugin's lines for every voice type that says it (from the speaker, the conditions, the quest's aliases and scenes). Files go to Sound\\Voice\\<plugin>\\<voice type>\\."));
            if (_rows.Count > 0) ImGui.Text(Summary());

            // Actions.
            var tools = new VoiceTools(session.Game.GameFolder);
            ImGui.SetNextItemWidth(-ImGui.GetFontSize() * 9);
            ImGui.InputText("##voiceexport", ref _exportPath, 1024);
            ImGui.SameLine();
            ImGui.BeginDisabled(_rows.Count == 0);
            if (ImGui.Button(T("Export list"))) _result = ExportTo(_exportPath);
            ImGui.EndDisabled();
            ImGui.BeginDisabled(_rows.Count == 0 || _working is not null || !tools.CanMakeLip);
            if (ImGui.Button(T("Make missing .lip"))) Process(app, true, false);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(tools.CanMakeLip ? T("For every loose WAV without a .lip: the CK's LipGenerator, written next to the WAV") : TF($"Needs the CK's LipGenerator: {tools.LipGenerator}"));
            ImGui.SameLine();
            ImGui.BeginDisabled(_rows.Count == 0 || _working is not null || !tools.CanEncode);
            if (ImGui.Button(T("Make .lip and pack .fuz"))) Process(app, tools.CanMakeLip, true);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(tools.CanEncode ? T("Also packs audio + lip into the .fuz the game reads (WAVs encoded to xWMA first)") : TF($"Needs xWMAEncode: {tools.XwmaEncode}"));
            ImGui.SameLine();
            ImGui.Checkbox(T("Text guides the lips"), ref _useText);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("LipGenerator takes the spoken text as a hint (English phonemes; text in other scripts is not passed); off — from the sound only"));
            if (_result is not null) ImGui.TextWrapped(_result);

            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12);
            string[] shows = [T("All rows"), T("Missing audio"), T("Missing .lip"), T("No voice type")];
            ImGui.Combo("##voiceshow", ref _show, shows, shows.Length);
            DrawTable(app, session);
        }
        ImGui.End();
    }

    private string Summary() => string.Join(", ", _rows.GroupBy(r => r.State).OrderBy(g => g.Key).Select(g => $"{StateText(g.Key)} {g.Count()}"));

    private string ExportTo(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Export(_rows), new UTF8Encoding(true));
            return TF($"{_rows.Count} rows written to {path}");
        }
        catch (Exception ex) { return TF($"Cannot write {path}: {ex.Message}"); }
    }

    private void DrawTable(EditorApp app, EditorSession session)
    {
        var shown = _rows.Where(r => _show switch
        {
            1 => r.State == State.Missing,
            2 => r.State is State.WavNoLip or State.XwmNoLip,
            3 => r.State == State.NoVoice,
            _ => true,
        }).ToList();
        if (!ImGui.BeginTable("##voicerows", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV, new Vector2(0, 0))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("State"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 6);
        ImGui.TableSetupColumn(T("Voice type"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 9);
        ImGui.TableSetupColumn(T("Quest / topic"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 12);
        ImGui.TableSetupColumn("FormID", ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 5);
        ImGui.TableSetupColumn(T("Text"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(T("File"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();
        var clipper = new ImGuiListClipper();
        clipper.Begin(shown.Count);
        while (clipper.Step())
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var row = shown[i];
                var l = row.Line;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var colour = row.State switch { State.Fuz or State.AudioAndLip => Theme.Good, State.Missing or State.NoVoice => Theme.Error, _ => Theme.Warning };
                ImGui.TextColored(colour, StateText(row.State));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(l.VoiceTypeEditorId ?? "—");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{l.QuestEditorId} / {l.TopicEditorId}");
                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{session.Workspace.FormIdText(l.Info)}  {l.Response}##voice{i}")) app.FormWindows.Open(l.Topic);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Open the topic"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(l.Text);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Audio ?? (l.PathStem is { } stem ? stem + ".wav" : ""));
            }
        clipper.End();
        ImGui.EndTable();
    }

    /// <summary>Autotests: rows listed for a plugin and quest filter, waiting on <see cref="IsBusy"/>.</summary>
    public void ListForTest(EditorApp app, string plugin, string quest)
    {
        _plugin = plugin;
        _quest = quest;
        List(app, plugin, quest);
    }

    /// <summary>Autotests: the rows become just this one.</summary>
    public void ReplaceForTest(Row row) => _rows = [row];
}
