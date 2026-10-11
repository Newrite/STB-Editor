using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using StbEditor.App.Graphics;
using StbEditor.App.Panels;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Render;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App;

/// <summary>Top-level editor state: the loaded session, panels, selection and the main menu.</summary>
internal sealed class EditorApp : IDisposable
{
    private readonly Settings _settings;
    private readonly ImGuiLayer _imgui;
    private readonly GraphicsDevice _graphics;
    private readonly CommandLine _options;
    private readonly List<Panel> _panels;
    private readonly DataDialog _dataDialog;
    private readonly ActiveFileDialog _activeDialog;
    private RenderWindow.ViewState? _restoreView;
    private readonly List<FormKey> _history = [];
    private int _historyIndex = -1;
    private int _framesSinceLoad;
    private bool _layoutBuilt;
    private bool _gizmoReady;
    private bool _pendingCommandLineSelection;

    public SessionLoader? Loader { get; private set; }
    public EditorSession? Session { get; private set; }
    public Settings Settings => _settings;
    public ImGuiLayer Ui => _imgui;

    /// <summary>Record shown in the Inspector.</summary>
    public FormKey? Selected { get; private set; }

    /// <summary>Bumped on every selection change so panels can scroll to it once.</summary>
    public int SelectionVersion { get; private set; }

    public bool IsBusy => Loader?.IsRunning == true || RenderWindow.NeedsRedraw || PreviewPanel.NeedsRedraw
        || Thumbnails?.HasPending == true || Saver.IsBusy;
    public uint IdleWaitMilliseconds => 500;

    public GraphicsDevice Graphics => _graphics;

    /// <summary>Unsaved edits of the session (placements, deletes) with undo/redo.</summary>
    public StbEditor.Core.EditDocument Document { get; private set; } = new();
    public RenderWindow RenderWindow { get; }
    public PreviewPanel PreviewPanel { get; }

    /// <summary>Single-object renderer for the Preview panel and thumbnails (per session).</summary>
    public ObjectPreview? Preview { get; private set; }
    public ThumbnailCache? Thumbnails { get; private set; }

    /// <summary>Where Save writes (the CK's active file); null until chosen.</summary>
    public StbEditor.Core.ActivePlugin? ActivePlugin { get; private set; }

    public SaveController Saver { get; } = new();
    public ObjectWindow ObjectWindow { get; }
    public InspectorPanel Inspector { get; }

    /// <summary>Records open in their own form windows.</summary>
    public FormWindows FormWindows { get; } = new();

    public EditorApp(Settings settings, ImGuiLayer imgui, GraphicsDevice graphics, CommandLine options)
    {
        _settings = settings;
        _imgui = imgui;
        _graphics = graphics;
        _options = options;

        RenderWindow = new RenderWindow();
        PreviewPanel = new PreviewPanel();
        ObjectWindow = new ObjectWindow();
        Inspector = new InspectorPanel();
        _panels =
        [
            RenderWindow,
            ObjectWindow,
            new CellViewPanel(),
            Inspector,
            PreviewPanel,
            new LandscapePanel(),
            new NavmeshPanel(),
            new LoadOrderPanel(),
            new LogPanel(),
        ];
        foreach (var panel in _panels) panel.IsOpen = !settings.ClosedPanels.Contains(panel.Name);
        Loc.Russian = settings.Language == "ru";
        // --lang en|ru: this run only (screenshots); --show-help opens the keys window.
        var cliArgs0 = Environment.GetCommandLineArgs();
        var lang = Array.IndexOf(cliArgs0, "--lang");
        if (lang >= 0 && lang + 1 < cliArgs0.Length) Loc.Russian = cliArgs0[lang + 1] == "ru";
        if (cliArgs0.Contains("--show-help")) _help.Show();
        _openForm = cliArgs0.SkipWhile(x => x != "--open-form").Skip(1).FirstOrDefault();

        if (options.Game is not null) settings.GameFolder = options.Game;
        if (options.Mo2 is not null) settings.Mo2Folder = options.Mo2;
        if (options.Profile is not null) settings.Mo2Profile = options.Profile;

        _dataDialog = new DataDialog(this);
        _activeDialog = new ActiveFileDialog(this);
        _newRecordDialog = new NewRecordDialog(this);
        _pendingCommandLineSelection = options.Select is not null || options.Search is not null || options.Cell is not null
            || Environment.GetCommandLineArgs().Contains("--object-shot") || Environment.GetCommandLineArgs().Contains("--psys")
            || Environment.GetCommandLineArgs().Contains("--dialog");

        if (options.Open || (settings.OpenOnStartup && settings.GameFolder is not null && options.Screenshot is null))
            _dataDialog.OpenImmediately();
        else
            _dataDialog.Show();
    }

    public void StartLoading(GameInstall game)
    {
        CloseSession();
        Selected = null;
        _history.Clear();
        _historyIndex = -1;
        Loader = new SessionLoader(game, ExtraPluginPath(), _settings.LoadOnly);
    }

