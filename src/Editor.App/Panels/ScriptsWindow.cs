using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// File → Scripts (xEdit: Apply Script): C# scripts (.csx) in Documents\STB Editor\Scripts — list, edit, check, run.
/// A script reads the load order (<c>Records&lt;T&gt;()</c>, <c>Get</c>, <c>Find</c>, <c>Selected</c>), prints, and edits
/// records with <c>Edit&lt;T&gt;(record, change)</c>; its edits go into the active file as one undo step when it ends
/// without an error. Examples are written into the folder the first time.
/// </summary>
internal sealed class ScriptsWindow
{
    private bool _open;
    private string? _file;
    private string _code = "";
    private string _savedCode = "";
    private string _newName = "";
    private string _output = "";
    private Task<ScriptHost.Outcome>? _run;
    private CancellationTokenSource? _stop;
    private string? _runName;

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "STB Editor", "Scripts");
    public bool IsBusy => _run is not null;
    public string Output => _output;

    public void Show()
    {
        _open = true;
        Seed();
        if (_file is null && Files().FirstOrDefault() is { } first) Load(first);
    }

    private static IEnumerable<string> Files() => Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.csx").OrderBy(f => f, StringComparer.OrdinalIgnoreCase) : [];

    private void Load(string file)
    {
        _file = file;
        _code = _savedCode = File.ReadAllText(file);
    }

    /// <summary>Starts a script on a worker (autotests wait on <see cref="IsBusy"/>).</summary>
    public void Run(EditorApp app, string code, string name)
    {
        if (_run is not null || app.Session is not { } session) return;
        var selected = app.ObjectWindow.Picked is { Count: > 1 } picked ? picked : app.Selected is { } one ? [one] : new List<FormKey>();
        _stop = new CancellationTokenSource();
        _runName = name;
        _output = TF($"running {name}…");
        var document = app.Document;
        var token = _stop.Token;
        _run = Task.Run(() => ScriptHost.Run(code, session, document, selected, token));
    }

    /// <summary>Each frame: a finished run's output and its edits as one undo step.</summary>
    private void Finish(EditorApp app)
    {
        if (_run is not { IsCompleted: true } task) return;
        _run = null;
        var outcome = task.IsCompletedSuccessfully ? task.Result : new ScriptHost.Outcome("", [], task.Exception?.GetBaseException().Message, TimeSpan.Zero);
        var text = outcome.Output.TrimEnd();
        if (outcome.Error is { } error)
        {
            _output = (text.Length > 0 ? text + "\n\n" : "") + TF($"Error: {error}") + (outcome.Edits.Count > 0 ? "" : "");
            Log.Warn($"script {_runName}: {error.Split('\n')[0]}");
            return;
        }
        var edits = new List<(IMajorRecord, RecordPlace, RefContext?)>();
        var refused = 0;
        foreach (var record in outcome.Edits)
        {
            if (Validate(app, record) is { } problem) { refused++; Log.Warn($"script {_runName}: {record.EditorID} not changed — {problem}"); continue; }
            edits.Add((record, RecordPlace.TopLevel, null));
        }
        if (edits.Count > 0) app.Document.CommitEdits(TF($"Script {_runName}"), edits);
        _output = (text.Length > 0 ? text + "\n\n" : "")
            + TF($"Done in {outcome.Time.TotalSeconds:0.0} s: {edits.Count} record(s) changed") + (edits.Count > 0 ? TF($" (one Ctrl+Z: \"Script {_runName}\")") : "")
            + (refused > 0 ? TF($"; {refused} edit(s) refused: the record would not save correctly (see the log)") : "");
        Log.Info($"script {_runName}: {edits.Count} record(s) changed, {refused} refused, {outcome.Time.TotalMilliseconds:0} ms");
    }

    private static string? Validate(EditorApp app, IMajorRecord edited)
    {
        var session = app.Session!;
        var encoding = StbEditor.Data.Raw.StringEncodingDetector.WriteEncoding(app.ActivePlugin?.Encoding ?? session.Workspace.DefaultEncoding, session.Game.Language);
        return RecordRoundTrip.Read(edited, session.Game.Release, encoding) is null ? RecordRoundTrip.LastError ?? "unknown" : null;
    }

    public void Draw(EditorApp app)
    {
        Finish(app);
        if (!_open || app.Session is null) return;
        ImGui.SetNextWindowSize(app.Scaled(960, 640), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Scripts") + "###scripts", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            var listWidth = ImGui.GetFontSize() * 15;
            if (ImGui.BeginChild("##scriptlist", new Vector2(listWidth, 0), ImGuiChildFlags.Borders))
            {
                foreach (var file in Files())
                    if (ImGui.Selectable(Path.GetFileNameWithoutExtension(file) + "##" + file, file == _file)) Load(file);
                ImGui.Separator();
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##newscript", T("new script name"), ref _newName, 96);
                if (ImGui.SmallButton(T("New")) && _newName.Trim().Length > 0)
                {
                    var fileName = string.Concat(_newName.Trim().Split(Path.GetInvalidFileNameChars()));
                    var path = Path.Combine(Folder, fileName + ".csx");
                    if (!File.Exists(path)) File.WriteAllText(path, T("// Records<INpcGetter>(), Get(key), Find(\"EditorID\"), Selected, Edit<Npc>(record, n => ...), Print(...)") + "\n");
                    Load(path);
                    _newName = "";
                }
                ImGui.SameLine();
                if (ImGui.SmallButton(T("Open folder"))) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Folder) { UseShellExecute = true });
            }
            ImGui.EndChild();
            ImGui.SameLine();
            ImGui.BeginGroup();
            var name = _file is null ? T("(no script)") : Path.GetFileNameWithoutExtension(_file);
            ImGui.Text(name + (_code != _savedCode ? " *" : ""));
            ImGui.SameLine();
            ImGui.BeginDisabled(_file is null || _code == _savedCode);
            if (ImGui.SmallButton(T("Save"))) { File.WriteAllText(_file!, _code); _savedCode = _code; }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.SmallButton(T("Check"))) _output = ScriptHost.Check(_code) is { } errors ? errors : T("Compiles.");
            ImGui.SameLine();
            ImGui.BeginDisabled(_run is not null || _file is null);
            if (ImGui.SmallButton(T("Run"))) Run(app, _code, name);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(_run is null);
            if (ImGui.SmallButton(T("Stop"))) _stop?.Cancel();
            ImGui.EndDisabled();
            ImGui.SameLine();
            var picked = app.ObjectWindow.Picked.Count;
            ImGui.TextColored(Theme.TextDim, TF($"Selected: {(picked > 1 ? picked : app.Selected is null ? 0 : 1)}") + (app.ActivePlugin is { } active ? TF($"; edits go to {active.Name}") : T("; no active file: choose one before saving")));
            var avail = ImGui.GetContentRegionAvail();
            ImGui.InputTextMultiline("##code", ref _code, 1 << 20, new Vector2(avail.X, avail.Y * 0.62f), ImGuiInputTextFlags.AllowTabInput);
            ImGui.TextColored(Theme.TextDim, T("C# script: Records<IWeaponGetter>(), Get(key), Get<T>(key), Find(\"EditorID or FormID\"), Selected, Plugins, Edit<Weapon>(record, w => ...), Print(x), Name(record), Cancel. The last expression is printed."));
            if (ImGui.BeginChild("##scriptoutput", Vector2.Zero, ImGuiChildFlags.Borders))
                ImGui.TextUnformatted(_output);
            ImGui.EndChild();
            ImGui.EndGroup();
        }
        ImGui.End();
    }

    /// <summary>Example scripts, written once into an empty folder.</summary>
    private static void Seed()
    {
        if (Directory.Exists(Folder) && Directory.EnumerateFiles(Folder, "*.csx").Any()) return;
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "NPCs without a voice.csx"), """
            // NPCs that would say nothing: no voice type of their own and no template to take one from.
            var count = 0;
            foreach (var npc in Records<INpcGetter>())
            {
                if (npc.Voice.IsNull && npc.Template.IsNull) { Print(Name(npc)); count++; }
            }
            $"{count} NPC(s) without a voice"
            """);
        File.WriteAllText(Path.Combine(Folder, "Weapon damage x1.1 (selected).csx"), """
            // Raises the damage of the weapons picked in the Object Window by 10 % — one Ctrl+Z undoes it.
            var changed = 0;
            foreach (var key in Selected)
            {
                if (Get<IWeaponGetter>(key) is not { BasicStats: { } stats } weapon) continue;
                Edit<Weapon>(weapon, w => w.BasicStats!.Damage = (ushort)Math.Round(stats.Damage * 1.1));
                Print($"{Name(weapon)}: {stats.Damage} -> {Math.Round(stats.Damage * 1.1)}");
                changed++;
            }
            $"{changed} weapon(s) changed"
            """);
        File.WriteAllText(Path.Combine(Folder, "Prefix EditorIDs (selected).csx"), """
            // Puts a prefix in front of the EditorIDs of the selected records (that do not have it yet).
            const string prefix = "MyMod_";
            foreach (var key in Selected)
            {
                if (Get(key) is { EditorID: { } id } record && !id.StartsWith(prefix))
                    Edit<IMajorRecord>(record, r => r.EditorID = prefix + id);
            }
            """);
        File.WriteAllText(Path.Combine(Folder, "Count records by plugin.csx"), """
            // How many weapons, armours and NPCs each plugin made (the plugin a record comes from, not the one that wins).
            foreach (var group in Records<IWeaponGetter>().Cast<IMajorRecordGetter>().Concat(Records<IArmorGetter>()).Concat(Records<INpcGetter>())
                .GroupBy(r => r.FormKey.ModKey.FileName.String).OrderByDescending(g => g.Count()).Take(20))
                Print($"{group.Key}: {group.Count()}");
            """);
    }

    /// <summary>Autotests: a script run by text, as the Run button does.</summary>
    public void RunForTest(EditorApp app, string code, string name)
    {
        _open = true;
        Run(app, code, name);
    }
}