    /// <summary>The active file goes into the load order even when MO2 does not list it yet.</summary>
    private string? ExtraPluginPath()
    {
        var path = ActivePlugin?.Path ?? _settings.ActivePluginPath;
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>Releases the workspace (and with it every open plugin file).</summary>
    public void CloseSession()
    {
        Session?.Dispose();
        Session = null;
        FormWindows.CloseAll();
        RenderWindow.DropScene();
    }

    public void SetActivePlugin(StbEditor.Core.ActivePlugin? active)
    {
        ActivePlugin = active;
        _settings.ActivePluginPath = active?.Path;
        _settings.Save();
        Log.Info(active is null ? "no active file" : $"active file: {active.Path}{(active.IsNew ? " (new, created on save)" : "")}, next FormID {active.NextObjectId:X6}");
    }

    /// <summary>Ctrl+S: into the active file, or ask for one first (as the CK does).</summary>
    public void Save()
    {
        if (Session is null || Saver.IsBusy) return;
        if (ActivePlugin is null)
        {
            _activeDialog.Show(saveAfter: true, T("Choose where to save."));
            return;
        }
        if (Document.PendingCount == 0)
        {
            Log.Info("nothing to save");
            return;
        }
        Saver.Start(this, ActivePlugin);
    }

    public void ShowActiveFileDialog(string reason = "") => _activeDialog.Show(saveAfter: false, reason);

    /// <summary>After a save: reload everything (the plugin changed on disk) and come back to the same view.</summary>
    public void AfterSave(GameInstall game, RenderWindow.ViewState? view, string savedPath)
    {
        _restoreView = view;
        _settings.ActivePluginPath = savedPath;
        _settings.Save();
        Document = new StbEditor.Core.EditDocument();
        Selected = null;
        Loader = new SessionLoader(game, File.Exists(savedPath) ? savedPath : null, _settings.LoadOnly);
    }

    /// <summary>New base records (duplicates) of the session appear in the catalog, and go again on undo.</summary>
    private void SyncTransientRecords()
    {
        if (Session is not { } session) return;
        var catalog = session.Catalog;
        var drafts = Document.Drafts.Values.OfType<StbEditor.Core.OverrideDraft>()
            .Where(d => d.Place == StbEditor.Core.RecordPlace.TopLevel && (catalog.Get(d.Key) is null or { IsTransient: true }))
            .ToDictionary(d => d.Key);

        foreach (var stale in catalog.Transient.Where(r => !drafts.ContainsKey(r.FormKey)).ToList()) catalog.RemoveTransient(stale.FormKey);
        if (ActivePlugin is not { } active) return;
        var owner = session.Workspace.Plugins.FirstOrDefault(p => p.ModKey == active.ModKey)
            ?? new StbEditor.Data.PluginInfo { ModKey = active.ModKey, Location = new StbEditor.Data.PluginLocation(active.Name, active.Path, "new", Path.GetDirectoryName(active.Path) ?? "", true) };
        foreach (var draft in drafts.Values)
        {
            var record = draft.Record;
            var name = (record as Mutagen.Bethesda.Plugins.Aspects.INamedGetter)?.Name;
            var existing = catalog.Get(draft.Key);
            if (existing is not null && existing.EditorId == record.EditorID && existing.Name == name) continue;
            catalog.AddTransient(draft.Key, StbEditor.Data.Catalog.RecordCatalog.SignatureOf(record), record.EditorID, name, owner);
        }
    }

    public bool? PollClose() => Session is null ? true : _activeDialog.PollClose();

    public void Select(FormKey? key, bool addToHistory = true)
    {
        if (key == Selected) return;
        Selected = key;
        SelectionVersion++;

        if (key is { } k && addToHistory)
        {
            if (_historyIndex < _history.Count - 1) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            _history.Add(k);
            if (_history.Count > 200) _history.RemoveAt(0);
            _historyIndex = _history.Count - 1;
        }
    }

    public void Back()
    {
        if (_historyIndex <= 0) return;
        _historyIndex--;
        Select(_history[_historyIndex], addToHistory: false);
    }

    public void Forward()
    {
        if (_historyIndex >= _history.Count - 1) return;
        _historyIndex++;
        Select(_history[_historyIndex], addToHistory: false);
    }

    public void Frame(float delta)
    {
        if (!_gizmoReady)
        {
            Hexa.NET.ImGuizmo.ImGuizmo.SetImGuiContext(ImGui.GetCurrentContext());
            _gizmoReady = true;
        }
        Hexa.NET.ImGuizmo.ImGuizmo.BeginFrame();

        PollLoader();
        Saver.Poll(this);
        RunAutotest();
        RunNewRecordsAutotest();
        RunLandAutotest();
        RunLandViewAutotest();
        RunDuplicateAutotest();
        RunNavmeshAutotest();
        RunFlyAutotest();
        RunFormAutotest();
        RunPickerAutotest();
        if (!_zoomDone && Session is not null && Environment.GetCommandLineArgs().Contains("--autotest-zoom")) _zoomDone = RenderWindow.ZoomTestStep();
        if (!_doorDone && Session is not null && Environment.GetCommandLineArgs().Contains("--autotest-door")) _doorDone = RenderWindow.DoorTestStep(this);
        if (!_gizmoDone && Session is not null && Environment.GetCommandLineArgs().Contains("--autotest-gizmo")) _gizmoDone = RenderWindow.GizmoTestStep();

        DrawMainMenu();
        StatusBar.Draw(this);

        var dockId = ImGui.DockSpaceOverViewport(0, ImGui.GetMainViewport(), ImGuiDockNodeFlags.PassthruCentralNode);
        if (!_layoutBuilt)
        {
            _layoutBuilt = true;
            DefaultLayout.BuildIfMissing(dockId, AppPaths.ImGuiIni);
        }

        foreach (var panel in _panels)
        {
            if (!panel.IsOpen) continue;
            panel.Draw(this);
        }
        RememberClosedPanels();

        if (Session is not null) Thumbnails?.Update(Session);
        _objectShot?.Step(this);

        _dataDialog.Draw();
        _activeDialog.Draw();
        // --open-form <FormID>: that record's form window once the data is loaded (screenshots).
        if (_openForm is not null && Session is { } loaded && loaded.Workspace.ParseFormReference(_openForm) is { } formKey)
        {
            FormWindows.Open(formKey);
            _openForm = null;
        }
        _help.Draw(this);
        _settingsWindow.Draw(this);
        FormWindows.Draw(this);
        _newRecordDialog.Draw();
        HandleShortcuts();
    }

    private void PollLoader()
    {
        if (Loader is null || Loader.IsRunning) return;

        if (Loader.Result is { } session)
        {
            Session = session;
            Document = new StbEditor.Core.EditDocument();
            RenderWindow.AttachDocument(Document);
            Document.DraftsChanged += SyncTransientRecords;
            ResolveActivePlugin(session);
            _framesSinceLoad = 0;
            Thumbnails?.Dispose();
            Preview?.Dispose();
            Preview = null;
            Thumbnails = null;
            foreach (var panel in _panels) panel.OnSessionChanged(this);
            _ = session.Doors; // door → cell index for going through doors, built in the background
            if (RenderWindow.Meshes is { } meshes && RenderWindow.Textures is { } textures)
            {
                Preview = new ObjectPreview(_graphics.Device, _graphics.Context, meshes, textures) { Document = Document };
                Thumbnails = new ThumbnailCache(_graphics.Device, Preview);
            }
            _dataDialog.OnLoaded();
            ApplyCommandLineSelection();
            if (_restoreView is { } view)
            {
                RenderWindow.RestoreView(this, view);
                _restoreView = null;
            }
        }
        else if (Loader.Error is { } error)
        {
            Log.Error($"load failed: {error}");
            _dataDialog.OnFailed(error);
        }

        Loader = null;
    }

    /// <summary>Re-reads the active file after a (re)load so its FormID counter and flags are current.</summary>
    private void ResolveActivePlugin(EditorSession session)
    {
        var path = ActivePlugin?.Path ?? _settings.ActivePluginPath;
        if (path is null) return;

        var plugin = session.Workspace.Active.FirstOrDefault(p => p.Location.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            ?? session.Workspace.Active.FirstOrDefault(p => p.Name.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));

        if (plugin is null)
        {
            // A new plugin not written yet keeps its pending definition; anything else is gone.
            if (ActivePlugin is not { IsNew: true }) ActivePlugin = null;
            return;
        }

        try
        {
            ActivePlugin = StbEditor.Core.ActivePlugin.ForExisting(plugin, session.Game);
            Log.Info($"active file: {plugin.Name} (next FormID {ActivePlugin.NextObjectId:X6})");
        }
        catch (Exception ex)
        {
            Log.Warn($"active file {plugin.Name}: {ex.Message}");
            ActivePlugin = null;
        }
    }

    private void ApplyCommandLineSelection()
    {
        if (!_pendingCommandLineSelection || Session is null) return;
        _pendingCommandLineSelection = false;

        if (_options.Search is { } search) ObjectWindow.SetFilter(search);
        var cliArgs = Environment.GetCommandLineArgs();
        var typeIndex = Array.IndexOf(cliArgs, "--type");
        if (typeIndex >= 0 && typeIndex + 1 < cliArgs.Length) ObjectWindow.SetType(cliArgs[typeIndex + 1]);
        if (_options.Cell is { } cellText)
        {
            RenderWindow.PendingCameraPose = _options.CameraPose;
            RenderWindow.PendingPick = _options.PickAt;
            if (Environment.GetCommandLineArgs().Contains("--no-markers")) RenderWindow.HideMarkers();
            var cli = Environment.GetCommandLineArgs();
            var renderIndex = Array.IndexOf(cli, "--render");
            if (renderIndex >= 0 && renderIndex + 1 < cli.Length) RenderWindow.ApplyRenderDebug(cli[renderIndex + 1]);
            if (RenderWindow.FindCell(this, cellText) is { } cell) RenderWindow.OpenCell(this, cell);
            else Log.Warn($"--cell {cellText}: no such cell");
        }
        if (_options.Select is { } select && Session.Workspace.ParseFormReference(select) is { } key) Select(key);

        _objectShot = ObjectShot.FromCommandLine(this);

        // --dialog newcell|newworld <plugin.esp>: open a creation dialog with a scratch active file (screenshots).
        var dialogIndex = Array.IndexOf(cliArgs, "--dialog");
        if (dialogIndex >= 0 && dialogIndex + 2 < cliArgs.Length)
        {
            SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(cliArgs[dialogIndex + 2], true, false, Session.Game, Session.Workspace.DefaultEncoding));
            if (cliArgs[dialogIndex + 1] == "newworld") _newRecordDialog.ShowWorldspace();
            else _newRecordDialog.ShowInteriorCell();
        }

        // --psys <name>: only particle systems with that name, in the Preview too (debugging effects).
        var psysIndex = Array.IndexOf(cliArgs, "--psys");
        if (psysIndex >= 0 && psysIndex + 1 < cliArgs.Length)
        {
            RenderWindow.ApplyRenderDebug("psys=" + cliArgs[psysIndex + 1]);
            if (Preview is not null) Preview.Renderer.Options.ParticleFilter = cliArgs[psysIndex + 1];
        }
    }

    /// <summary>--autotest-save &lt;folder&gt;: once the --cell scene settles, nudge the picked object, save into a new plugin there.</summary>
    private int _autotestStage;

    private void RunAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(cli, "--autotest-save");
        if (i < 0 || i + 1 >= cli.Length || Session is null) return;

        switch (_autotestStage)
        {
            case 0 when RenderWindow.IsSettled && RenderWindow.SingleSelection is { } obj:
                var before = obj.State;
                RenderWindow.PreviewFieldEdit(obj, before with { Position = before.Position + new Vector3(0, 0, 64) });
                RenderWindow.CommitFieldEdit("autotest move");
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[i + 1], "STBEditorAutotest.esp"), true, false, Session.Game, Session.Workspace.DefaultEncoding));
                Log.Info($"autotest: moved {obj.Reference}, saving");
                Save();
                _autotestStage = 1;
                break;
            case 1 when Loader is null && !Saver.IsBusy && Document.ChangedCount == 0 && RenderWindow.IsSettled:
                Log.Info($"autotest: reloaded, active {ActivePlugin?.Name}, scene {RenderWindow.Scene?.Title}");
                _autotestStage = 2;
                break;
        }
    }

    private ObjectShot? _objectShot;
    private readonly NewRecordDialog _newRecordDialog;
    private readonly HelpWindow _help = new();
    private readonly SettingsWindow _settingsWindow = new();

    public bool AutotestDone =>
        (!Environment.GetCommandLineArgs().Contains("--autotest-save") || _autotestStage == 2)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-new") || _newTestStage == 4)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-land") || _landTestStage == 3)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-landview") || _landViewStage == 3)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-dup") || _dupStage == 3)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-nav") || _navStage == 3)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-fly") || _flyDone)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-gizmo") || _gizmoDone)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-door") || _doorDone)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-zoom") || _zoomDone)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-form") || _formStage == 2)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-picker") || _pickerStage == 9);

    private int _pickerStage;
    private int _pickerFrames;
    private FormKey? _pickerBefore;
    private System.Numerics.Vector2 _pickerClick;

    /// <summary>
    /// --autotest-picker &lt;FormID of an NPC&gt; &lt;text&gt;: in the record's form window, click its Race field,
    /// type the text (fake keyboard), check suggestions appear, take the 2nd with Down + Enter; logs the
    /// race before and after. Nothing is saved.
    /// </summary>
    private void RunPickerAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(cli, "--autotest-picker");
        if (i < 0 || i + 2 >= cli.Length || Session is not { } session || _pickerStage == 9) return;
        if (session.Workspace.ParseFormReference(cli[i + 1]) is not { } npc) return;
        _pickerFrames++;
        FormKey? Race() => (StbEditor.Core.OverrideEditing.Override(Document, npc)?.Record as Mutagen.Bethesda.Skyrim.INpcGetter
            ?? new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog).Resolve<Mutagen.Bethesda.Skyrim.INpcGetter>(npc))?.Race.FormKeyNullable;
        switch (_pickerStage)
        {
            case 0:
                FormWindows.Open(npc);
                _pickerBefore = Race();
                _pickerStage = 1;
                _pickerFrames = 0;
                break;
            case 1 when _pickerFrames > 20 && cli.Contains("browse") && FormField.LastRects.TryGetValue("Race", out var field):
                // The ▼ button right of the field: open the full list, type the filter, leave it open.
                _pickerClick = new System.Numerics.Vector2(field.Max.X + ImGui.GetStyle().ItemSpacing.X + ImGui.GetFrameHeight() / 2, (field.Min.Y + field.Max.Y) / 2);
                ImGuiLayer.TestMouse = (_pickerClick, true);
                _pickerStage = 10;
                _pickerFrames = 0;
                break;
            case 10 when _pickerFrames > 3:
                ImGuiLayer.TestMouse = (_pickerClick, false);
                _pickerStage = 11;
                _pickerFrames = 0;
                break;
            case 11 when _pickerFrames > 5:
                ImGuiLayer.TestMouse = null;
                ImGuiLayer.TestText = cli[i + 2];
                Log.Info("autotest-picker: browse list opened, filter typed");
                _pickerStage = 9;
                break;
            case 1 when _pickerFrames > 20 && FormField.LastRects.TryGetValue("Race", out var rect):
                ImGuiLayer.TestMouse = ((rect.Min + rect.Max) / 2, true);
                _pickerStage = 2;
                _pickerFrames = 0;
                break;
            case 2 when _pickerFrames > 2:
                ImGuiLayer.TestMouse = ((FormField.LastRects["Race"].Min + FormField.LastRects["Race"].Max) / 2, false);
                _pickerStage = 3;
                _pickerFrames = 0;
                break;
            case 3 when _pickerFrames > 3:
                // The click selected the old text (AutoSelectAll): typing replaces it.
                _pickerStage = 4;
                _pickerFrames = 0;
                break;
            case 4 when _pickerFrames > 3:
                ImGuiLayer.TestText = cli[i + 2];
                _pickerStage = 5;
                _pickerFrames = 0;
                break;
            case 5 when _pickerFrames > 10:
                Log.Info($"autotest-picker: typed \"{cli[i + 2]}\", {FormField.LastSuggestionCount} suggestion(s) shown; query {FormField.LastQuery}");
                if (cli.Contains("show"))
                {
                    // Leave the list open for the screenshot.
                    _pickerStage = 9;
                    break;
                }
                if (cli.Contains("click"))
                {
                    // Third row of the list under the field (a header line, then the rows).
                    var field = FormField.LastRects["Race"];
                    var row = ImGui.GetTextLineHeightWithSpacing();
                    _pickerClick = new System.Numerics.Vector2(field.Min.X + 60, field.Max.Y + ImGui.GetStyle().WindowPadding.Y + row * 3.5f);
                    ImGuiLayer.TestMouse = (_pickerClick, false);
                    _pickerStage = 7;
                }
                else
                {
                    ImGuiLayer.TestKeys.Enqueue(ImGuiKey.DownArrow);
                    ImGuiLayer.TestKeys.Enqueue(ImGuiKey.Enter);
                    _pickerStage = 6;
                }
                _pickerFrames = 0;
                break;
            case 7 when _pickerFrames > 3:
                ImGuiLayer.TestMouse = (_pickerClick, true);
                _pickerStage = 8;
                _pickerFrames = 0;
                break;
            case 8 when _pickerFrames > 3:
                ImGuiLayer.TestMouse = (_pickerClick, false);
                _pickerStage = 6;
                _pickerFrames = 0;
                break;
            case 6 when _pickerFrames > 12:
                ImGuiLayer.TestMouse = null;
                var after = Race();
                Log.Info($"autotest-picker: race {(_pickerBefore is { } b ? FormField.Describe(this, b) : "-")} -> {(after is { } a ? FormField.Describe(this, a) : "-")}, undo: {Document.History.UndoName}");
                _pickerStage = 9;
                break;
        }
    }

    private int _formStage;
    private FormKey _formKey;

    /// <summary>
    /// --autotest-form &lt;folder&gt; [FormID of an NPC]: edits through the same EditTarget the form tabs use —
    /// a fine float (height 1.000123), a skill value, an enum, an added inventory row — saves into a new
    /// plugin in the folder, reloads, logs what the record reads back.
    /// </summary>
    private void RunFormAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(cli, "--autotest-form");
        if (i < 0 || i + 1 >= cli.Length || Session is not { } session) return;
        switch (_formStage)
        {
            case 0:
            {
                var npc = i + 2 < cli.Length && session.Workspace.ParseFormReference(cli[i + 2]) is { } given ? given
                    : session.Catalog.BySignature["NPC_"].First(r => r.FormKey.ModKey.FileName == "Skyrim.esm").FormKey;
                _formKey = npc;
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[i + 1], "STBEditorFormAutotest.esp"), false, false, session.Game, session.Workspace.DefaultEncoding));
                var record = new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog).Resolve(npc)!;
                var item = session.Catalog.BySignature["MISC"].First().FormKey;
                var target = new EditTarget(
                    () => StbEditor.Core.OverrideEditing.WorkingCopy(Document, npc, (IMajorRecordGetter?)StbEditor.Core.OverrideEditing.Override(Document, npc)?.Record ?? record),
                    (name, edited) => StbEditor.Core.OverrideEditing.CommitEdit(Document, name, edited, StbEditor.Core.RecordPlace.TopLevel));
                target.Apply("height", c => StbEditor.Core.RecordFields.Set(c, ["Height"], 1.000123f));
                target.Apply("skill", c => StbEditor.Core.RecordFields.Set(c, ["PlayerSkills", "SkillValues", Mutagen.Bethesda.Skyrim.Skill.OneHanded], (byte)33));
                target.Apply("confidence", c => StbEditor.Core.RecordFields.Set(c, ["AIData", "Confidence"], Mutagen.Bethesda.Skyrim.Confidence.Foolhardy));
                target.Apply("add item", c => StbEditor.Core.RecordFields.AddItem(c, ["Items"]));
                var edited = StbEditor.Core.OverrideEditing.Override(Document, npc)!.Record;
                var row = ((System.Collections.IList)StbEditor.Core.RecordFields.Get(edited, ["Items"])!).Count - 1;
                target.Apply("item", c => StbEditor.Core.RecordFields.Set(c, ["Items", row, "Item", "Item"], item));
                target.Apply("count", c => StbEditor.Core.RecordFields.Set(c, ["Items", row, "Item", "Count"], 7));
                Log.Info($"autotest-form: {record.EditorID} edited ({History()}), {Document.PendingCount} pending; saving");
                Save();
                _formStage = 1;
                break;
            }
            case 1 when Loader is null && !Saver.IsBusy && Document.PendingCount == 0:
            {
                var lookup = new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog);
                var back = lookup.Resolve(_formKey);
                string F(params object[] path) => StbEditor.Core.RecordFields.Repr(StbEditor.Core.RecordFields.Get(back!, path));
                var items = StbEditor.Core.RecordFields.Get(back!, ["Items"]) as System.Collections.IEnumerable;
                var last = items?.Cast<object>().LastOrDefault();
                Log.Info($"autotest-form: reloaded {back?.EditorID} from {session.Catalog.Get(_formKey)?.WinningPlugin.Name}: height {F("Height")}, one-handed {F("PlayerSkills", "SkillValues", Mutagen.Bethesda.Skyrim.Skill.OneHanded)}, confidence {F("AIData", "Confidence")}, " +
                    $"items {items?.Cast<object>().Count()}, last {StbEditor.Core.RecordFields.Repr(last is null ? null : StbEditor.Core.RecordFields.Get(last, ["Item", "Item"]))} x {StbEditor.Core.RecordFields.Repr(last is null ? null : StbEditor.Core.RecordFields.Get(last, ["Item", "Count"]))}");
                _formStage = 2;
                break;
            }
        }

        string History() => string.Join(", ", Document.History.Entries.Select(e => e.Name));
    }

    private bool _gizmoDone;
    private string? _openForm;
    private bool _doorDone;
    private bool _zoomDone;

    private int _flySteps = -1;
    private readonly System.Diagnostics.Stopwatch _flyClock = System.Diagnostics.Stopwatch.StartNew();
    private double _flyNext;
    private bool _flyDone;

    /// <summary>
    /// --autotest-fly &lt;cells&gt; (with --cell, an exterior): fly east one cell at a time, each step once
    /// the grid has streamed in, logging process and video memory and the cache sizes.
    /// </summary>
    private void RunFlyAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(cli, "--autotest-fly");
        if (i < 0 || _flyDone || Session is null) return;
        var total = i + 1 < cli.Length && int.TryParse(cli[i + 1], out var n) ? n : 20;
        // A cell every 3 s, about a fast fly-through by hand (eviction waits for 20 s unused).
        if (_flyClock.Elapsed.TotalSeconds < _flyNext || !RenderWindow.FlyStepForTest()) return;
        _flyNext = _flyClock.Elapsed.TotalSeconds + 3;

        _flySteps++;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var (used, budget) = _graphics.VideoMemory;
        Log.Info($"autotest-fly: step {_flySteps}/{total} cell {RenderWindow.Scene?.CenterCell}: private {process.PrivateMemorySize64 >> 20} MB, " +
            $"managed {GC.GetTotalMemory(false) >> 20} MB (GC committed {GC.GetGCMemoryInfo().TotalCommittedBytes >> 20} MB), video {used >> 20}/{budget >> 20} MB, meshes {RenderWindow.Meshes?.Count} ({RenderWindow.Meshes?.Bytes >> 20} MB), textures {RenderWindow.Textures?.Count} ({RenderWindow.Textures?.Bytes >> 20} MB), " +
            $"objects {RenderWindow.Scene?.Objects.Count}");
        if (_flySteps >= total) _flyDone = true;
    }

    /// <summary>
    /// --autotest-nav &lt;folder&gt; (with --cell, an interior): delete a navmesh triangle, rebuild it from its
    /// three vertices, move a vertex, undo/redo, save into a new plugin, reload, compare.
    /// </summary>
    private int _navStage;
    private FormKey _navKey;
    private int _navTriangles;
    private Vector3 _navMoved;

    private void RunNavmeshAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(cli, "--autotest-nav");
        if (i < 0 || i + 1 >= cli.Length || Session is not { } session || Loader is not null) return;
        switch (_navStage)
        {
            case 0 when RenderWindow.IsSettled && RenderWindow.Scene is { Navmeshes.Count: > 0 } scene:
            {
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[i + 1], "STBEditorNavAutotest.esp"), true, false, session.Game, session.Workspace.DefaultEncoding));
                var view = scene.Navmeshes.OrderByDescending(n => n.Record.Data!.Triangles.Count).First();
                _navKey = view.Key;
                var before = view.Record.Data!.Triangles.Count;
                var corners = view.Record.Data.Triangles[0].Vertices;
                Log.Info($"autotest-nav: {view.Key} has {before} triangles");

                RenderWindow.SelectNavmeshForTest(view.Key, [], [0]);
                RenderWindow.DeleteNavmeshSelection();
                RenderWindow.RefreshNavmeshesNow(this);
                var afterDelete = RenderWindow.Scene!.Navmeshes.First(n => n.Key == _navKey).Record.Data!.Triangles.Count;

                RenderWindow.SelectNavmeshForTest(_navKey, [corners.X, corners.Y, corners.Z], []);
                RenderWindow.CreateNavmeshTriangle();
                RenderWindow.RefreshNavmeshesNow(this);
                var afterCreate = RenderWindow.Scene!.Navmeshes.First(n => n.Key == _navKey).Record.Data!;
                _navTriangles = afterCreate.Triangles.Count;

                RenderWindow.SelectNavmeshForTest(_navKey, [0], []);
                RenderWindow.EditNavmesh("move", (mesh, _) =>
                {
                    var p = mesh.Data!.Vertices[0];
                    mesh.Data.Vertices[0] = new Noggog.P3Float(p.X + 32, p.Y, p.Z);
                });
                RenderWindow.RefreshNavmeshesNow(this);
                var moved = RenderWindow.Scene!.Navmeshes.First(n => n.Key == _navKey).Record.Data!.Vertices[0];
                _navMoved = new Vector3(moved.X, moved.Y, moved.Z);
                Document.Undo();
                RenderWindow.RefreshNavmeshesNow(this);
                var undone = RenderWindow.Scene!.Navmeshes.First(n => n.Key == _navKey).Record.Data!.Vertices[0];
                Document.Redo();
                Log.Info($"autotest-nav: triangles {before} -> delete {afterDelete} -> create {_navTriangles}; vertex 0 moved to {_navMoved}, undo {undone.X:0.#}; saving");
                Save();
                _navStage = 1;
                break;
            }
            case 1 when !Saver.IsBusy && Document.PendingCount == 0 && RenderWindow.IsSettled:
            {
                var file = Path.Combine(cli[i + 1], "STBEditorNavAutotest.esp");
                using var mod = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(file, session.Game.Release);
                var saved = mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells).SelectMany(c => c.NavigationMeshes).FirstOrDefault(n => n.FormKey == _navKey);
                var view = RenderWindow.Scene?.Navmeshes.FirstOrDefault(n => n.Key == _navKey);
                Log.Info($"autotest-nav: saved navmesh {(saved is null ? "MISSING" : $"{saved.Data!.Triangles.Count} triangles, vertex 0 {saved.Data.Vertices[0]}")}; scene after reload {view?.Record.Data?.Triangles.Count} triangles");
                _navStage = 3;
                break;
            }
        }
    }

    /// <summary>--autotest-dup &lt;folder&gt; (with --cell, an interior): duplicate a STAT, place the copy, save, reload, check.</summary>
    private int _dupStage;
    private FormKey _dupKey;

    private void RunDuplicateAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(cli, "--autotest-dup");
        if (i < 0 || i + 1 >= cli.Length || Session is not { } session || Loader is not null) return;
        switch (_dupStage)
        {
            case 0 when RenderWindow.IsSettled && RenderWindow.Scene is { IsInterior: true } scene:
            {
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[i + 1], "STBEditorDupAutotest.esp"), true, false, session.Game, session.Workspace.DefaultEncoding));
                var source = session.Catalog.BySignature["STAT"].First(r => r.EditorId?.StartsWith("Barrel", StringComparison.OrdinalIgnoreCase) == true);
                var record = new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog).Resolve(source.FormKey)!;
                var copy = (Mutagen.Bethesda.Plugins.Records.IMajorRecord)record.Duplicate(ActivePlugin!.Allocate());
                copy.EditorID = source.EditorId + "STBDup";
                StbEditor.Core.OverrideEditing.CommitEdit(Document, T("Duplicate"), copy, StbEditor.Core.RecordPlace.TopLevel);
                _dupKey = copy.FormKey;
                Log.Info($"autotest-dup: {copy.EditorID} {copy.FormKey} in catalog: {session.Catalog.Get(copy.FormKey) is not null}");
                RenderWindow.PlaceForTest(this, copy.FormKey, scene.Focus);
                var placed = RenderWindow.Scene!.Objects.LastOrDefault();
                Log.Info($"autotest-dup: placed {placed?.BaseEditorId} model {placed?.ModelPath}; saving");
                Save();
                _dupStage = 1;
                break;
            }
            case 1 when !Saver.IsBusy && Document.PendingCount == 0 && RenderWindow.IsSettled:
            {
                var record = session.Catalog.Get(_dupKey);
                var refs = RenderWindow.Scene!.Objects.Count(o => o.Base == _dupKey);
                Log.Info($"autotest-dup: reloaded; record {record?.EditorId} from {record?.WinningPlugin.Name}, transient {record?.IsTransient}, references in scene {refs}");
                _dupStage = 3;
                break;
            }
        }
    }

    /// <summary>--autotest-landview &lt;folder&gt;: new 3×3 worldspace with flat land, a hill, paint and smoothing; nothing saved.</summary>
    private int _landViewStage;
    private FormKey _landViewWorld;

    private void RunLandViewAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(cli, "--autotest-landview");
        if (i < 0 || i + 1 >= cli.Length || Session is not { } session || Loader is not null) return;
        switch (_landViewStage)
        {
            case 0:
            {
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[i + 1], "STBEditorLandView.esp"), true, false, session.Game, session.Workspace.DefaultEncoding));
                var world = StbEditor.Core.NewRecords.Worldspace(ActivePlugin!.Allocate(), ActivePlugin.Allocate(), session.Game.Release, "STBLandView", null, null, null, -1e5f, 0f, true);
                Document.Execute(T("New worldspace"), [], [world]);
                _landViewWorld = world.Key;
                RenderWindow.OpenExterior(this, world.Key, 0, 0);
                _landViewStage = 1;
                break;
            }
            case 1 when RenderWindow.IsSettled && RenderWindow.Scene?.Worldspace == _landViewWorld:
            {
                RenderWindow.CreateFlatLand(this, 0f);
                RenderWindow.RefreshTerrainNow(this);
                var brush = RenderWindow.Brush;
                brush.Mode = Panels.LandBrushMode.Raise;
                brush.Radius = 2500f;
                brush.Strength = 900f;
                RenderWindow.StrokeForTest(this, new Vector3(2048, 2048, 0), 2f);
                brush.Radius = 1200f;
                RenderWindow.StrokeForTest(this, new Vector3(4096, 1200, 0), 1.5f);
                var snow = session.Catalog.BySignature["LTEX"].FirstOrDefault(t => t.EditorId == "LSnow01") ?? session.Catalog.BySignature["LTEX"][0];
                var grass = session.Catalog.BySignature["LTEX"].FirstOrDefault(t => t.EditorId == "LGrass01") ?? session.Catalog.BySignature["LTEX"][1];
                brush.Mode = Panels.LandBrushMode.Paint;
                brush.Strength = 100f;
                brush.Texture = grass.FormKey;
                brush.Radius = 6000f;
                RenderWindow.StrokeForTest(this, new Vector3(2048, 2048, 0), 0.5f);
                brush.Texture = snow.FormKey;
                brush.Radius = 1400f;
                RenderWindow.StrokeForTest(this, new Vector3(2048, 2048, 0), 0.6f);
                brush.Mode = Panels.LandBrushMode.Smooth;
                brush.Radius = 900f;
                RenderWindow.StrokeForTest(this, new Vector3(4096, 1200, 0), 1f);
                RenderWindow.RefreshTerrainNow(this);
                RenderWindow.PendingCameraPoseNow("-6500,-7500,5200,40,-28");
                Log.Info($"autotest-landview: {Document.Drafts.Count} drafts, {RenderWindow.Scene!.Terrain.Count} terrain cells");
                _landViewStage = 2;
                break;
            }
            case 2 when RenderWindow.IsSettled:
                _landViewStage = 3;
                break;
        }
    }

    /// <summary>
    /// --autotest-land &lt;folder&gt; (with --cell): raise a hill in the middle of the loaded cell, undo
    /// and redo it, paint a texture, save into a new plugin there, reload, compare the heights.
    /// </summary>
    private int _landTestStage;
    private Vector3 _landTestPoint;
    private float _landTestBefore, _landTestAfter;

    private void RunLandAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(cli, "--autotest-land");
        if (i < 0 || i + 1 >= cli.Length || Session is not { } session || Loader is not null) return;

        switch (_landTestStage)
        {
            case 0 when RenderWindow.IsSettled && RenderWindow.Scene is { IsInterior: false } scene && scene.Terrain.Count > 0:
            {
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[i + 1], "STBEditorLandAutotest.esp"), true, false, session.Game, session.Workspace.DefaultEncoding));
                var (cx, cy) = scene.CenterCell;
                var x = (cx + 0.5f) * TerrainCell.CellSize;
                var y = cy * TerrainCell.CellSize + 40f; // on the edge to the cell below: tests stitching
                _landTestBefore = RenderWindow.HeightForTest(x, y) ?? 0;
                _landTestPoint = new Vector3(x, y, _landTestBefore);

                var brush = RenderWindow.Brush;
                brush.Mode = Panels.LandBrushMode.Raise;
                brush.Radius = 900f;
                brush.Strength = 600f;
                RenderWindow.StrokeForTest(this, _landTestPoint, 1f);
                _landTestAfter = RenderWindow.HeightForTest(x, y) ?? 0;
                Document.Undo();
                RenderWindow.RefreshTerrainNow(this);
                var undone = RenderWindow.HeightForTest(x, y) ?? 0;
                Document.Redo();
                RenderWindow.RefreshTerrainNow(this);
                var redone = RenderWindow.HeightForTest(x, y) ?? 0;
                Log.Info($"autotest-land: height {_landTestBefore:0} -> {_landTestAfter:0}, undo {undone:0}, redo {redone:0}");

                var ltex = session.Catalog.BySignature.GetValueOrDefault("LTEX")?.FirstOrDefault(t => t.EditorId?.Contains("Snow", StringComparison.OrdinalIgnoreCase) == true)
                    ?? session.Catalog.BySignature["LTEX"][0];
                brush.Mode = Panels.LandBrushMode.Paint;
                brush.Texture = ltex.FormKey;
                brush.Strength = 60f;
                RenderWindow.StrokeForTest(this, _landTestPoint + new Vector3(600, 900, 0), 1f);
                Log.Info($"autotest-land: painted {ltex.EditorId}; {Document.Drafts.Count} land draft(s), saving");
                Save();
                _landTestStage = 1;
                break;
            }
            case 1 when !Saver.IsBusy && Document.PendingCount == 0 && RenderWindow.IsSettled:
            {
                var height = RenderWindow.HeightForTest(_landTestPoint.X, _landTestPoint.Y) ?? 0;
                var file = Path.Combine(cli[i + 1], "STBEditorLandAutotest.esp");
                using var mod = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(file, session.Game.Release);
                var lands = mod.Worldspaces.SelectMany(w => w.SubCells).SelectMany(b => b.Items).SelectMany(sb => sb.Items).Count(c => c.Landscape is not null);
                Log.Info($"autotest-land: reloaded; height there {height:0} (edited {_landTestAfter:0}); plugin has {lands} LAND record(s)");
                _landTestStage = 3;
                break;
            }
        }
    }

    /// <summary>
    /// --autotest-new &lt;folder&gt;: new interior cell + object, new worldspace + object beyond its
    /// cells (auto-created cell), save into a new plugin there, reload and check the records exist.
    /// </summary>
    private int _newTestStage;
    private FormKey _newTestCell, _newTestWorld;

    private void RunNewRecordsAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(cli, "--autotest-new");
        if (i < 0 || i + 1 >= cli.Length || Session is not { } session || Loader is not null) return;

        var barrel = session.Catalog.BySignature["STAT"].First(r => r.EditorId == "FarmhouseBarrel01" || r.EditorId?.StartsWith("Barrel", StringComparison.OrdinalIgnoreCase) == true);
        switch (_newTestStage)
        {
            case 0:
            {
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[i + 1], "STBEditorNewAutotest.esp"), true, false, session.Game, session.Workspace.DefaultEncoding));
                var cell = StbEditor.Core.NewRecords.InteriorCell(ActivePlugin!.Allocate(), session.Game.Release, "STBAutotestRoom", "Комната", StbEditor.Core.LightingSpec.Default, null);
                Document.AddDraft(T("New cell"), cell);
                _newTestCell = cell.Key;
                RenderWindow.OpenCell(this, cell.Key);
                _newTestStage = 1;
                break;
            }
            case 1 when RenderWindow.IsSettled && RenderWindow.Scene?.Title == "STBAutotestRoom":
            {
                RenderWindow.PlaceForTest(this, barrel.FormKey, new Vector3(0, 0, 0));
                var world = StbEditor.Core.NewRecords.Worldspace(ActivePlugin!.Allocate(), ActivePlugin.Allocate(), session.Game.Release, "STBAutotestWorld", "Мир",
                    null, null, -1e5f, 0f, true);
                Document.Execute("New worldspace", [], [world, StbEditor.Core.NewRecords.ExteriorCell(ActivePlugin.Allocate(), session.Game.Release, world.Key, 0, 0)]);
                _newTestWorld = world.Key;
                RenderWindow.OpenExterior(this, world.Key, 0, 0);
                _newTestStage = 2;
                break;
            }
            case 2 when RenderWindow.IsSettled && RenderWindow.Scene?.Worldspace == _newTestWorld:
                RenderWindow.PlaceForTest(this, barrel.FormKey, new Vector3(100, 100, 0));
                RenderWindow.PlaceForTest(this, barrel.FormKey, new Vector3(5000, 100, 0));
                Log.Info($"autotest-new: {Document.Drafts.Count} drafts, {Document.ChangedCount} references, saving");
                Save();
                _newTestStage = 3;
                break;
            case 3 when !Saver.IsBusy && Document.PendingCount == 0 && RenderWindow.IsSettled:
            {
                var cells = session.Catalog.BySignature["CELL"].Where(c => c.Plugins.Any(p => p.Name == "STBEditorNewAutotest.esp")).ToList();
                var worlds = session.Catalog.BySignature["WRLD"].Where(c => c.Plugins.Any(p => p.Name == "STBEditorNewAutotest.esp")).ToList();
                Log.Info($"autotest-new: reloaded; cells {string.Join(", ", cells.Select(c => $"{c.EditorId ?? "(ext)"} {c.Winner.Cell?.GridX},{c.Winner.Cell?.GridY}"))}; worlds {string.Join(", ", worlds.Select(w => w.EditorId))}; scene {RenderWindow.Scene?.Title} with {RenderWindow.Scene?.Objects.Count} objects");
                _newTestStage = 4;
                break;
            }
        }
    }

    public void RenderScene()
    {
        // The 3D view arrives with the renderer (stage 1).
    }

    /// <summary>Which panels are closed goes into the settings (written on exit).</summary>
    private void RememberClosedPanels()
    {
        var closed = _panels.Where(p => !p.IsOpen).Select(p => p.Name).ToList();
        if (!closed.SequenceEqual(_settings.ClosedPanels)) _settings.ClosedPanels = closed;
        _settings.FlySpeed = RenderWindow.FlySpeed;
    }

    public bool ReadyForScreenshot(int frames)
    {
        if (_options.Open || _options.Game is not null)
        {
            if (Session is null) return false;
            if (!AutotestDone) return frames > (Environment.GetCommandLineArgs().Contains("--autotest-fly") ? 60 * 60 * 30 : 60 * 180);
            if (_objectShot is { Done: false }) { _framesSinceLoad = 0; return false; }
            if (_options.Cell is not null && !RenderWindow.IsSettled)
            {
                _framesSinceLoad = 0;
                return frames > 60 * 120; // give up after ~2 minutes
            }
            // A few frames for tables to settle, thumbnails to render and the selection to scroll into view.
            if (Thumbnails?.HasPending == true && frames < 60 * 60) { _framesSinceLoad = 0; return false; }
            return ++_framesSinceLoad > 30;
        }

        return frames > 5;
    }

    public bool ConfirmClose() => Document.PendingCount == 0;

    private void DrawMainMenu()
    {
        if (!ImGui.BeginMainMenuBar()) return;

        if (ImGui.BeginMenu(T("File")))
        {
            if (ImGui.MenuItem(T("Data..."), T("Ctrl+O"))) _dataDialog.Show();
            ImGui.Separator();
            if (ImGui.MenuItem(T("Active File..."), (string?)null, false, Session is not null)) ShowActiveFileDialog();
            var saveLabel = ActivePlugin is { } ap ? TF($"Save into {ap.Name}") : T("Save...");
            if (ImGui.MenuItem(saveLabel, T("Ctrl+S"), false, Session is not null && !Saver.IsBusy)) Save();
            ImGui.Separator();
            if (ImGui.MenuItem("Exit")) Environment.Exit(0);
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu(T("Edit")))
        {
            var history = Document.History;
            if (ImGui.MenuItem(history.UndoName is { } u ? TF($"Undo {u}") : T("Undo"), T("Ctrl+Z"), false, history.CanUndo)) Document.Undo();
            if (ImGui.MenuItem(history.RedoName is { } r ? TF($"Redo {r}") : T("Redo"), T("Ctrl+Y"), false, history.CanRedo)) Document.Redo();
            ImGui.Separator();
            ImGui.TextColored(Theme.TextDim, TF($"{Document.ChangedCount} changed reference(s), {Document.Drafts.Count} new record(s), not saved"));
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu(T("World")))
        {
            var ready = Session is not null;
            if (ImGui.MenuItem(T("New Interior Cell..."), (string?)null, false, ready)) _newRecordDialog.ShowInteriorCell();
            if (ImGui.MenuItem(T("New Worldspace..."), (string?)null, false, ready)) _newRecordDialog.ShowWorldspace();
            ImGui.Separator();
            ImGui.TextColored(Theme.TextDim, T("Placing an object where no exterior cell exists creates the cell."));
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu(T("Navigate")))
        {
            if (ImGui.MenuItem(T("Back"), T("Alt+Left"), false, _historyIndex > 0)) Back();
            if (ImGui.MenuItem(T("Forward"), T("Alt+Right"), false, _historyIndex < _history.Count - 1)) Forward();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu(T("View")))
        {
            foreach (var panel in _panels)
            {
                var open = panel.IsOpen;
                if (ImGui.MenuItem(T(panel.Name), (string?)null, open)) panel.IsOpen = !open;
            }
            ImGui.Separator();
            if (ImGui.MenuItem(T("Settings..."))) _settingsWindow.Show();
            if (ImGui.BeginMenu(T("Frame limit")))
            {
                foreach (var (label, fps) in new[] { ("30 FPS", 30), ("60 FPS", 60), ("120 FPS", 120), (T("Monitor refresh (vsync)"), 0) })
                    if (ImGui.MenuItem(label, (string?)null, _settings.FpsLimit == fps)) _settings.FpsLimit = fps;
                ImGui.Separator();
                ImGui.TextColored(Theme.TextDim, T("Lower = cooler GPU; 15 FPS while in the background."));
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu(T("Language / Язык")))
            {
                if (ImGui.MenuItem(T("English"), (string?)null, !Loc.Russian)) SetLanguage("en");
                if (ImGui.MenuItem("Русский", (string?)null, Loc.Russian)) SetLanguage("ru");
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (ImGui.MenuItem(T("Reset layout"))) DefaultLayout.RequestReset();
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu(T("Help")))
        {
            if (ImGui.MenuItem(T("Keys and features"), "F1")) _help.Show();
            if (ImGui.MenuItem(T("Project page (GitHub)")))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/STB-Team/STB-Editor") { UseShellExecute = true });
            if (ImGui.MenuItem(T("Open log file")))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true });
            ImGui.Separator();
            ImGui.TextColored(Theme.TextDim, $"STB Editor {typeof(EditorApp).Assembly.GetName().Version?.ToString(3)}");
            ImGui.EndMenu();
        }

        if (Session is { } session)
        {
            ImGui.SameLine(ImGui.GetWindowWidth() - ImGui.CalcTextSize(TF($"{session.Game.DisplayName}  ·  {session.Game.Mo2?.ProfileName ?? "Data"}  ·  {session.Workspace.Active.Count} plugins")).X - 16);
            ImGui.TextColored(Theme.TextDim, TF($"{session.Game.DisplayName}  ·  {session.Game.Mo2?.ProfileName ?? "Data"}  ·  {session.Workspace.Active.Count} plugins"));
        }

        ImGui.EndMainMenuBar();
    }

    private void HandleShortcuts()
    {
        var io = ImGui.GetIO();
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.O, false)) _dataDialog.Show();
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S, false)) Save();
        if (io.KeyAlt && ImGui.IsKeyPressed(ImGuiKey.LeftArrow, false)) Back();
        if (io.KeyAlt && ImGui.IsKeyPressed(ImGuiKey.RightArrow, false)) Forward();
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.F, false)) ObjectWindow.FocusSearch();

        if (!io.WantTextInput && io.KeyCtrl)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Z, true) && !io.KeyShift) Document.Undo();
            if (ImGui.IsKeyPressed(ImGuiKey.Y, true) || (io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.Z, true))) Document.Redo();
        }
    }

    public void SetLanguage(string language)
    {
        _settings.Language = language;
        Loc.Russian = language == "ru";
    }

    public Vector2 Scaled(float x, float y) => new Vector2(x, y) * (ImGui.GetFontSize() / 16f);

    public void Dispose()
    {
        Thumbnails?.Dispose();
        Preview?.Dispose();
        PreviewPanel.Shutdown();
        RenderWindow.Shutdown();
        Loader?.Cancel();
        Session?.Dispose();
    }
}
