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

    public bool IsBusy => Loader?.IsRunning == true || RenderWindow.NeedsRedraw || PreviewPanel.NeedsRedraw || Panels.FaceEditor.IsBusy || ObjectWindow.IsScanning || UseInfo.IsBusy
        || Thumbnails?.HasPending == true || Saver.IsBusy || Panels.QuestEditor.IsCompiling || Panels.DialogueEditor.IsCompiling || ScriptSources.IsBusy || PluginCheck.IsBusy || Archive.IsBusy || FindText.IsBusy || VoiceFilesWindow.IsBusy || ScriptsWindow.IsBusy || ExportDataWindow.IsBusy;

    /// <summary>Cell and worldspace property windows.</summary>
    public Panels.PlacePropertiesWindows PlaceProperties { get; } = new();

    /// <summary>Papyrus source windows (Scripts tab: Source / New script).</summary>
    public Panels.ScriptSourceWindows ScriptSources { get; } = new();

    private (EditorSession Session, string? Compiler, StbEditor.Data.PapyrusSetup Setup)? _papyrus;

    /// <summary>The Papyrus compiler and the load order's script sources, found once per session.</summary>
    public StbEditor.Data.PapyrusSetup? Papyrus
    {
        get
        {
            if (Session is not { } session) return null;
            if (_papyrus is { } p && p.Session == session && p.Compiler == Settings.PapyrusCompiler) return p.Setup;
            var setup = StbEditor.Data.PapyrusSetup.Find(session.Game, Settings.PapyrusCompiler);
            _papyrus = (session, Settings.PapyrusCompiler, setup);
            return setup;
        }
    }
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
    public CompareWindows Compare { get; } = new();
    public UseInfoWindows UseInfo { get; } = new();
    public SpreadsheetWindow Spreadsheets { get; } = new();
    public NewFormDialog NewForm { get; private set; } = null!;

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
        if (cliArgs0.Contains("--show-settings")) _settingsWindow.Show();
        // --theme <name>: this run only (screenshots).
        var theme = Array.IndexOf(cliArgs0, "--theme");
        Theme.Use(theme >= 0 && theme + 1 < cliArgs0.Length ? cliArgs0[theme + 1] : settings.Theme);
        // --ui-scale <factor>: this run only (screenshots).
        var uiScale = Array.IndexOf(cliArgs0, "--ui-scale");
        Theme.SetUiScale(uiScale >= 0 && uiScale + 1 < cliArgs0.Length && float.TryParse(cliArgs0[uiScale + 1], System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : settings.UiScale);
        _openForm = cliArgs0.SkipWhile(x => x != "--open-form").Skip(1).FirstOrDefault();

        if (options.Game is not null) settings.GameFolder = options.Game;
        if (options.Mo2 is not null) settings.Mo2Folder = options.Mo2;
        if (options.Profile is not null) settings.Mo2Profile = options.Profile;

        _dataDialog = new DataDialog(this);
        NewForm = new NewFormDialog(this);
        _activeDialog = new ActiveFileDialog(this);
        _newRecordDialog = new NewRecordDialog(this);
        _pendingCommandLineSelection = options.Select is not null || options.Search is not null || options.Cell is not null
            || Environment.GetCommandLineArgs().Contains("--object-shot") || Environment.GetCommandLineArgs().Contains("--psys")
            || Environment.GetCommandLineArgs().Contains("--dialog") || Environment.GetCommandLineArgs().Contains("--type");

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
        Compare.CloseAll();
        UseInfo.CloseAll();
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
        RunCompareAutotest();
        RunSaveCheckAutotest();
        RunOpsAutotest();
        RunFilterAutotest();
        RunMastersAutotest();
        RunUseInfoAutotest();
        RunBatchAutotest();
        Panels.QuestEditor.Update(this);
        Panels.DialogueEditor.Update(this);
        Panels.FragmentsPanel.Update(this);
        RunTogetherAutotest();
        RunRefMoreAutotest();
        RunConditionsAutotest();
        RunQuestAutotest();
        RunPackageAutotest();
        RunSceneAutotest();
        RunAliasUseAutotest();
        RunTextsAutotest();
        RunNavSplitAutotest();
        RunCompareFileAutotest();
        RunLevelMergeAutotest();
        RunBodyTintAutotest();
        RunHeightmapAutotest();
        RunVertexColorAutotest();
        RunPaletteAutotest();
        RunPlaceAutotest();
        RunFaceGenAutotest();
        RunHavokAutotest();
        RunNavGenAutotest();
        RunPerkTreeAutotest();
        RunIdleTreeAutotest();
        RunVoiceAutotest();
        RunSkyAutotest();
        RunRegionAutotest();
        RunScriptAutotest();
        RunExportAutotest();
        RunFaceAutotest();
        RunLayersAutotest();
        RunStoryManagerAutotest();
        RunFragmentsAutotest();
        RunDialogueAutotest();
        RunCellPropsAutotest();
        RunFinalizeAutotest();
        RunCleanAutotest();
        RunArchiveAutotest();
        RunCompactAutotest();
        RunCompareCopyAutotest();
        RunFindTextAutotest();
        RunSheetAutotest();
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
        _mastersWindow.Draw(this);
        PluginCheck.Draw(this);
        Archive.Draw(this);
        Compact.Draw(this);
        Texts.Draw(this);
        CompareFile.Draw(this);
        LeveledMergeWindow.Draw(this);
        PaletteWindow.Draw(this);
        LayersWindow.Draw(this);
        StoryManagerWindow.Draw(this);
        IdleTreeWindow.Draw(this);
        VoiceFilesWindow.Draw(this);
        RegionsWindow.Draw(this);
        ScriptsWindow.Draw(this);
        ModGroupsWindow.Draw(this);
        ExportDataWindow.Draw(this);
        FindText.Draw(this);
        FormWindows.Draw(this);
        Compare.Draw(this);
        UseInfo.Draw(this);
        ScriptSources.Draw(this);
        PlaceProperties.Draw(this);
        Spreadsheets.Draw(this);
        NewForm.Draw();
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
            _ = session.Uses; // Use Info index, built in the background (seconds; cached on disk)
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
            // --hide Statics,Lights and --current-cell-only: the Show/Hide options for screenshots.
            if (Environment.GetCommandLineArgs().SkipWhile(a => a != "--hide").Skip(1).FirstOrDefault() is { } hide) RenderWindow.HideGroups(hide.Split(','));
            if (Environment.GetCommandLineArgs().Contains("--current-cell-only")) RenderWindow.HideGroups([], currentCellOnly: true);
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
    private readonly MastersWindow _mastersWindow = new();

    /// <summary>File → Check and clean.</summary>
    public Panels.PluginCheckWindow PluginCheck { get; } = new();

    /// <summary>File → Pack into BSA.</summary>
    public Panels.ArchiveWindow Archive { get; } = new();

    /// <summary>File → Compact FormIDs.</summary>
    public Panels.CompactWindow Compact { get; } = new();
    public Panels.TextsWindow Texts { get; } = new();
    public Panels.CompareFileWindow CompareFile { get; } = new();
    public Panels.LeveledMergeWindow LeveledMergeWindow { get; } = new();
    public Panels.PaletteWindow PaletteWindow { get; } = new();
    public Panels.LayersWindow LayersWindow { get; } = new();
    public Panels.StoryManagerWindow StoryManagerWindow { get; } = new();
    public Panels.IdleTreeWindow IdleTreeWindow { get; } = new();
    public Panels.VoiceFilesWindow VoiceFilesWindow { get; } = new();
    public Panels.RegionsWindow RegionsWindow { get; } = new();
    public Panels.ScriptsWindow ScriptsWindow { get; } = new();
    public Panels.ModGroupsWindow ModGroupsWindow { get; } = new();
    public Panels.ExportDataWindow ExportDataWindow { get; } = new();

    /// <summary>xEdit's ModGroups (Documents\STB Editor\modgroups.json), edited in File → ModGroups.</summary>
    public List<StbEditor.Core.ModGroup> ModGroupList { get; } = StbEditor.Core.ModGroups.Load(Panels.ModGroupsWindow.FilePath);

    /// <summary>The groups whose plugins are all loaded.</summary>
    public IReadOnlyList<StbEditor.Core.ModGroup> ActiveModGroups => Session is { } s ? StbEditor.Core.ModGroups.Active(ModGroupList, s.Workspace.Active.Select(p => p.Name)) : [];

    /// <summary>Edit → Find text.</summary>
    public Panels.FindTextWindow FindText { get; } = new();

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
        && (!Environment.GetCommandLineArgs().Contains("--autotest-picker") || _pickerStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-compare") || _compareStage == 3)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-savecheck") || _saveCheckStage == 2)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-ops") || _opsStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-filter") || _filterStage == 2)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-masters") || _mastersStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-useinfo") || _useStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-batch") || _batchStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-together") || _togetherStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-sheet") || _sheetStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-refmore") || _refMoreStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-conditions") || _condStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-quest") || _questStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-package") || _packStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-scene") || _sceneStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-aliasuse") || _aliasUseStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-texts") || _textsStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-navsplit") || _navSplitStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-comparefile") || _compareFileStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-levelmerge") || _mergeStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-bodytint") || _tintStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-heightmap") || _heightStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-vcolor") || _vcolorStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-palette") || _paletteStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-place") || _placeStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-facegen") || _faceGenStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-havok") || _havokStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-navgen") || _navGenStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-perktree") || _perkStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-idletree") || _idleStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-voice") || _voiceStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-sky") || _skyStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-region") || _regionStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-script") || _scriptStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-export") || _exportStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-face") || _faceStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-layers") || _layersStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-storymanager") || _smStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-fragments") || _fragStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-dialogue") || _dialStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-cellprops") || _cellPropsStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-finalize") || _finalizeStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-clean") || _cleanStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-bsa") || _bsaStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-compact") || _compactStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-comparecopy") || _compareCopyStage == 9)
        && (!Environment.GetCommandLineArgs().Contains("--autotest-findtext") || _findStage == 9);

    private int _findStage;

    /// <summary>--autotest-findtext [text]: what to look for (default Ulfric).</summary>
    private static string FindQuery => Environment.GetCommandLineArgs().SkipWhile(a => a != "--autotest-findtext").Skip(1).FirstOrDefault() is { } next && !next.StartsWith("--", StringComparison.Ordinal) ? next : "Ulfric";

    /// <summary>
    /// --autotest-findtext: "Ulfric" in every text field of Skyrim.esm (records, topics, dialogue lines) and over the
    /// catalog; every result replaced with "Ulfrik" in one step: a dialogue line and a record checked; undone; nothing saved.
    /// </summary>
    private void RunFindTextAutotest()
    {
        if (!Environment.GetCommandLineArgs().Contains("--autotest-findtext") || Session is not { } session || Loader is not null || _findStage == 9) return;
        switch (_findStage)
        {
            case 0:
                FindText.Show();
                FindText.Start(this, FindQuery, "Skyrim.esm", false);
                _findStage = 1;
                break;
            case 1 when !FindText.IsBusy && FindText.Hits is { } hits:
            {
                Log.Info($"autotest-findtext: {hits.Count} hit(s): {string.Join(", ", hits.GroupBy(h => h.Signature).OrderByDescending(g => g.Count()).Take(6).Select(g => $"{g.Key} {g.Count()}"))}");
                var before = Document.History.UndoName;
                var changed = Panels.FindTextWindow.ReplaceAll(this, hits, FindQuery, FindQuery + "X", false);
                var line = hits.FirstOrDefault(h => h.Signature == "INFO" && h.Display);
                var technical = hits.FirstOrDefault(h => h.PathText.EndsWith("VariableName", StringComparison.Ordinal));
                var record = hits.FirstOrDefault(h => h.Signature == "NPC_" && h.PathText == "EditorID");
                string Text(StbEditor.Core.TextHit? h) => h is null ? "-" : StbEditor.Core.OverrideEditing.Override(Document, h.Key)?.Record is { } r
                    ? StbEditor.Core.RecordFields.Get(r, h.Path) switch { string s => s, Mutagen.Bethesda.Strings.ITranslatedStringGetter t => t.String ?? "", _ => "?" } : "not edited";
                Log.Info($"autotest-findtext: {changed} record(s) changed, pending {Document.PendingCount}; line {line?.PathText}: '{Text(line)}'; NPC_ EditorID: '{Text(record)}'; place of the line {StbEditor.Core.OverrideEditing.Override(Document, line?.Key ?? default)?.Place}; script variable {technical?.PathText}: '{Text(technical)}'");
                var steps = 0;
                while (Document.History.UndoName != before && steps < 5) { Document.Undo(); steps++; }
                Log.Info($"autotest-findtext: {steps} undo step(s), pending {Document.PendingCount}; catalog search: {StbEditor.Core.TextSearch.InCatalog(session.Catalog, "Ulfric", false).Count} hit(s)");
                _findStage = 9;
                break;
            }
        }
    }

    private int _compareCopyStage;

    /// <summary>
    /// --autotest-comparecopy: a record with several versions; the oldest version's Keywords and Weight taken into the
    /// active file's version from the compare columns (one undo step each); three armors get one armor's weight
    /// ("copy to the other records"); all undone; nothing saved.
    /// </summary>
    private void RunCompareCopyAutotest()
    {
        if (!Environment.GetCommandLineArgs().Contains("--autotest-comparecopy") || Session is not { } session || Loader is not null || _compareCopyStage == 9) return;
        var lookup = new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog);
        var armor = session.Catalog.BySignature["ARMO"].First(r => r.Plugins.Count >= 3);
        var columns = Panels.CompareWindows.VersionColumns(this, armor.FormKey);
        string Keywords(Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter? r) => r is Mutagen.Bethesda.Skyrim.IArmorGetter a ? $"{a.Keywords?.Count ?? 0} kw, weight {a.Weight}" : "?";
        Log.Info($"autotest-comparecopy: {armor.EditorId}: columns [{string.Join(", ", columns.Select(c => c.Title + " " + Keywords(c.Record)))}]");
        var before = Document.History.UndoName;
        var oldest = 0; // columns run from the first plugin to the winner
        StbEditor.App.Ui.CompareView.CopyValue(this, columns, oldest, "Keywords", true);
        StbEditor.App.Ui.CompareView.CopyValue(this, columns, oldest, "Weight", true);
        Log.Info($"autotest-comparecopy: edited: {Keywords(StbEditor.Core.OverrideEditing.Override(Document, armor.FormKey)?.Record)} (oldest {Keywords(columns[oldest].Record)}); undo '{Document.History.UndoName}'");
        var three = new[] { "ArmorIronHelmet", "ArmorIronBoots", "ArmorIronGauntlets" }.Select(id => session.Catalog.BySignature["ARMO"].First(r => r.EditorId == id).FormKey).ToList();
        var recordColumns = three.Select(k => new StbEditor.App.Ui.CompareColumn(session.Catalog.Get(k)!.EditorId!, "", lookup.Resolve(k))).ToList();
        var copied = StbEditor.App.Ui.CompareView.CopyValue(this, recordColumns, 0, "Weight", false);
        Log.Info($"autotest-comparecopy: weight of {recordColumns[0].Title} into {copied} other(s): {string.Join(", ", three.Skip(1).Select(k => Keywords(StbEditor.Core.OverrideEditing.Override(Document, k)?.Record)))}");
        var steps = 0;
        while (Document.History.UndoName != before && steps < 10) { Document.Undo(); steps++; }
        Log.Info($"autotest-comparecopy: {steps} undo step(s), pending {Document.PendingCount}");
        _compareCopyStage = 9;
    }

    private int _compactStage;
    private string _compactCopy = "";

    /// <summary>
    /// --autotest-compact &lt;dir&gt; &lt;plugin&gt;: a copy of the plugin in &lt;dir&gt; is the active file; File → Compact FormIDs
    /// (light): backed up, replaced, reloaded; the copy's own records all in 0x800–0xFFF with the light flag.
    /// </summary>
    private void RunCompactAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-compact");
        if (arg < 0 || arg + 2 >= cli.Length || Session is not { } session || _compactStage == 9) return;
        switch (_compactStage)
        {
            case 0 when Loader is null:
            {
                var plugin = session.Workspace.Active.First(p => p.Name.Equals(cli[arg + 2], StringComparison.OrdinalIgnoreCase));
                _compactCopy = Path.Combine(cli[arg + 1], plugin.Name);
                File.Copy(plugin.Location.Path, _compactCopy, overwrite: true);
                var active = StbEditor.Core.ActivePlugin.ForExisting(plugin, session.Game).RetargetedTo(_compactCopy);
                SetActivePlugin(active);
                var plan = StbEditor.Core.FormIdCompactor.Plan(plugin.Mod!, plugin.ModKey, session.Workspace.Active);
                Log.Info($"autotest-compact: {plan.Own} own, {plan.Moved} to move, fits {plan.Fits}");
                Log.Info($"autotest-compact: {Panels.CompactWindow.Run(this, session, active, plan, plan.Fits, true)}");
                _compactStage = 1;
                break;
            }
            case 1 when Loader is null:
            {
                using var check = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(_compactCopy, session.Game.Release);
                var own = check.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == check.ModKey).ToList();
                Log.Info($"autotest-compact: file: flags {check.ModHeader.Flags}, own {own.Count}, outside 0x800-0xFFF {own.Count(r => r.FormKey.ID is < 0x800 or > 0xFFF)}, next {check.ModHeader.Stats.NextFormID:X}");
                _compactStage = 9;
                break;
            }
        }
    }

    private int _bsaStage;

    /// <summary>
    /// --autotest-bsa &lt;dir&gt;: a new plugin in &lt;dir&gt; is the active file; loose meshes, textures, a compiled script and a
    /// script source next to it; Pack into BSA: the main archive and the textures archive read back by Mutagen with the
    /// right files byte for byte, the source left out.
    /// </summary>
    private void RunArchiveAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-bsa");
        if (arg < 0 || arg + 1 >= cli.Length || Session is not { } session || Loader is not null || _bsaStage == 9) return;
        var dir = cli[arg + 1];
        switch (_bsaStage)
        {
            case 0:
            {
                var plugin = Path.Combine(dir, "STBEditorBsaAutotest.esp");
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(plugin, false, false, session.Game, session.Workspace.DefaultEncoding));
                var assets = new[] { @"meshes\clutter\barrel01.nif", @"meshes\clutter\bucket01.nif", @"textures\clutter\barrel01.dds", @"textures\clutter\barrel01_n.dds", @"scripts\objectreference.pex" };
                foreach (var asset in assets)
                {
                    if (session.Assets.ReadAllBytes(asset) is not { } bytes) { Log.Warn($"autotest-bsa: {asset} not found"); continue; }
                    var target = Path.Combine(dir, asset);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, bytes);
                }
                Directory.CreateDirectory(Path.Combine(dir, "scripts", "source"));
                File.WriteAllText(Path.Combine(dir, "scripts", "source", "Test.psc"), "Scriptname Test");
                var summary = Panels.ArchiveWindow.Pack(plugin, true);
                Log.Info($"autotest-bsa: {summary}");
                foreach (var archive in Directory.EnumerateFiles(dir, "*.bsa"))
                {
                    var reader = Mutagen.Bethesda.Archives.Archive.CreateReader(session.Game.GameRelease, new Noggog.FilePath(archive));
                    var files = reader.Files.ToList();
                    var same = files.Count(f => File.Exists(Path.Combine(dir, f.Path)) && f.GetBytes().AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dir, f.Path))));
                    Log.Info($"autotest-bsa: {Path.GetFileName(archive)}: {files.Count} file(s) [{string.Join(", ", files.Select(f => f.Path))}], {same} byte for byte");
                }
                _bsaStage = 9;
                break;
            }
        }
    }

    private int _cleanStage;
    private List<FormKey> _cleanItm = [];
    private string _cleanCopy = "";

    /// <summary>
    /// --autotest-clean &lt;dir&gt; &lt;plugin&gt;: a copy of the plugin in &lt;dir&gt; is the active file; Check and clean finds its errors,
    /// ITM and deleted references; the ITM are removed (one undo step, undone and redone) and saved into the copy; the copy
    /// must lack exactly those overrides and keep the rest.
    /// </summary>
    private void RunCleanAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-clean");
        if (arg < 0 || arg + 2 >= cli.Length || Session is not { } session || Loader is not null || _cleanStage == 9) return;
        switch (_cleanStage)
        {
            case 0:
            {
                var plugin = session.Workspace.Active.First(p => p.Name.Equals(cli[arg + 2], StringComparison.OrdinalIgnoreCase));
                _cleanCopy = Path.Combine(cli[arg + 1], plugin.Name);
                File.Copy(plugin.Location.Path, _cleanCopy, overwrite: true);
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForExisting(plugin, session.Game).RetargetedTo(_cleanCopy));
                PluginCheck.Show(plugin.Name);
                PluginCheck.Start(this, plugin.Name);
                _cleanStage = 1;
                break;
            }
            case 1 when !PluginCheck.IsBusy && PluginCheck.Result is { } result:
            {
                Log.Info($"autotest-clean: {result.Issues.Count(i => i.Error)} error(s), {result.Issues.Count(i => !i.Error)} warning(s), {result.Clean.Itm.Count} ITM, {result.Clean.DeletedReferences.Count} UDR");
                var removable = result.Clean.Itm.Where(i => i.Signature is not ("CELL" or "WRLD" or "DIAL")).ToList();
                _cleanItm = removable.Select(i => i.Key).ToList();
                Panels.PluginCheckWindow.RemoveItm(this, removable);
                var pending = Document.PendingCount;
                Document.Undo();
                var undone = Document.PendingCount;
                Document.Redo();
                Log.Info($"autotest-clean: removing {removable.Count} ITM: pending {pending}, after Ctrl+Z {undone}, after Ctrl+Y {Document.PendingCount}");
                if (result.Clean.DeletedReferences.Count > 0) Panels.PluginCheckWindow.FixDeleted(this, session, result.Clean.DeletedReferences);
                Save();
                _cleanStage = 2;
                break;
            }
            case 2 when !Saver.IsBusy && Document.PendingCount == 0 && Loader is null:
            {
                using var check = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(_cleanCopy, session.Game.Release);
                var present = check.EnumerateMajorRecords().Select(r => r.FormKey).ToHashSet();
                var original = session.Workspace.Active.First(p => p.Name.Equals(cli[arg + 2], StringComparison.OrdinalIgnoreCase));
                using var before = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(original.Location.Path, session.Game.Release);
                var beforeCount = before.EnumerateMajorRecords().Count();
                Log.Info($"autotest-clean: file had {beforeCount} records, now {present.Count}; ITM still present {_cleanItm.Count(present.Contains)} of {_cleanItm.Count}");
                _cleanStage = 9;
                break;
            }
        }
    }

    private int _finalizeStage;

    /// <summary>
    /// --autotest-finalize &lt;dir&gt; (with --cell of an interior with a load door): a new plugin in &lt;dir&gt; is the active file;
    /// Finalize cell on the untouched navmesh (door triangle as the CK's), then a triangle deleted (numbers shift) and
    /// Finalize edited: the door's XNDP follows; save, reload: navmesh, door and NAVI entry in the plugin agree.
    /// </summary>
    private void RunFinalizeAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-finalize");
        if (arg < 0 || arg + 1 >= cli.Length || Session is not { } session || Loader is not null || _finalizeStage == 9) return;
        var path = Path.Combine(cli[arg + 1], "STBEditorFinalizeAutotest.esp");
        string Doors(Mutagen.Bethesda.Skyrim.INavigationMeshGetter mesh) => string.Join(", ", mesh.Data!.DoorTriangles.Select(d => $"{d.TriangleBeforeDoor}->{d.Door.FormKey}"));
        switch (_finalizeStage)
        {
            case 0 when RenderWindow.IsSettled && RenderWindow.Scene is { IsInterior: true, Navmeshes.Count: > 0 } scene && session.Doors.IsCompletedSuccessfully:
            {
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(path, false, false, session.Game, session.Workspace.DefaultEncoding));
                var view = scene.Navmeshes[0];
                Log.Info($"autotest-finalize: {view.Key}: {view.Record.Data!.Triangles.Count} triangles, CK door triangles {Doors(view.Record)}");
                Log.Info($"autotest-finalize: cell: {RenderWindow.FinalizeNavmeshes(this, false)}");
                var after = (Mutagen.Bethesda.Skyrim.INavigationMeshGetter)StbEditor.Core.OverrideEditing.Override(Document, view.Key)!.Record;
                Log.Info($"autotest-finalize: after finalize: door triangles {Doors(after)}, pending {Document.PendingCount}");
                // Delete triangle 0 (not a door triangle): later numbers shift down.
                var copy = (Mutagen.Bethesda.Skyrim.NavigationMesh)StbEditor.Core.OverrideEditing.WorkingCopy(Document, view.Key, after);
                StbEditor.Core.NavmeshTools.DeleteTriangles(copy, new HashSet<int> { 0 });
                StbEditor.Core.OverrideEditing.CommitEdit(Document, "delete", copy, StbEditor.Core.RecordPlace.Navmesh, cell: view.Cell, world: null);
                Log.Info($"autotest-finalize: edited: {RenderWindow.FinalizeNavmeshes(this, true)}");
                var final = (Mutagen.Bethesda.Skyrim.INavigationMeshGetter)StbEditor.Core.OverrideEditing.Override(Document, view.Key)!.Record;
                var doors = final.Data!.DoorTriangles.Select(d => (d.Door.FormKey, (int)d.TriangleBeforeDoor)).ToList();
                var xndp = doors.Select(d => StbEditor.Core.OverrideEditing.Override(Document, d.Item1)?.Record as Mutagen.Bethesda.Skyrim.IPlacedObjectGetter)
                    .Select(r => r?.NavigationDoorLink is { } l ? $"{l.NavMesh.FormKey}/{l.TeleportMarkerTriangle}" : "unchanged");
                Log.Info($"autotest-finalize: door triangles {Doors(final)}; door XNDP {string.Join(", ", xndp)}; pending {Document.PendingCount}");
                Save();
                _finalizeStage = 1;
                break;
            }
            case 1 when !Saver.IsBusy && Document.PendingCount == 0 && Loader is null && RenderWindow.IsSettled:
            {
                using var check = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(path, session.Game.Release);
                var cells = check.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells).ToList();
                foreach (var nav in cells.SelectMany(c => c.NavigationMeshes))
                    Log.Info($"autotest-finalize: file navmesh {nav.FormKey}: {nav.Data?.Triangles.Count} triangles, door triangles {Doors(nav)}");
                foreach (var door in cells.SelectMany(c => c.Persistent.Concat(c.Temporary)).OfType<Mutagen.Bethesda.Skyrim.IPlacedObjectGetter>())
                    Log.Info($"autotest-finalize: file door {door.FormKey}: XNDP {door.NavigationDoorLink?.NavMesh.FormKey}/{door.NavigationDoorLink?.TeleportMarkerTriangle}");
                foreach (var navi in check.NavigationMeshInfoMaps)
                    foreach (var info in navi.MapInfos)
                        Log.Info($"autotest-finalize: file NAVI {navi.FormKey}: {info.NavigationMesh.FormKey} point {info.Point} parent {info.Parent?.GetType().Name} doors [{string.Join(", ", info.LinkedDoors.Select(l => $"{l.Unknown:X}:{l.Door.FormKey}"))}] unknown {info.Unknown:X} unknown2 {info.Unknown2:X}");
                _finalizeStage = 9;
                break;
            }
        }
    }

    private int _cellPropsStage, _cellPropsFrames;
    private FormKey _cellPropsCell, _cellPropsWorld;
    private System.Numerics.Vector3 _cellPropsAmbient;

    /// <summary>
    /// --autotest-cellprops &lt;dir&gt; (with --cell of an interior): a new plugin in &lt;dir&gt; is the active file; the cell's
    /// properties window; ambient light, music, owner, a flag and the name changed (the view's light follows), Tamriel's
    /// climate changed; save, reload: the plugin has both as headers only (no references), with the new values.
    /// </summary>
    private void RunCellPropsAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-cellprops");
        if (arg < 0 || arg + 1 >= cli.Length || Session is not { } session || Loader is not null || _cellPropsStage == 9) return;
        var path = Path.Combine(cli[arg + 1], "STBEditorCellPropsAutotest.esp");
        switch (_cellPropsStage)
        {
            case 0 when RenderWindow.IsSettled && RenderWindow.Scene is { IsInterior: true } scene:
            {
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(path, false, false, session.Game, session.Workspace.DefaultEncoding));
                _cellPropsCell = session.Catalog.BySignature["CELL"].First(c => c.EditorId == scene.Title).FormKey;
                _cellPropsWorld = session.Catalog.BySignature["WRLD"].First(w => w.EditorId == "Tamriel").FormKey;
                _cellPropsAmbient = scene.Lighting.Ambient;
                PlaceProperties.Open(_cellPropsCell);
                var lookup = new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog);
                var cell = lookup.CellVersions(_cellPropsCell).Last();
                var target = Panels.PlacePropertiesWindows.TargetFor(this, _cellPropsCell, cell);
                var music = session.Catalog.BySignature["MUSC"].First().FormKey;
                var faction = session.Catalog.BySignature["FACT"].First(f => f.EditorId == "PlayerFaction").FormKey;
                target.Apply("ambient", r => { var c = (Mutagen.Bethesda.Skyrim.Cell)r; c.Lighting ??= new Mutagen.Bethesda.Skyrim.CellLighting(); c.Lighting.AmbientColor = System.Drawing.Color.FromArgb(255, 200, 40, 40); });
                target.Apply("music", r => StbEditor.Core.RecordFields.Set(r, ["Music"], music));
                target.Apply("owner", r => StbEditor.Core.RecordFields.Set(r, ["Owner"], faction));
                target.Apply("flag", r => ((Mutagen.Bethesda.Skyrim.Cell)r).Flags |= Mutagen.Bethesda.Skyrim.Cell.Flag.PublicArea);
                target.Apply("name", r => StbEditor.Core.RecordFields.Set(r, ["Name"], "STB cell props test"));
                var world = lookup.Resolve<Mutagen.Bethesda.Skyrim.IWorldspaceGetter>(_cellPropsWorld)!;
                var climate = session.Catalog.BySignature["CLMT"].First(c => c.FormKey != world.Climate.FormKeyNullable).FormKey;
                Panels.PlacePropertiesWindows.TargetFor(this, _cellPropsWorld, world).Apply("climate", r => StbEditor.Core.RecordFields.Set(r, ["Climate"], climate));
                Log.Info($"autotest-cellprops: {scene.Title}: pending {Document.PendingCount}; header copy refs {((Mutagen.Bethesda.Skyrim.ICellGetter)StbEditor.Core.OverrideEditing.Override(Document, _cellPropsCell)!.Record).Temporary.Count}");
                _cellPropsStage = 1;
                break;
            }
            case 1 when ++_cellPropsFrames > 5:
                Log.Info($"autotest-cellprops: view ambient {_cellPropsAmbient} -> {RenderWindow.Scene?.Lighting.Ambient}");
                Save();
                _cellPropsStage = 2;
                break;
            case 2 when !Saver.IsBusy && Document.PendingCount == 0 && Loader is null && RenderWindow.IsSettled:
            {
                using (var check = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(path, session.Game.Release))
                {
                    var cell = check.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells).FirstOrDefault(c => c.FormKey == _cellPropsCell);
                    Log.Info(cell is null ? "autotest-cellprops: cell missing!" : $"autotest-cellprops: file cell '{cell.Name?.String}' flags {cell.Flags} ambient {cell.Lighting?.AmbientColor} music {cell.Music.FormKeyNullable} owner {cell.Owner.FormKeyNullable}; refs {cell.Temporary.Count}+{cell.Persistent.Count}");
                    var world = check.Worldspaces.FirstOrDefault(w => w.FormKey == _cellPropsWorld);
                    Log.Info(world is null ? "autotest-cellprops: world missing!" : $"autotest-cellprops: file world {world.EditorID} climate {world.Climate.FormKeyNullable}; sub-cell blocks {world.SubCells.Count}, persistent cell {(world.TopCell is null ? "none" : "present")}");
                }
                Log.Info($"autotest-cellprops: reloaded view ambient {RenderWindow.Scene?.Lighting.Ambient}");
                _cellPropsStage = 9;
                break;
            }
        }
    }

    private int _dialStage, _dialFrames;
    private FormKey _dialQuest, _dialTopic, _dialNewTopic, _dialNewLine;

    /// <summary>
    /// --autotest-dialogue &lt;dir&gt; (with --inspector-tab Dialogue): a new plugin in &lt;dir&gt; is the active file; DA01's
    /// Dialogue tab; a vanilla line edited (text, a second response, Goodbye, a condition), a new line, the topic's
    /// priority, a new branch with its topic and a line; save, reload: the topic holds only our lines, the new
    /// branch / topic / line are there and listed under the quest. Writes only into &lt;dir&gt;.
    /// </summary>
    private void RunDialogueAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-dialogue");
        if (arg < 0 || arg + 1 >= cli.Length || Session is not { } session || Loader is not null || _dialStage == 9) return;
        string Describe(FormKey topic) => string.Join(" | ", Panels.DialogueEditor.Lines(this, topic).Select(l => $"{l.Line.FormKey.ID:X6}:'{l.Line.Responses.FirstOrDefault()?.Text?.String}'x{l.Line.Responses.Count}{(l.Line.Flags?.Flags.HasFlag(Mutagen.Bethesda.Skyrim.DialogResponses.Flag.Goodbye) == true ? "+bye" : "")}/c{l.Line.Conditions.Count}@{l.Plugin}").Take(6));
        switch (_dialStage)
        {
            case 0 when session.Dialogue.IsCompletedSuccessfully:
            {
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[arg + 1], "STBEditorDialogueAutotest.esp"), false, false, session.Game, session.Workspace.DefaultEncoding));
                _dialQuest = session.Catalog.BySignature["QUST"].First(r => r.EditorId == "DA01").FormKey;
                _dialTopic = session.Dialogue.Result.TopicsOf(_dialQuest).First(t => Panels.DialogueEditor.Lines(this, t).Count >= 2);
                FormWindows.Open(_dialQuest);
                Panels.DialogueEditor.SelectForTest(_dialQuest, _dialTopic);
                Log.Info($"autotest-dialogue: DA01: {session.Dialogue.Result.TopicsOf(_dialQuest).Count} topics, {session.Dialogue.Result.BranchesOf(_dialQuest).Count} branches; topic {session.Catalog.Get(_dialTopic)?.EditorId}: {Describe(_dialTopic)}");
                _dialStage = 1;
                break;
            }
            case 1 when ++_dialFrames > 15:
            {
                var lines = Panels.DialogueEditor.Lines(this, _dialTopic);
                var line = lines[0].Line;
                var target = new Ui.EditTarget(() => StbEditor.Core.OverrideEditing.WorkingCopy(Document, line.FormKey, Panels.DialogueEditor.Lines(this, _dialTopic)[0].Line),
                    (name, edited) => StbEditor.Core.OverrideEditing.CommitEdit(Document, name, edited, StbEditor.Core.RecordPlace.DialogInfo, cell: _dialTopic));
                target.Apply("text", r => StbEditor.Core.RecordFields.Set(r, ["Responses", 0, "Text"], "STB autotest line"));
                target.Apply("response", r => ((Mutagen.Bethesda.Skyrim.DialogResponses)r).Responses.Add(new Mutagen.Bethesda.Skyrim.DialogResponse { ResponseNumber = 2, Text = "second", Emotion = Mutagen.Bethesda.Skyrim.Emotion.Anger, EmotionValue = 70 }));
                target.Apply("goodbye", r => StbEditor.Core.RecordFields.Set(r, ["Flags", "Flags"], (line.Flags?.Flags ?? 0) | Mutagen.Bethesda.Skyrim.DialogResponses.Flag.Goodbye));
                target.Apply("condition", r => StbEditor.Core.Conditions.ListAt(r, ["Conditions"]).Add(StbEditor.Core.Conditions.New()));
                var topic = (Mutagen.Bethesda.Skyrim.IDialogTopicGetter)StbEditor.Core.OverrideEditing.WorkingCopy(Document, _dialTopic, new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog).Resolve<Mutagen.Bethesda.Skyrim.IDialogTopicGetter>(_dialTopic)!);
                ((Mutagen.Bethesda.Skyrim.DialogTopic)topic).Priority = 61;
                StbEditor.Core.OverrideEditing.CommitEdit(Document, "priority", (Mutagen.Bethesda.Plugins.Records.IMajorRecord)topic, StbEditor.Core.RecordPlace.TopLevel);
                Panels.DialogueEditor.NewLine(this, topic, Panels.DialogueEditor.Lines(this, _dialTopic));
                var quest = new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog).Resolve<Mutagen.Bethesda.Skyrim.IQuestGetter>(_dialQuest)!;
                var branchKey = Panels.DialogueEditor.NewBranch(this, quest)!.Value;
                _dialNewTopic = Document.Drafts.Values.OfType<StbEditor.Core.OverrideDraft>().Select(d => d.Record).OfType<Mutagen.Bethesda.Skyrim.IDialogTopicGetter>()
                    .First(x => x.Branch.FormKeyNullable == branchKey).FormKey;
                var newTopic = (Mutagen.Bethesda.Skyrim.IDialogTopicGetter)StbEditor.Core.OverrideEditing.Override(Document, _dialNewTopic)!.Record;
                _dialNewLine = Panels.DialogueEditor.NewLine(this, newTopic, []) ?? default;
                var newTarget = new Ui.EditTarget(() => StbEditor.Core.OverrideEditing.WorkingCopy(Document, _dialNewLine, (Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter)StbEditor.Core.OverrideEditing.Override(Document, _dialNewLine)!.Record),
                    (name, edited) => StbEditor.Core.OverrideEditing.CommitEdit(Document, name, edited, StbEditor.Core.RecordPlace.DialogInfo, cell: _dialNewTopic));
                newTarget.Apply("text", r => StbEditor.Core.RecordFields.Set(r, ["Responses", 0, "Text"], "Hello from a new branch"));
                Panels.DialogueEditor.SetFragmentsForTest(_dialNewLine, null, "akSpeaker.SetRelationshipRank(Game.GetPlayer(), 1)");
                Panels.DialogueEditor.StartCompile(this, (Mutagen.Bethesda.Skyrim.IDialogResponsesGetter)StbEditor.Core.OverrideEditing.Override(Document, _dialNewLine)!.Record, newTarget);
                _dialStage = 10;
                break;
            }
            case 10 when !Panels.DialogueEditor.IsCompiling:
            {
                var newLine = (Mutagen.Bethesda.Skyrim.IDialogResponsesGetter)StbEditor.Core.OverrideEditing.Override(Document, _dialNewLine)!.Record;
                var fragments = newLine.VirtualMachineAdapter?.ScriptFragments;
                Log.Info($"autotest-dialogue: end fragment: compiled {Panels.DialogueEditor.LastResultFor(_dialNewLine)?.Success}, file {fragments?.FileName}, end {fragments?.OnEnd?.FragmentName}, begin {fragments?.OnBegin?.FragmentName ?? "none"}, scripts [{string.Join(", ", newLine.VirtualMachineAdapter?.Scripts.Select(x => x.Name) ?? [])}]");
                Log.Info($"autotest-dialogue: edited topic: {Describe(_dialTopic)}; new topic {session.Catalog.Get(_dialNewTopic)?.EditorId}: {Describe(_dialNewTopic)}; pending {Document.PendingCount}");
                Save();
                _dialStage = 2;
                break;
            }
            case 2 when !Saver.IsBusy && Document.PendingCount == 0 && Loader is null && session.Dialogue.IsCompletedSuccessfully:
            {
                var path = Path.Combine(cli[arg + 1], "STBEditorDialogueAutotest.esp");
                using (var check = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(path, session.Game.Release))
                {
                    foreach (var t in check.DialogTopics)
                        Log.Info($"autotest-dialogue: file topic {t.EditorID} priority {t.Priority} branch {t.Branch.FormKeyNullable}: {t.Responses.Count} line(s): {string.Join(" | ", t.Responses.Select(r => $"'{r.Responses.FirstOrDefault()?.Text?.String}'x{r.Responses.Count}"))}");
                    Log.Info($"autotest-dialogue: file branches: {string.Join(", ", check.DialogBranches.Select(b => $"{b.EditorID} start {b.StartingTopic.FormKeyNullable}"))}");
                }
                Log.Info($"autotest-dialogue: reloaded: DA01 has {session.Dialogue.Result.TopicsOf(_dialQuest).Count} topics, {session.Dialogue.Result.BranchesOf(_dialQuest).Count} branches; topic: {Describe(_dialTopic)}");
                FormWindows.Open(_dialQuest);
                Panels.DialogueEditor.SelectForTest(_dialQuest, _dialTopic);
                _dialStage = 3;
                _dialFrames = 0;
                break;
            }
            case 3 when ++_dialFrames > 20:
                _dialStage = 9;
                break;
        }
    }

    private int _fragStage, _fragFrames;
    private FormKey _fragQuest;

    /// <summary>
    /// --autotest-fragments &lt;dir&gt;: a new plugin in &lt;dir&gt; is the active file; a new quest (Start Game Enabled) with an
    /// alias and a stage; a broken fragment must fail with its line, a good one compiles (QF_ script next to the
    /// plugin), the quest's script data gets the fragment and the Alias_ property; save (with the SEQ file), reload,
    /// check. Nothing is written outside &lt;dir&gt;.
    /// </summary>
    private void RunFragmentsAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-fragments");
        if (arg < 0 || arg + 1 >= cli.Length || Session is not { } session || Loader is not null || _fragStage == 9) return;
        var lookup = new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog);
        Mutagen.Bethesda.Skyrim.IQuestGetter? Quest() => (Mutagen.Bethesda.Skyrim.IQuestGetter?)StbEditor.Core.OverrideEditing.Override(Document, _fragQuest)?.Record
            ?? lookup.Resolve<Mutagen.Bethesda.Skyrim.IQuestGetter>(_fragQuest);
        switch (_fragStage)
        {
            case 0:
            {
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[arg + 1], "STBEditorFragmentAutotest.esp"), false, false, session.Game, session.Workspace.DefaultEncoding));
                _fragQuest = NewForm.CreateForTest("QUST", 0, "STBFragTestQuest", "STB fragment test") ?? default;
                if (_fragQuest.IsNull) { Log.Warn("autotest-fragments: quest not created"); _fragStage = 9; return; }
                FormWindows.Open(_fragQuest);
                _fragStage = 1;
                break;
            }
            case 1 when ++_fragFrames > 10 && FormWindows.FormOf(_fragQuest)?.Target is { } target:
            {
                target.Apply("setup", r =>
                {
                    var q = (Mutagen.Bethesda.Skyrim.Quest)r;
                    q.Flags |= Mutagen.Bethesda.Skyrim.Quest.Flag.StartGameEnabled;
                    q.Aliases.Add(new Mutagen.Bethesda.Skyrim.QuestAlias { ID = 0, Name = "PlayerAlias", Type = Mutagen.Bethesda.Skyrim.QuestAlias.TypeEnum.Reference });
                    q.Aliases[0].ForcedReference.SetTo(new FormKey(Mutagen.Bethesda.Skyrim.Constants.Skyrim, 0x14));
                    q.NextAliasID = 1;
                    var stage = new Mutagen.Bethesda.Skyrim.QuestStage { Index = 10, Flags = Mutagen.Bethesda.Skyrim.QuestStage.Flag.StartUpStage };
                    stage.LogEntries.Add(new Mutagen.Bethesda.Skyrim.QuestLogEntry());
                    q.Stages.Add(stage);
                });
                Panels.QuestEditor.SetFragmentForTest(_fragQuest, 10, 0, "Debug.Notification(NoSuchVariable)");
                Panels.QuestEditor.StartCompile(this, Quest()!, target);
                _fragStage = 2;
                break;
            }
            case 2 when !Panels.QuestEditor.IsCompiling && Panels.QuestEditor.LastCompile is { } broken:
            {
                Log.Info($"autotest-fragments: broken code: success {broken.Success}, {broken.Errors} error(s); fragments in quest: {Quest()!.VirtualMachineAdapter?.Fragments.Count ?? 0}");
                Panels.QuestEditor.SetFragmentForTest(_fragQuest, 10, 0, "Debug.Notification(\"STB fragment test\")\nAlias_PlayerAlias.GetReference().AddItem(Game.GetForm(0xF) as MiscObject, 1)");
                Panels.QuestEditor.StartCompile(this, Quest()!, FormWindows.FormOf(_fragQuest)!.Target!);
                _fragStage = 3;
                break;
            }
            case 3 when !Panels.QuestEditor.IsCompiling:
            {
                var q = Quest()!;
                var vmad = q.VirtualMachineAdapter;
                var good = Panels.QuestEditor.LastCompile;
                Log.Info($"autotest-fragments: good code: success {good?.Success}, errors {good?.Errors}; file {vmad?.FileName}, fragments [{string.Join(", ", vmad?.Fragments.Select(f => $"{f.Stage}/{f.StageIndex}->{f.FragmentName}") ?? [])}], properties [{string.Join(", ", vmad?.Scripts.SelectMany(s => s.Properties).Select(p => $"{p.Name}={(p as Mutagen.Bethesda.Skyrim.IScriptObjectPropertyGetter)?.Alias}") ?? [])}]");
                var pex = Directory.EnumerateFiles(cli[arg + 1], "*.pex", SearchOption.AllDirectories).Select(Path.GetFileName);
                Log.Info($"autotest-fragments: pex files: {string.Join(", ", pex)}; library sees it: {Panels.ScriptsEditor.Library?.Get(vmad?.FileName ?? "") is not null || session.Assets.Exists($@"scripts\{vmad?.FileName}.pex")}");
                // A new script through the source window, attached to the quest once it compiles.
                var formTarget = FormWindows.FormOf(_fragQuest)!.Target!;
                ScriptSources.Open(this, "STBEditorTestScript", "Quest", () => formTarget.Apply("Add script STBEditorTestScript",
                    r => ((Mutagen.Bethesda.Skyrim.Quest)r).VirtualMachineAdapter!.Scripts.Add(new Mutagen.Bethesda.Skyrim.ScriptEntry { Name = "STBEditorTestScript" })));
                ScriptSources.CompileForTest(this, "Scriptname STBEditorTestScript extends Quest\r\n\r\nInt Property Count Auto\r\n\r\nEvent OnInit()\r\n    Debug.Trace(\"STB test \" + Count)\r\nEndEvent\r\n");
                _fragStage = 5;
                break;
            }
            case 5 when !ScriptSources.IsBusy:
            {
                var scripts = Quest()!.VirtualMachineAdapter?.Scripts.Select(s => s.Name) ?? [];
                Log.Info($"autotest-fragments: new script: compiled {ScriptSources.LastResult?.Success} ({ScriptSources.LastResult?.Errors.Count} errors); quest scripts [{string.Join(", ", scripts)}]; property Count seen: {Panels.ScriptsEditor.Library is null || new StbEditor.Assets.ScriptLibrary(session.Assets).AllProperties("STBEditorTestScript").Any(p => p.Property.Name == "Count")}");
                Save();
                _fragStage = 4;
                break;
            }
            case 4 when !Saver.IsBusy && Document.PendingCount == 0 && Loader is null:
            {
                var q = Quest();
                var seq = StbEditor.Data.Raw.SeqFile.PathFor(Path.Combine(cli[arg + 1], "STBEditorFragmentAutotest.esp"));
                Log.Info($"autotest-fragments: reloaded: quest {q?.EditorID}, start game {q?.Flags.HasFlag(Mutagen.Bethesda.Skyrim.Quest.Flag.StartGameEnabled)}, fragments {q?.VirtualMachineAdapter?.Fragments.Count}, fragment script {q?.VirtualMachineAdapter?.FileName}, scripts [{string.Join(", ", q?.VirtualMachineAdapter?.Scripts.Select(s => s.Name) ?? [])}]; SEQ {(File.Exists(seq) ? new FileInfo(seq).Length + " bytes" : "missing")}");
                _fragStage = 9;
                break;
            }
        }
    }

    private int _questStage, _questFrames;
    private FormKey _quest;

    /// <summary>
    /// --autotest-quest [EditorID] (with --inspector-tab "Quest Stages" / "Quest Objectives" / "Quest Aliases"): a quest's form;
    /// through its edit target a stage with a log entry and a condition, a stage index change (list stays sorted), an
    /// objective with a target alias, a reference alias filled with a specific reference; the quest written and read back;
    /// all undone and redone (the screenshot shows the edits); nothing saved.
    /// </summary>
    private void RunQuestAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-quest");
        if (arg < 0 || Session is not { } session || Loader is not null || _questStage == 9) return;
        var lookup = new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog);
        Mutagen.Bethesda.Skyrim.IQuestGetter? Quest() => (Mutagen.Bethesda.Skyrim.IQuestGetter?)StbEditor.Core.OverrideEditing.Override(Document, _quest)?.Record
            ?? lookup.Resolve<Mutagen.Bethesda.Skyrim.IQuestGetter>(_quest);
        string Summary(Mutagen.Bethesda.Skyrim.IQuestGetter q) =>
            $"stages [{string.Join(",", q.Stages.Select(s => s.Index))}], objectives [{string.Join(",", q.Objectives.Select(o => o.Index))}], aliases [{string.Join(",", q.Aliases.Select(a => $"{a.ID}:{a.Name}"))}], next alias {q.NextAliasID}";
        switch (_questStage)
        {
            case 0:
            {
                var wanted = arg + 1 < cli.Length && !cli[arg + 1].StartsWith("--", StringComparison.Ordinal) ? cli[arg + 1] : null;
                _quest = (wanted is not null
                    ? session.Catalog.BySignature["QUST"].First(r => string.Equals(r.EditorId, wanted, StringComparison.OrdinalIgnoreCase))
                    : session.Catalog.BySignature["QUST"].First(r => lookup.Resolve<Mutagen.Bethesda.Skyrim.IQuestGetter>(r.FormKey) is { Stages.Count: >= 3, Objectives.Count: >= 1, Aliases.Count: >= 2 })).FormKey;
                FormWindows.Open(_quest);
                Log.Info($"autotest-quest: {session.Catalog.Get(_quest)?.EditorId}: {Summary(Quest()!)}");
                _questStage = 1;
                break;
            }
            case 1 when ++_questFrames > 15 && FormWindows.FormOf(_quest)?.Target is { } target:
            {
                var before = Document.History.UndoName;
                var q = Quest()!;
                var newIndex = q.Stages.Max(s => s.Index) / 10 * 10 + 10;
                target.Apply("add stage", r =>
                {
                    var list = (System.Collections.IList)StbEditor.Core.RecordFields.Get(r, ["Stages"])!;
                    list.Add(new Mutagen.Bethesda.Skyrim.QuestStage { Index = (ushort)newIndex, Flags = Mutagen.Bethesda.Skyrim.QuestStage.Flag.ShutDownStage });
                });
                var last = Quest()!.Stages.Count - 1;
                target.Apply("log entry", r => StbEditor.Core.RecordFields.AddItem(r, ["Stages", last, "LogEntries"]));
                target.Apply("entry text", r => StbEditor.Core.RecordFields.Set(r, ["Stages", last, "LogEntries", 0, "Entry"], "STB Editor test entry"));
                target.Apply("entry condition", r => StbEditor.Core.Conditions.ListAt(r, ["Stages", last, "LogEntries", 0, "Conditions"]).Add(StbEditor.Core.Conditions.New()));
                var firstIndex = Quest()!.Stages[0].Index;
                target.Apply("stage index", r =>
                {
                    StbEditor.Core.RecordFields.Set(r, ["Stages", 0, "Index"], (ushort)(newIndex + 5));
                    var list = (System.Collections.IList)StbEditor.Core.RecordFields.Get(r, ["Stages"])!;
                    var sorted = list.Cast<Mutagen.Bethesda.Skyrim.QuestStage>().OrderBy(s => s.Index).ToList();
                    list.Clear();
                    foreach (var s in sorted) list.Add(s);
                });
                var aliasId = (int)Quest()!.Aliases[0].ID;
                target.Apply("objective", r => ((Mutagen.Bethesda.Skyrim.Quest)r).Objectives.Add(new Mutagen.Bethesda.Skyrim.QuestObjective { Index = 999, DisplayText = "STB test objective" }));
                var objective = Quest()!.Objectives.Count - 1;
                target.Apply("objective target", r => { StbEditor.Core.RecordFields.AddItem(r, ["Objectives", objective, "Targets"]); ((Mutagen.Bethesda.Skyrim.Quest)r).Objectives[objective].Targets[^1].AliasID = aliasId; });
                var id = Math.Max(Quest()!.NextAliasID ?? 0, Quest()!.Aliases.Max(a => a.ID) + 1);
                target.Apply("alias", r =>
                {
                    var quest = (Mutagen.Bethesda.Skyrim.Quest)r;
                    var alias = new Mutagen.Bethesda.Skyrim.QuestAlias { ID = id, Name = $"STBTestAlias{id}", Type = Mutagen.Bethesda.Skyrim.QuestAlias.TypeEnum.Reference };
                    alias.ForcedReference.SetTo(new FormKey(Mutagen.Bethesda.Skyrim.Constants.Skyrim, 0x14));
                    alias.Flags = Mutagen.Bethesda.Skyrim.QuestAlias.Flag.Optional | Mutagen.Bethesda.Skyrim.QuestAlias.Flag.QuestObject;
                    quest.Aliases.Add(alias);
                    quest.NextAliasID = id + 1;
                });
                var edited = Quest()!;
                Log.Info($"autotest-quest: edited: {Summary(edited)}; stage {newIndex} entry '{edited.Stages.First(s => s.Index == newIndex).LogEntries[0].Entry?.String}' with {edited.Stages.First(s => s.Index == newIndex).LogEntries[0].Conditions.Count} condition(s); first stage {firstIndex} now {newIndex + 5}");
                var back = StbEditor.Core.RecordRoundTrip.Read(edited, session.Game.Release, System.Text.Encoding.UTF8) as Mutagen.Bethesda.Skyrim.IQuestGetter;
                Log.Info(back is null ? $"autotest-quest: round trip FAILED: {StbEditor.Core.RecordRoundTrip.LastError}" : $"autotest-quest: round trip: {Summary(back)}; alias fill {back.Aliases[^1].ForcedReference.FormKey}");
                var steps = 0;
                while (Document.History.UndoName != before && steps < 30) { Document.Undo(); steps++; }
                Log.Info($"autotest-quest: {steps} undo steps: pending {Document.PendingCount}: {Summary(Quest()!)}");
                for (var i = 0; i < steps; i++) Document.Redo();
                StbEditor.App.Panels.QuestEditor.SelectForTest(_quest, "Stages", cli.Skip(arg + 1).FirstOrDefault() is { } named && !named.StartsWith("--", StringComparison.Ordinal) ? 0 : Quest()!.Stages.Count - 2);
                StbEditor.App.Panels.QuestEditor.SelectForTest(_quest, "Objectives", Quest()!.Objectives.Count - 1);
                StbEditor.App.Panels.QuestEditor.SelectForTest(_quest, "Aliases", Quest()!.Aliases.Count - 1);
                Log.Info($"autotest-quest: redone: pending {Document.PendingCount}");
                _questStage = 2;
                _questFrames = 0;
                break;
            }
            case 2 when ++_questFrames > 15:
                _questStage = 9;
                break;
        }
    }

    private int _packStage, _packFrames;
    private FormKey _pack;

    /// <summary>
    /// --autotest-package &lt;dir&gt; [EditorID] (with --inspector-tab "Package Data" / "Schedule" / "Procedure Tree"): a new
    /// package in a scratch active file (&lt;dir&gt;) gets the Sandbox template through the form's template picker logic,
    /// a schedule, an input value, a location kind and the Wear Sleep Outfit flag; read back; undone and redone. With an
    /// EditorID that package's form opens too (the screenshot shows it). Nothing is saved.
    /// </summary>
    private void RunPackageAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-package");
        if (arg < 0 || arg + 1 >= cli.Length || Session is not { } session || Loader is not null || _packStage == 9) return;
        var lookup = new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog);
        Mutagen.Bethesda.Skyrim.IPackageGetter? Pack() => (Mutagen.Bethesda.Skyrim.IPackageGetter?)StbEditor.Core.OverrideEditing.Override(Document, _pack)?.Record;
        string Summary(Mutagen.Bethesda.Skyrim.IPackageGetter p) =>
            $"type {p.Type}, template {p.PackageTemplate.FormKey}, flags 0x{(uint)p.Flags:X}, schedule '{StbEditor.App.Panels.PackageEditor.Summary(p)}', inputs [" +
            string.Join(", ", StbEditor.Core.Packages.Inputs(p, StbEditor.App.Panels.PackageEditor.Template(this, p)).Select(i => $"{i.Key} {i.Name} {StbEditor.Core.Packages.KindOf(i.Data)}" +
                (i.Data is Mutagen.Bethesda.Skyrim.IPackageDataBoolGetter b ? $"={b.Data}" : i.Data is Mutagen.Bethesda.Skyrim.IPackageDataLocationGetter l ? $"={l.Location?.Target.GetType().Name.Replace("BinaryOverlay", "")}" : ""))) + "]";
        switch (_packStage)
        {
            case 0:
            {
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[arg + 1], "STBEditorPackageAutotest.esp"), false, false, session.Game, session.Workspace.DefaultEncoding));
                _pack = NewForm.CreateForTest("PACK", 0, "STBTestPackage", "") ?? default;
                if (_pack.IsNull) { Log.Warn("autotest-package: package not created"); _packStage = 9; return; }
                FormWindows.Open(_pack);
                Log.Info($"autotest-package: new: {Summary(Pack()!)}");
                _packStage = 1;
                break;
            }
            case 1 when ++_packFrames > 10 && FormWindows.FormOf(_pack)?.Target is { } target:
            {
                var before = Document.History.UndoName;
                StbEditor.App.Panels.PackageEditor.SetTemplate(this, target, Mutagen.Bethesda.Plugins.FormKey.Factory("01C254:Skyrim.esm"));
                Log.Info($"autotest-package: template: {Summary(Pack()!)}");
                target.Apply("schedule", r =>
                {
                    StbEditor.Core.RecordFields.Set(r, ["ScheduleDayOfWeek"], Mutagen.Bethesda.Skyrim.Package.DayOfWeek.Weekdays);
                    StbEditor.Core.RecordFields.Set(r, ["ScheduleHour"], (sbyte)8);
                    StbEditor.Core.RecordFields.Set(r, ["ScheduleMinute"], (sbyte)30);
                    StbEditor.Core.RecordFields.Set(r, ["ScheduleDurationInMinutes"], 600);
                });
                target.Apply("input", r => StbEditor.Core.RecordFields.Set(r, ["Data", (sbyte)1, "Data"], true));
                target.Apply("location kind", r =>
                {
                    var near = new Mutagen.Bethesda.Skyrim.LocationTarget();
                    near.Link.SetTo(FormField.PlayerRef);
                    StbEditor.Core.RecordFields.Set(r, ["Data", (sbyte)0, "Location", "Target"], near);
                });
                var bit = StbEditor.Core.RecordFields.FlagBits(typeof(Mutagen.Bethesda.Skyrim.Package.Flag)).First(f => f.Name == "WearSleepOutfit").Bit;
                target.Apply("flag", r => StbEditor.Core.RecordFields.Set(r, ["Flags"], Enum.ToObject(typeof(Mutagen.Bethesda.Skyrim.Package.Flag), bit)));
                var edited = Pack()!;
                Log.Info($"autotest-package: edited: {Summary(edited)}");
                var back = StbEditor.Core.RecordRoundTrip.Read(edited, session.Game.Release, System.Text.Encoding.UTF8) as Mutagen.Bethesda.Skyrim.IPackageGetter;
                Log.Info(back is null ? $"autotest-package: round trip FAILED: {StbEditor.Core.RecordRoundTrip.LastError}" : $"autotest-package: round trip: {Summary(back)}");
                var steps = 0;
                while (Document.History.UndoName != before && steps < 30) { Document.Undo(); steps++; }
                Log.Info($"autotest-package: {steps} undo steps: {Summary(Pack()!)}");
                for (var i = 0; i < steps; i++) Document.Redo();
                Log.Info($"autotest-package: redone: {Summary(Pack()!)}");
                if (arg + 2 < cli.Length && !cli[arg + 2].StartsWith("--", StringComparison.Ordinal)
                    && session.Catalog.BySignature["PACK"].FirstOrDefault(r => string.Equals(r.EditorId, cli[arg + 2], StringComparison.OrdinalIgnoreCase)) is { } shown)
                {
                    FormWindows.Open(shown.FormKey);
                    StbEditor.App.Panels.PackageEditor.SelectForTest(shown.FormKey, 1);
                    if (lookup.Resolve<Mutagen.Bethesda.Skyrim.IPackageGetter>(shown.FormKey) is { } p) Log.Info($"autotest-package: {shown.EditorId}: {Summary(p)}");
                }
                // A Begin fragment, compiled next to the scratch file; the package then points at it.
                StbEditor.App.Panels.FragmentsPanel.SetForTest(_pack, "Begin", "Debug.Trace(\"STB autotest package begin \" + akActor)");
                StbEditor.App.Panels.FragmentsPanel.StartCompile(this, Pack()!, target);
                _packStage = 2;
                _packFrames = 0;
                break;
            }
            case 2 when !StbEditor.App.Panels.FragmentsPanel.IsCompiling:
            {
                var result = StbEditor.App.Panels.FragmentsPanel.LastResultFor(_pack);
                var vmad = Pack()!.VirtualMachineAdapter;
                Log.Info($"autotest-package: fragments compile {(result?.Success == true ? "ok" : "FAILED " + result?.Output)}; script {vmad?.ScriptFragments?.FileName}, begin {vmad?.ScriptFragments?.OnBegin?.FragmentName}, scripts [{string.Join(",", vmad?.Scripts.Select(x => x.Name) ?? [])}]");
                _packStage = 3;
                break;
            }
            case 3 when ++_packFrames > 15:
                _packStage = 9;
                break;
        }
    }

    private int _sceneStage, _sceneFrames;
    private FormKey _scene;

    /// <summary>
    /// --autotest-scene &lt;dir&gt; [EditorID] (with --inspector-tab "Phases and Actions" / "Actors" / "Scene"): a new scene in a
    /// scratch active file (&lt;dir&gt;) for dunFellglow_ReleaseDogs' quest: two actors, two phases, a dialogue action with a new
    /// scene topic and a line, a package and a timer action, a phase inserted in front; read back; undone and redone. With an
    /// EditorID that scene opens too with its first action selected. Nothing is saved.
    /// </summary>
    private void RunSceneAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-scene");
        if (arg < 0 || arg + 1 >= cli.Length || Session is not { } session || Loader is not null || _sceneStage == 9) return;
        var lookup = new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog);
        Mutagen.Bethesda.Skyrim.ISceneGetter? Scene() => (Mutagen.Bethesda.Skyrim.ISceneGetter?)StbEditor.Core.OverrideEditing.Override(Document, _scene)?.Record;
        string Summary(Mutagen.Bethesda.Skyrim.ISceneGetter s) =>
            $"quest {s.Quest.FormKey}, actors [{string.Join(",", s.Actors.Select(a => $"{a.ID}:{a.BehaviorFlags}"))}], phases {s.Phases.Count}, actions [" +
            string.Join(", ", s.Actions.Select(a => $"{a.Index} {a.Type} actor {a.ActorID} {a.StartPhase}-{a.EndPhase} topic {a.Topic.FormKeyNullable}")) + $"], last {s.LastActionIndex}";
        switch (_sceneStage)
        {
            case 0:
            {
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[arg + 1], "STBEditorSceneAutotest.esp"), false, false, session.Game, session.Workspace.DefaultEncoding));
                _scene = NewForm.CreateForTest("SCEN", 0, "STBTestScene", "") ?? default;
                if (_scene.IsNull) { Log.Warn("autotest-scene: scene not created"); _sceneStage = 9; return; }
                FormWindows.Open(_scene);
                Log.Info($"autotest-scene: new: {Summary(Scene()!)}");
                _sceneStage = 1;
                break;
            }
            case 1 when ++_sceneFrames > 10 && FormWindows.FormOf(_scene)?.Target is { } target:
            {
                var before = Document.History.UndoName;
                var model = lookup.Resolve<Mutagen.Bethesda.Skyrim.ISceneGetter>(Mutagen.Bethesda.Plugins.FormKey.Factory("08A372:Skyrim.esm"))!;
                var quest = lookup.Resolve<Mutagen.Bethesda.Skyrim.IQuestGetter>(model.Quest.FormKey)!;
                var ids = quest.Aliases.Select(a => (int)a.ID).Take(2).ToList();
                target.Apply("quest", r => StbEditor.Core.RecordFields.Set(r, ["Quest"], quest.FormKey));
                foreach (var id in ids) target.Apply("actor", r => StbEditor.Core.Scenes.AddActor((Mutagen.Bethesda.Skyrim.Scene)r, id));
                target.Apply("phase", r => StbEditor.Core.Scenes.InsertPhase((Mutagen.Bethesda.Skyrim.Scene)r, 0));
                target.Apply("phase", r => StbEditor.Core.Scenes.InsertPhase((Mutagen.Bethesda.Skyrim.Scene)r, 1));
                target.Apply("dialogue", r => StbEditor.Core.Scenes.AddAction((Mutagen.Bethesda.Skyrim.Scene)r, Mutagen.Bethesda.Skyrim.SceneAction.TypeEnum.Dialog, ids[0], 0));
                target.Apply("package", r =>
                {
                    var action = StbEditor.Core.Scenes.AddAction((Mutagen.Bethesda.Skyrim.Scene)r, Mutagen.Bethesda.Skyrim.SceneAction.TypeEnum.Package, ids[^1], 0);
                    action.EndPhase = 1;
                    action.Packages.Add(new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.IPackageGetter>(model.Actions.First(a => a.Packages.Count > 0).Packages[0].FormKey));
                });
                target.Apply("timer", r => StbEditor.Core.Scenes.AddAction((Mutagen.Bethesda.Skyrim.Scene)r, Mutagen.Bethesda.Skyrim.SceneAction.TypeEnum.Timer, ids[0], 1));
                // The editor's own «+ scene topic»: a topic of the quest, set on the dialogue action, then a line in it.
                var topic = new Mutagen.Bethesda.Skyrim.DialogTopic(ActivePlugin!.Allocate(), session.Game.Release)
                {
                    EditorID = "STBTestSceneTopic", Priority = 50, Category = Mutagen.Bethesda.Skyrim.DialogTopic.CategoryEnum.Scene, Subtype = Mutagen.Bethesda.Skyrim.DialogTopic.SubtypeEnum.Scene,
                };
                topic.Quest.SetTo(quest.FormKey);
                var copy = (Mutagen.Bethesda.Skyrim.Scene)StbEditor.Core.OverrideEditing.WorkingCopy(Document, _scene, Scene()!);
                copy.Actions[0].Topic.SetTo(topic.FormKey);
                StbEditor.Core.OverrideEditing.CommitEdits(Document, "scene topic", [(topic, StbEditor.Core.RecordPlace.TopLevel, null), (copy, StbEditor.Core.RecordPlace.TopLevel, null)]);
                StbEditor.App.Panels.DialogueEditor.NewLine(this, topic, []);
                target.Apply("phase in front", r => StbEditor.Core.Scenes.InsertPhase((Mutagen.Bethesda.Skyrim.Scene)r, 0));
                var edited = Scene()!;
                Log.Info($"autotest-scene: edited: {Summary(edited)}; topic lines {StbEditor.App.Panels.DialogueEditor.Lines(this, topic.FormKey).Count}");
                var back = StbEditor.Core.RecordRoundTrip.Read(edited, session.Game.Release, System.Text.Encoding.UTF8) as Mutagen.Bethesda.Skyrim.ISceneGetter;
                Log.Info(back is null ? $"autotest-scene: round trip FAILED: {StbEditor.Core.RecordRoundTrip.LastError}" : $"autotest-scene: round trip: {Summary(back)}");
                var steps = 0;
                while (Document.History.UndoName != before && steps < 40) { Document.Undo(); steps++; }
                Log.Info($"autotest-scene: {steps} undo steps: {Summary(Scene()!)}; pending {Document.PendingCount}");
                for (var i = 0; i < steps; i++) Document.Redo();
                Log.Info($"autotest-scene: redone: {Summary(Scene()!)}; pending {Document.PendingCount}");
                StbEditor.App.Panels.SceneEditor.SelectActionForTest(_scene, Scene()!.Actions[0].Index ?? 0);
                if (arg + 2 < cli.Length && !cli[arg + 2].StartsWith("--", StringComparison.Ordinal)
                    && session.Catalog.BySignature["SCEN"].FirstOrDefault(r => string.Equals(r.EditorId, cli[arg + 2], StringComparison.OrdinalIgnoreCase)) is { } shown
                    && lookup.Resolve<Mutagen.Bethesda.Skyrim.ISceneGetter>(shown.FormKey) is { } other)
                {
                    FormWindows.Open(shown.FormKey);
                    StbEditor.App.Panels.SceneEditor.SelectActionForTest(shown.FormKey, other.Actions.FirstOrDefault()?.Index ?? 0);
                    Log.Info($"autotest-scene: {shown.EditorId}: {Summary(other)}");
                }
                _sceneStage = 2;
                _sceneFrames = 0;
                break;
            }
            case 2 when ++_sceneFrames > 15:
                _sceneStage = 9;
                break;
        }
    }

    private int _aliasUseStage, _aliasUseFrames;
    private FormKey _aliasUseQuest;
    private Task<List<StbEditor.Core.AliasUse>>? _aliasUseTask;

    /// <summary>
    /// --autotest-aliasuse [quest EditorID] [alias ID] (with --inspector-tab "Quest Aliases"): the quest's form with that alias
    /// selected and its Use Report built (default MS13, alias 1 Arvel); the uses are logged; nothing changes.
    /// </summary>
    private void RunAliasUseAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-aliasuse");
        if (arg < 0 || Session is not { } session || Loader is not null || _aliasUseStage == 9) return;
        string? Arg(int i) => arg + i < cli.Length && !cli[arg + i].StartsWith("--", StringComparison.Ordinal) ? cli[arg + i] : null;
        var lookup = new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog);
        switch (_aliasUseStage)
        {
            case 0:
            {
                var name = Arg(1) ?? "MS13";
                _aliasUseQuest = session.Catalog.BySignature["QUST"].First(r => string.Equals(r.EditorId, name, StringComparison.OrdinalIgnoreCase)).FormKey;
                var quest = lookup.Resolve<Mutagen.Bethesda.Skyrim.IQuestGetter>(_aliasUseQuest)!;
                var alias = uint.Parse(Arg(2) ?? "1");
                FormWindows.Open(_aliasUseQuest);
                StbEditor.App.Panels.QuestEditor.SelectForTest(_aliasUseQuest, "Aliases", quest.Aliases.Select((a, i) => (a, i)).First(x => x.a.ID == alias).i);
                _aliasUseTask = StbEditor.App.Panels.QuestEditor.UseReportForTest(this, quest, alias);
                _aliasUseStage = 1;
                break;
            }
            case 1 when _aliasUseTask is { IsCompleted: true } task:
            {
                if (task.IsCompletedSuccessfully)
                    foreach (var use in task.Result) Log.Info($"autotest-aliasuse: {session.Catalog.Get(use.Record)?.EditorId ?? use.Record.ToString()}: {use.Where}");
                Log.Info(task.IsCompletedSuccessfully ? $"autotest-aliasuse: {task.Result.Count} uses" : $"autotest-aliasuse: FAILED {task.Exception?.GetBaseException().Message}");
                _aliasUseStage = 2;
                break;
            }
            case 2 when ++_aliasUseFrames > 15:
                _aliasUseStage = 9;
                break;
        }
    }

    private int _textsStage, _textsFrames;
    private string _textsPath = "";

    /// <summary>
    /// --autotest-texts &lt;dir&gt; [plugin]: the plugin's texts (default: the Heart of Dibella quest mod) exported into &lt;dir&gt;,
    /// every 5th "translated", imported into a scratch active file in &lt;dir&gt;; the window stays open for the screenshot;
    /// undone and redone; nothing saved.
    /// </summary>
    private void RunTextsAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-texts");
        if (arg < 0 || arg + 1 >= cli.Length || Session is not { } session || Loader is not null || _textsStage == 9) return;
        switch (_textsStage)
        {
            case 0:
            {
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[arg + 1], "STBEditorTextsAutotest.esp"), false, false, session.Game, session.Workspace.DefaultEncoding));
                var plugin = arg + 2 < cli.Length && !cli[arg + 2].StartsWith("--", StringComparison.Ordinal) ? cli[arg + 2] : "The Heart Of Dibella - Quest Expansion.esp";
                _textsPath = Path.Combine(cli[arg + 1], Path.GetFileNameWithoutExtension(plugin) + ".texts.json");
                Texts.Show(this);
                Texts.StartExport(this, plugin, _textsPath);
                _textsStage = 1;
                break;
            }
            case 1 when !Texts.IsBusy:
            {
                Log.Info($"autotest-texts: export: {Texts.LastExport}");
                var file = StbEditor.Core.TextTranslation.Read(_textsPath);
                for (var i = 0; i < file.Strings.Count; i += 5) file.Strings[i].Translation = file.Strings[i].Source + " [перевод]";
                var translated = Path.ChangeExtension(_textsPath, ".ru.json");
                StbEditor.Core.TextTranslation.Write(translated, file);
                var before = Document.History.UndoName;
                Log.Info($"autotest-texts: import: {Texts.Import(this, translated, force: false)}; pending {Document.PendingCount}");
                Document.Undo();
                Log.Info($"autotest-texts: undone: pending {Document.PendingCount}, undo now '{Document.History.UndoName}' (was '{before}')");
                Document.Redo();
                Log.Info($"autotest-texts: redone: pending {Document.PendingCount}");
                _textsStage = 2;
                break;
            }
            case 2 when ++_textsFrames > 15:
                _textsStage = 9;
                break;
        }
    }

    private int _navSplitStage, _navSplitFrames;

    /// <summary>
    /// --autotest-navsplit &lt;dir&gt; (with --cell &lt;interior&gt;): a scratch active file in &lt;dir&gt;; triangle 5 of the cell's first
    /// navmesh found by index, then the first third of its triangles moved to a new navmesh (one undo step); undone and
    /// redone; the new navmesh stays selected for the screenshot. Nothing is saved.
    /// </summary>
    private void RunNavSplitAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-navsplit");
        if (arg < 0 || arg + 1 >= cli.Length || Session is not { } session || Loader is not null || _navSplitStage == 9) return;
        var scene = RenderWindow.Scene;
        string Counts() => string.Join(", ", RenderWindow.Scene?.Navmeshes.Select(n => $"{session.Workspace.FormIdText(n.Key)}:{n.Record.Data?.Triangles.Count}") ?? []);
        switch (_navSplitStage)
        {
            case 0 when scene is { Navmeshes.Count: > 0 } && ++_navSplitFrames > 30:
            {
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[arg + 1], "STBEditorNavSplitAutotest.esp"), false, false, session.Game, session.Workspace.DefaultEncoding));
                RenderWindow.SetNavmeshMode(true);
                var mesh = scene.Navmeshes[0];
                var found = RenderWindow.SelectNavmeshIndex(mesh.Key, 5, true);
                Log.Info($"autotest-navsplit: before: {Counts()}; triangle 5 found {found}, selected {RenderWindow.NavSelection?.Triangles.FirstOrDefault()}");
                var third = Enumerable.Range(0, (mesh.Record.Data?.Triangles.Count ?? 0) / 3).ToList();
                RenderWindow.SelectNavmeshForTest(mesh.Key, [], third);
                var before = Document.History.UndoName;
                var created = RenderWindow.MoveNavmeshSelectionToNew(this);
                Log.Info($"autotest-navsplit: moved {third.Count} into {created}: {Counts()}; undo '{Document.History.UndoName}', pending {Document.PendingCount}");
                Document.Undo();
                RenderWindow.RefreshNavmeshesNow(this);
                Log.Info($"autotest-navsplit: undone: {Counts()}; pending {Document.PendingCount}");
                Document.Redo();
                RenderWindow.RefreshNavmeshesNow(this);
                if (created is { } key) RenderWindow.SelectNavmeshForTest(key, [], Enumerable.Range(0, third.Count));
                Log.Info($"autotest-navsplit: redone: {Counts()}; pending {Document.PendingCount}");
                _navSplitStage = 1;
                _navSplitFrames = 0;
                break;
            }
            case 1 when ++_navSplitFrames > 15:
                _navSplitStage = 9;
                break;
        }
    }

    private int _compareFileStage, _compareFileFrames;

    /// <summary>--autotest-comparefile &lt;plugin&gt; &lt;other file&gt;: the window compares them; the differences are logged and shown.</summary>
    private void RunCompareFileAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-comparefile");
        if (arg < 0 || arg + 2 >= cli.Length || Session is null || Loader is not null || _compareFileStage == 9) return;
        switch (_compareFileStage)
        {
            case 0:
                CompareFile.Show(this);
                CompareFile.Start(this, cli[arg + 1], cli[arg + 2]);
                _compareFileStage = 1;
                break;
            case 1 when !CompareFile.IsBusy && ++_compareFileFrames > 2:
                Log.Info($"autotest-comparefile: {CompareFile.Result?.Count} difference(s): {string.Join(", ", CompareFile.Result?.GroupBy(e => e.Kind).Select(g => $"{g.Key} {g.Count()}") ?? [])}");
                _compareFileStage = 2;
                _compareFileFrames = 0;
                break;
            case 2 when ++_compareFileFrames > 15:
                _compareFileStage = 9;
                break;
        }
    }

    private int _mergeStage, _mergeFrames;

    /// <summary>
    /// --autotest-levelmerge &lt;dir&gt;: a scratch active file in &lt;dir&gt;; the leveled lists to merge are found and put into it
    /// (one undo step); undone and redone; the window stays open for the screenshot. Nothing is saved.
    /// </summary>
    private void RunLevelMergeAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-levelmerge");
        if (arg < 0 || arg + 1 >= cli.Length || Session is not { } session || Loader is not null || _mergeStage == 9) return;
        switch (_mergeStage)
        {
            case 0:
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[arg + 1], "STBEditorMergeAutotest.esp"), false, false, session.Game, session.Workspace.DefaultEncoding));
                LeveledMergeWindow.Show();
                LeveledMergeWindow.Start(this);
                _mergeStage = 1;
                break;
            case 1 when !LeveledMergeWindow.IsBusy && ++_mergeFrames > 2:
            {
                var found = LeveledMergeWindow.Result?.Count ?? -1;
                var put = LeveledMergeWindow.Apply(this);
                Log.Info($"autotest-levelmerge: {found} list(s) found, {put} put into the active file; pending {Document.PendingCount}, undo '{Document.History.UndoName}'");
                Document.Undo();
                Log.Info($"autotest-levelmerge: undone: pending {Document.PendingCount}");
                Document.Redo();
                Log.Info($"autotest-levelmerge: redone: pending {Document.PendingCount}");
                _mergeStage = 2;
                _mergeFrames = 0;
                break;
            }
            case 2 when ++_mergeFrames > 15:
                _mergeStage = 9;
                break;
        }
    }

    private int _tintStage, _tintFrames;

    /// <summary>
    /// --autotest-bodytint &lt;dir&gt; [NPC EditorID]: a scratch active file in &lt;dir&gt;; the NPC's body tint warning (default
    /// aaMZa_Imp_Fire of STB.esp) fixed through the check window's fix; the override's tint logged; undone. Nothing saved.
    /// </summary>
    private void RunBodyTintAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-bodytint");
        if (arg < 0 || arg + 1 >= cli.Length || Session is not { } session || Loader is not null || _tintStage == 9) return;
        switch (_tintStage)
        {
            case 0:
            {
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[arg + 1], "STBEditorTintAutotest.esp"), false, false, session.Game, session.Workspace.DefaultEncoding));
                var name = arg + 2 < cli.Length && !cli[arg + 2].StartsWith("--", StringComparison.Ordinal) ? cli[arg + 2] : "aaMZa_Imp_Fire";
                var record = session.Catalog.BySignature["NPC_"].First(r => string.Equals(r.EditorId, name, StringComparison.OrdinalIgnoreCase));
                var lookup = new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog);
                var npc = lookup.Resolve<Mutagen.Bethesda.Skyrim.INpcGetter>(record.FormKey)!;
                var race = lookup.Resolve<Mutagen.Bethesda.Skyrim.IRaceGetter>(npc.Race.FormKey)!;
                var stale = StbEditor.Core.BodyTint.Stale(npc, race, out var wanted);
                Log.Info($"autotest-bodytint: {name}: tint {npc.TextureLighting}, expected {wanted}, stale {stale}");
                var fixedCount = Panels.PluginCheckWindow.FixBodyTints(this, session, [new StbEditor.Core.CheckIssue(false, record.FormKey, "NPC_", name, "", "BodyTint")]);
                var edited = StbEditor.Core.OverrideEditing.Override(Document, record.FormKey)?.Record as Mutagen.Bethesda.Skyrim.INpcGetter;
                Log.Info($"autotest-bodytint: fixed {fixedCount}; override tint {edited?.TextureLighting}; stale now {(edited is null ? "-" : StbEditor.Core.BodyTint.Stale(edited, race, out _).ToString())}; pending {Document.PendingCount}");
                Document.Undo();
                Log.Info($"autotest-bodytint: undone: pending {Document.PendingCount}");
                _tintStage = 1;
                break;
            }
            case 1 when ++_tintFrames > 10:
                _tintStage = 9;
                break;
        }
    }

    private int _heightStage, _heightFrames;

    /// <summary>
    /// --autotest-heightmap &lt;dir&gt; (with --cell &lt;exterior&gt;): the loaded cells' heightmap exported into &lt;dir&gt;, raised by
    /// 1000 units in a copy, imported (one undo step), the heights checked; undone. Nothing is saved.
    /// </summary>
    private void RunHeightmapAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-heightmap");
        if (arg < 0 || arg + 1 >= cli.Length || Session is null || Loader is not null || _heightStage == 9) return;
        var scene = RenderWindow.Scene;
        switch (_heightStage)
        {
            case 0 when scene is { IsInterior: false, Terrain.Count: > 0 } && ++_heightFrames > 30:
            {
                var path = Path.Combine(cli[arg + 1], "heightmap.r16");
                Log.Info($"autotest-heightmap: export: {RenderWindow.ExportHeightmap(this, path)}");
                float Sample() => scene.Terrain.First(t => t.Data is not null).Data!.Heights[16 * 33 + 16];
                var before = Sample();
                // The same map 1000 units higher: shift the height range in the .json.
                var (info, samples) = StbEditor.Core.Heightmap.Read(path);
                info.MinHeight += 1000;
                info.MaxHeight += 1000;
                var raised = Path.Combine(cli[arg + 1], "raised.r16");
                StbEditor.Core.Heightmap.Write(raised, info, samples);
                Log.Info($"autotest-heightmap: import: {RenderWindow.ImportHeightmap(this, raised)}; pending {Document.PendingCount}");
                var after = RenderWindow.Scene!.Terrain.First(t => t.Data is not null).Data!.Heights[16 * 33 + 16];
                Log.Info($"autotest-heightmap: centre of the first cell {before:0.0} -> {after:0.0} (expected +1000 within the 16-bit step)");
                Document.Undo();
                RenderWindow.RefreshTerrainNow(this);
                Log.Info($"autotest-heightmap: undone: pending {Document.PendingCount}");
                _heightStage = 1;
                break;
            }
            case 1 when ++_heightFrames > 15:
                _heightStage = 9;
                break;
        }
    }

    private int _vcolorStage, _vcolorFrames;

    /// <summary>
    /// --autotest-vcolor (with --cell &lt;exterior&gt;): one stroke of the vertex colour brush (red, radius 1500) at the view's
    /// focus; the colour under it logged; left in place for the screenshot. Nothing is saved.
    /// </summary>
    private void RunVertexColorAutotest()
    {
        if (!Environment.GetCommandLineArgs().Contains("--autotest-vcolor") || Session is null || Loader is not null || _vcolorStage == 9) return;
        var scene = RenderWindow.Scene;
        switch (_vcolorStage)
        {
            case 0 when scene is { IsInterior: false, Terrain.Count: > 0 } && ++_vcolorFrames > 30:
            {
                var brush = RenderWindow.Brush;
                brush.Mode = Panels.LandBrushMode.Color;
                brush.Color = 0x0000FF;
                brush.Radius = 1500;
                brush.Strength = 100;
                RenderWindow.SetLandscapeMode(true);
                var point = scene.Focus;
                var ok = RenderWindow.StrokeForTest(this, point, 1f);
                var cell = RenderWindow.Scene!.Terrain.FirstOrDefault(t => t.Data?.Colors is not null
                    && point.X >= t.Origin.X && point.X < t.Origin.X + 4096 && point.Y >= t.Origin.Y && point.Y < t.Origin.Y + 4096);
                var vx = cell is null ? 0 : (int)MathF.Round((point.X - cell.Origin.X) / StbEditor.Core.LandscapeData.Spacing);
                var vy = cell is null ? 0 : (int)MathF.Round((point.Y - cell.Origin.Y) / StbEditor.Core.LandscapeData.Spacing);
                var c = cell?.Data?.Colors?[Math.Clamp(vy, 0, 32) * 33 + Math.Clamp(vx, 0, 32)];
                Log.Info($"autotest-vcolor: stroke {ok}; colour at the centre {(c is { } v ? $"{v & 0xFF},{(v >> 8) & 0xFF},{(v >> 16) & 0xFF}" : "none")}; pending {Document.PendingCount}, undo '{Document.History.UndoName}'");
                _vcolorStage = 1;
                _vcolorFrames = 0;
                break;
            }
            case 1 when ++_vcolorFrames > 20:
                _vcolorStage = 9;
                break;
        }
    }

    /// <summary>A frame to save as BMP once this frame is drawn (autotests: pictures before and after a change).</summary>
    public string? ShotRequest { get; set; }

    private int _faceStage, _faceFrames;

    /// <summary>
    /// --autotest-face [NPC FormID] (use with --inspector-tab Appearance): the NPC's form window on its face editor; two
    /// sliders moved as a person would; the preview rebuilt (pictures before / after in %TEMP%), the record changed in one
    /// undo step each. Nothing is saved.
    /// </summary>
    private void RunFaceAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-face");
        if (arg < 0 || Session is not { } session || Loader is not null || _faceStage == 9) return;
        var text = arg + 1 < cli.Length && !cli[arg + 1].StartsWith("--", StringComparison.Ordinal) ? cli[arg + 1] : "00013479";
        if (session.Workspace.ParseFormReference(text) is not { } key) { _faceStage = 9; return; }
        switch (_faceStage)
        {
            case 0:
                FormWindows.Open(key);
                _faceStage = 1;
                break;
            case 1 when Panels.FaceEditor.PreviewCurrent && ++_faceFrames > 30:
                ShotRequest = Path.Combine(Path.GetTempPath(), "stbeditor_face_before.bmp");
                _faceStage = 2;
                _faceFrames = 0;
                break;
            case 2 when ++_faceFrames > 2:
            {
                var a = Panels.FaceEditor.SetSliderForTest("NoseLongVsShort", 1f);
                var b = Panels.FaceEditor.SetSliderForTest("JawNarrowVsWide", 1f);
                var morph = (StbEditor.Core.OverrideEditing.Override(Document, key)?.Record as Mutagen.Bethesda.Skyrim.INpcGetter)?.FaceMorph;
                Log.Info($"autotest-face: sliders set {a}/{b}; record now nose {morph?.NoseLongVsShort}, jaw {morph?.JawNarrowVsWide}; undo '{Document.History.UndoName}', pending {Document.PendingCount}");
                _faceStage = 3;
                _faceFrames = 0;
                break;
            }
            case 3 when Panels.FaceEditor.PreviewCurrent && ++_faceFrames > 20:
                ShotRequest = Path.Combine(Path.GetTempPath(), "stbeditor_face_after.bmp");
                _faceStage = 4;
                _faceFrames = 0;
                break;
            case 4 when ++_faceFrames > 2:
                Document.Undo();
                Document.Undo();
                Log.Info($"autotest-face: after two Ctrl+Z pending {Document.PendingCount}");
                _faceStage = 9;
                break;
        }
    }

    private int _havokStage, _havokFrames;
    private List<(SceneObject Object, StbEditor.Core.RefState Resting)> _havokItems = [];

    /// <summary>
    /// --autotest-havok (with --cell of an interior with clutter): six small items near the view lifted 10 units and tipped 20°, Run
    /// Havok Sim; where they came to rest against where they lay (they should be back on their tables), then Ctrl+Z.
    /// Nothing is saved.
    /// </summary>
    private void RunHavokAutotest()
    {
        if (!Environment.GetCommandLineArgs().Contains("--autotest-havok") || Session is null || Loader is not null || _havokStage == 9) return;
        switch (_havokStage)
        {
            case 0 when RenderWindow.Scene is { } scene && RenderWindow.IsSettled && ++_havokFrames > 20:
            {
                var focus = scene.Focus;
                _havokItems = scene.Objects
                    .Where(o => o.BaseSignature == "MISC" && o.Model is { Shapes.Count: > 0 } && !o.IsDeleted && o.BoundsRadius < 40)
                    .OrderBy(o => System.Numerics.Vector3.Distance(o.Position, focus)).Take(6)
                    .Select(o => (o, o.State)).ToList();
                if (_havokItems.Count == 0) { Log.Error("autotest-havok: no clutter"); _havokStage = 9; break; }
                // A little above where they lay and tipped over (lifting far would put them into the shelves above).
                foreach (var (o, rest) in _havokItems)
                    o.Apply(rest with { Position = rest.Position + new System.Numerics.Vector3(0, 0, 10), Rotation = rest.Rotation + new System.Numerics.Vector3(0.35f, 0, 0) });
                RenderWindow.SelectForTest([.. _havokItems.Select(i => i.Object)]);
                RenderWindow.LookAtForTest(_havokItems[0].Object, 220);
                Log.Info($"autotest-havok: lifted {_havokItems.Count}: {string.Join(", ", _havokItems.Select(i => $"{i.Object.BaseEditorId} z {i.Resting.Position.Z:0.#}"))}");
                RenderWindow.RunHavokSim();
                _havokStage = 1;
                _havokFrames = 0;
                break;
            }
            case 1 when !RenderWindow.HavokRunning && ++_havokFrames > 5:
            {
                var lines = _havokItems.Select(i =>
                {
                    var now = i.Object.State;
                    var tilt = Math.Max(Math.Abs(Math.Sin(now.Rotation.X - i.Resting.Rotation.X)), Math.Abs(Math.Sin(now.Rotation.Y - i.Resting.Rotation.Y))) * 180 / Math.PI;
                    return $"{i.Object.BaseEditorId}: z {i.Resting.Position.Z:0.#} -> {now.Position.Z:0.#} (dz {now.Position.Z - i.Resting.Position.Z:0.#}), moved {System.Numerics.Vector2.Distance(new(now.Position.X, now.Position.Y), new(i.Resting.Position.X, i.Resting.Position.Y)):0.#} sideways, tilt {tilt:0}°";
                });
                Log.Info("autotest-havok: " + string.Join("; ", lines) + $"; undo '{Document.History.UndoName}'");
                ShotRequest = Path.Combine(Path.GetTempPath(), "stbeditor_havok_after.bmp");
                _havokStage = 2;
                _havokFrames = 0;
                break;
            }
            case 2 when ++_havokFrames > 3:
                Document.Undo();
                Log.Info($"autotest-havok: after Ctrl+Z z {string.Join(", ", _havokItems.Select(i => $"{i.Object.Position.Z:0.#}"))} (lifted), pending {Document.PendingCount}");
                _havokStage = 9;
                break;
        }
    }

    private int _exportStage;

    /// <summary>
    /// --autotest-export &lt;dir&gt; [plugin] (default Dawnguard.esm): File → Export data with every kind into &lt;dir&gt;; the files
    /// and their rows logged; then the ModGroups collapse checked on made-up versions.
    /// </summary>
    private void RunExportAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-export");
        if (arg < 0 || arg + 1 >= cli.Length || Session is null || Loader is not null || _exportStage == 9) return;
        switch (_exportStage)
        {
            case 0:
            {
                var plugin = arg + 2 < cli.Length && !cli[arg + 2].StartsWith("--", StringComparison.Ordinal) ? cli[arg + 2] : "Dawnguard.esm";
                ExportDataWindow.Show(this);
                ExportDataWindow.Start(this, plugin, StbEditor.Core.DataExport.Kinds.ToHashSet(), cli[arg + 1]);
                _exportStage = 1;
                break;
            }
            case 1 when !ExportDataWindow.IsBusy:
            {
                var files = Directory.GetFiles(cli[arg + 1]).Select(f => $"{Path.GetFileName(f)} {File.ReadLines(f).Count() - 1}");
                Log.Info($"autotest-export: {ExportDataWindow.Result}; {string.Join(", ", files)}");
                var group = new StbEditor.Core.ModGroup("test", ["B.esp", "C.esp"]);
                var collapsed = StbEditor.Core.ModGroups.Collapse(["C.esp", "B.esp", "A.esm"], v => v, [group]);
                Log.Info($"autotest-export: ModGroups C, B, A with group [B, C] -> {string.Join(", ", collapsed)}");
                _exportStage = 9;
                break;
            }
        }
    }

    private int _scriptStage;

    /// <summary>
    /// --autotest-script: File → Scripts; a script that lists NPCs without a voice and raises IronSword's damage by 1 run as the
    /// Run button does; its output, the sword's damage before and after, then Ctrl+Z. Nothing is saved.
    /// </summary>
    private void RunScriptAutotest()
    {
        if (!Environment.GetCommandLineArgs().Contains("--autotest-script") || Session is null || Loader is not null || _scriptStage == 9) return;
        int? Damage() => (StbEditor.Core.OverrideEditing.Override(Document, Mutagen.Bethesda.Plugins.FormKey.Factory("012EB7:Skyrim.esm"))?.Record as Mutagen.Bethesda.Skyrim.IWeaponGetter)?.BasicStats?.Damage;
        switch (_scriptStage)
        {
            case 0:
                // Not Show(): that writes the example scripts into Documents.
                ScriptsWindow.RunForTest(this, """
                    var silent = Records<INpcGetter>().Count(n => n.Voice.IsNull && n.Template.IsNull);
                    Print($"NPCs without a voice: {silent}");
                    var sword = (IWeaponGetter)Find("IronSword")!;
                    Print($"IronSword damage {sword.BasicStats!.Damage}");
                    Edit<Weapon>(sword, w => w.BasicStats!.Damage += 1);
                    Get<IWeaponGetter>(sword.FormKey)!.BasicStats!.Damage
                    """, "autotest");
                _scriptStage = 1;
                break;
            case 1 when !ScriptsWindow.IsBusy:
                Log.Info($"autotest-script: output [{ScriptsWindow.Output.Replace('\n', '|')}]; IronSword override damage {Damage()}; undo '{Document.History.UndoName}', pending {Document.PendingCount}");
                Document.Undo();
                Log.Info($"autotest-script: after Ctrl+Z override {Damage()?.ToString() ?? "none"}, pending {Document.PendingCount}");
                _scriptStage = 9;
                break;
        }
    }

    private int _regionStage, _regionFrames;

    /// <summary>
    /// --autotest-region [dir] (with --cell of an exterior; dir: a draft active file there for the new region — restore settings.json): World → Regions, borders shown, the Region tool on; the region under the
    /// view's centre selected, a shot from above (%TEMP%\stbeditor_region.bmp); its first corner moved 500 units east as a drag
    /// does, the corner read back; a new region made around the view's centre is undone with it (two Ctrl+Z). Nothing is saved.
    /// </summary>
    private void RunRegionAutotest()
    {
        if (!Environment.GetCommandLineArgs().Contains("--autotest-region") || Session is null || Loader is not null || _regionStage == 9) return;
        switch (_regionStage)
        {
            case 0 when RenderWindow.Scene is not null && RenderWindow.IsSettled && ++_regionFrames > 20:
            {
                RegionsWindow.Show(this);
                RenderWindow.SetRegionMode(true);
                var centre = RenderWindow.ViewCentreOnGround();
                var regions = RenderWindow.Regions(this);
                var under = regions.Where(r => r.Record.RegionAreas.Any(a => Panels.RenderWindow.Inside(a.RegionPointListData, centre))).ToList();
                Log.Info($"autotest-region: {regions.Count} regions in the worldspace; under {centre}: {string.Join(", ", under.Select(r => r.Record.EditorID))}");
                RenderWindow.SelectedRegion = under.FirstOrDefault().Key is var k && !k.IsNull ? k : regions.FirstOrDefault().Key;
                RenderWindow.TopViewForRegionTest(centre);
                ShotRequest = Path.Combine(Path.GetTempPath(), "stbeditor_region.bmp");
                _regionStage = 1;
                _regionFrames = 0;
                break;
            }
            case 1 when ++_regionFrames > 5:
            {
                var key = RenderWindow.SelectedRegion!.Value;
                var before = RenderWindow.Regions(this).First(r => r.Key == key).Record.RegionAreas[0].RegionPointListData[0];
                RenderWindow.MoveRegionPointForTest(this, 0, 0, new System.Numerics.Vector2(500, 0));
                var after = RenderWindow.Regions(this).First(r => r.Key == key).Record.RegionAreas[0].RegionPointListData[0];
                // A draft active file in <dir> for the new region (restore settings.json afterwards).
                var cli = Environment.GetCommandLineArgs();
                var at = Array.IndexOf(cli, "--autotest-region");
                if (at + 1 < cli.Length && !cli[at + 1].StartsWith("--", StringComparison.Ordinal))
                    SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[at + 1], "STBEditorRegionAutotest.esp"), false, false, Session!.Game, Session.Workspace.DefaultEncoding));
                var created = RenderWindow.AddRegionArea(this, RenderWindow.ViewCentreOnGround(), true);
                Log.Info($"autotest-region: corner {before} -> {after}; new region {created}; undo '{Document.History.UndoName}', pending {Document.PendingCount}");
                Document.Undo();
                Document.Undo();
                Log.Info($"autotest-region: after two Ctrl+Z corner {RenderWindow.Regions(this).First(r => r.Key == key).Record.RegionAreas[0].RegionPointListData[0]}, pending {Document.PendingCount}");
                _regionStage = 9;
                break;
            }
        }
    }

    private int _skyStage, _skyFrames;
    private static readonly float[] SkyHours = [6.5f, 12f, 19f, 23f];

    /// <summary>
    /// --autotest-sky [WTHR EditorID] (with --cell of an exterior; default SkyrimClear): the view lit as that weather at
    /// 6:30, 12, 19 and 23 — a shot each (%TEMP%\stbeditor_sky_&lt;hour&gt;.bmp) and the light logged; then the default light.
    /// </summary>
    private void RunSkyAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-sky");
        if (arg < 0 || Session is not { } session || Loader is not null || _skyStage == 9) return;
        if (_skyStage == 0)
        {
            if (RenderWindow.Scene is null || !RenderWindow.IsSettled || ++_skyFrames < 20) return;
            var name = arg + 1 < cli.Length && !cli[arg + 1].StartsWith("--", StringComparison.Ordinal) ? cli[arg + 1] : "SkyrimClear";
            var weather = session.Catalog.BySignature.GetValueOrDefault("WTHR")?.FirstOrDefault(r => string.Equals(r.EditorId, name, StringComparison.OrdinalIgnoreCase));
            if (weather is null) { Log.Error($"autotest-sky: no weather {name}"); _skyStage = 9; return; }
            RenderWindow.SetSky(weather.FormKey, SkyHours[0]);
            _skyStage = 1;
            _skyFrames = 0;
            return;
        }
        var index = _skyStage - 1;
        if (index < SkyHours.Length)
        {
            ++_skyFrames;
            if (_skyFrames == 5)
            {
                var l = RenderWindow.Scene!.Lighting;
                Log.Info($"autotest-sky: {SkyHours[index]:0.#}h sun {l.DirectionalColor} from {l.DirectionTowardsLight}, ambient {l.Ambient}, fog {l.FogColor} {l.FogNear:0}-{l.FogFar:0}");
                ShotRequest = Path.Combine(Path.GetTempPath(), $"stbeditor_sky_{SkyHours[index]:0.#}.bmp".Replace(',', '.'));
            }
            if (_skyFrames == 8)
            {
                _skyStage++;
                _skyFrames = 0;
                if (index + 1 < SkyHours.Length) RenderWindow.SetSky(RenderWindow.Weather, SkyHours[index + 1]);
            }
            return;
        }
        RenderWindow.SetSky(null, 12);
        _skyStage = 9;
    }

    private int _voiceStage, _voiceFrames;
    private string? _voiceDir;

    /// <summary>
    /// --autotest-voice &lt;dir&gt;: World → Voice files for Skyrim.esm's MQ101 lines (states counted); then a WAV of one line written
    /// to &lt;dir&gt; (from the build's .fuz, mixed to stereo so LipGenerator gets the conversion), shown to the asset view as that
    /// line's loose file, «Make .lip and pack .fuz» — the .lip and .fuz must appear in &lt;dir&gt; and the row turn «fuz».
    /// Writes only into &lt;dir&gt;; nothing is saved.
    /// </summary>
    private void RunVoiceAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-voice");
        if (arg < 0 || arg + 1 >= cli.Length || Session is not { } session || Loader is not null || _voiceStage == 9) return;
        switch (_voiceStage)
        {
            case 0:
                _voiceDir = cli[arg + 1];
                Directory.CreateDirectory(_voiceDir);
                VoiceFilesWindow.Show(this);
                VoiceFilesWindow.ListForTest(this, "Skyrim.esm", "MQ101");
                _voiceStage = 1;
                break;
            case 1 when !VoiceFilesWindow.IsBusy && ++_voiceFrames > 5:
            {
                var rows = VoiceFilesWindow.Rows;
                Log.Info($"autotest-voice: {rows.Count} rows: {string.Join(", ", rows.GroupBy(r => r.State).Select(g => $"{g.Key} {g.Count()}"))}; first {rows.FirstOrDefault(r => r.Line.PathStem is not null)?.Line.PathStem}");
                // One voiced line made into a loose stereo WAV in the test folder, as if recorded.
                var row = rows.FirstOrDefault(r => r.State == Panels.VoiceFilesWindow.State.Fuz);
                if (row is null || session.Assets.ReadAllBytes(row.Audio!) is not { } fuz || StbEditor.Core.VoiceTools.Unfuz(fuz) is not { } parts) { Log.Error("autotest-voice: no fuz to start from"); _voiceStage = 9; break; }
                var tools = new StbEditor.Core.VoiceTools(session.Game.GameFolder);
                var xwm = Path.Combine(_voiceDir!, "source.xwm");
                File.WriteAllBytes(xwm, parts.Audio);
                var wav = Path.Combine(_voiceDir!, Path.GetFileName(row.Line.PathStem!) + ".wav");
                var decode = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tools.XwmaEncode, $"\"{xwm}\" \"{wav}\"") { UseShellExecute = false, CreateNoWindow = true });
                decode?.WaitForExit(60000);
                session.Assets.AddLoose(row.Line.PathStem + ".wav", wav, "autotest");
                // The build's .fuz would win over the WAV: test this line as though it had none.
                VoiceFilesWindow.ReplaceForTest(row with { State = Panels.VoiceFilesWindow.State.WavNoLip, Audio = row.Line.PathStem + ".wav", Lip = null });
                Log.Info($"autotest-voice: {wav} {(File.Exists(wav) ? new FileInfo(wav).Length : 0)} bytes; tools lip {tools.CanMakeLip}, encode {tools.CanEncode}");
                VoiceFilesWindow.Process(this, true, true);
                _voiceStage = 2;
                _voiceFrames = 0;
                break;
            }
            case 2 when !VoiceFilesWindow.IsBusy && ++_voiceFrames > 5:
            {
                var files = Directory.GetFiles(_voiceDir!).Select(f => $"{Path.GetFileName(f)} {new FileInfo(f).Length}");
                Log.Info($"autotest-voice: {VoiceFilesWindow.Result}; files: {string.Join(", ", files)}");
                _voiceStage = 9;
                break;
            }
        }
    }

    private int _idleStage, _idleFrames;
    private Panels.IdleTreeWindow.TestCase? _idleCase;

    /// <summary>
    /// --autotest-idletree: World → Idle animations built; under the first idle with three or more children the first child
    /// moved after the second (as the menu's «Move down» does), the order read back from the rebuilt tree, a shot
    /// (%TEMP%\stbeditor_idletree.bmp), Ctrl+Z and the order read again. Nothing is saved.
    /// </summary>
    private void RunIdleTreeAutotest()
    {
        if (!Environment.GetCommandLineArgs().Contains("--autotest-idletree") || Session is null || Loader is not null || _idleStage == 9) return;
        switch (_idleStage)
        {
            case 0:
                IdleTreeWindow.Show();
                IdleTreeWindow.Rebuild(this);
                _idleCase = IdleTreeWindow.MoveDownForTest(this);
                if (_idleCase is null) { Log.Error("autotest-idletree: no idle with three children"); _idleStage = 9; break; }
                Log.Info($"autotest-idletree: {IdleTreeWindow.Nodes.Count} idles, {IdleTreeWindow.Roots.Count} roots; under {_idleCase.Parent}: before {_idleCase.Before}; undo '{Document.History.UndoName}', pending {Document.PendingCount}");
                _idleStage = 1;
                break;
            case 1 when ++_idleFrames > 20:
                IdleTreeWindow.Rebuild(this);
                Log.Info($"autotest-idletree: after the move {IdleTreeWindow.OrderForTest(_idleCase!)}");
                ShotRequest = Path.Combine(Path.GetTempPath(), "stbeditor_idletree.bmp");
                _idleStage = 2;
                _idleFrames = 0;
                break;
            case 2 when ++_idleFrames > 3:
                Document.Undo();
                IdleTreeWindow.Rebuild(this);
                Log.Info($"autotest-idletree: after Ctrl+Z {IdleTreeWindow.OrderForTest(_idleCase!)}, pending {Document.PendingCount}");
                _idleStage = 9;
                break;
        }
    }

    private int _perkStage, _perkFrames;
    private Mutagen.Bethesda.Plugins.FormKey _perkKey;

    /// <summary>
    /// --autotest-perktree [AVIF EditorID] (with --inspector-tab "Perk tree"; default AVOneHanded): the skill's form on its perk
    /// tree, a shot (%TEMP%\stbeditor_perktree_before.bmp); a node moved, a link toggled and a node added as the canvas does,
    /// the record's tree logged, a second shot (_after.bmp); three Ctrl+Z. Nothing is saved.
    /// </summary>
    private void RunPerkTreeAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-perktree");
        if (arg < 0 || Session is not { } session || Loader is not null || _perkStage == 9) return;
        string Tree() => (StbEditor.Core.OverrideEditing.Override(Document, _perkKey)?.Record as Mutagen.Bethesda.Skyrim.IActorValueInformationGetter)?.PerkTree is { } tree
            ? $"{tree.Count} nodes; #1 at {Panels.PerkTreeEditor.Position(tree[1])}, links {string.Join(",", tree[1].ConnectionLineToIndices)}; last #{tree[^1].Index} at {Panels.PerkTreeEditor.Position(tree[^1])}"
            : "not edited";
        switch (_perkStage)
        {
            case 0:
            {
                var name = arg + 1 < cli.Length && !cli[arg + 1].StartsWith("--", StringComparison.Ordinal) ? cli[arg + 1] : "AVOneHanded";
                var entry = session.Catalog.BySignature.GetValueOrDefault("AVIF")?.FirstOrDefault(r => string.Equals(r.EditorId, name, StringComparison.OrdinalIgnoreCase));
                if (entry is null) { Log.Error($"autotest-perktree: no AVIF {name}"); _perkStage = 9; break; }
                _perkKey = entry.FormKey;
                FormWindows.Open(_perkKey);
                _perkStage = 1;
                break;
            }
            case 1 when ++_perkFrames > 30:
                ShotRequest = Path.Combine(Path.GetTempPath(), "stbeditor_perktree_before.bmp");
                _perkStage = 2;
                _perkFrames = 0;
                break;
            case 2 when ++_perkFrames > 2:
                if (!Panels.PerkTreeEditor.EditForTest()) { Log.Error("autotest-perktree: the tree is not drawn for editing"); _perkStage = 9; break; }
                Log.Info($"autotest-perktree: {Tree()}; undo '{Document.History.UndoName}', pending {Document.PendingCount}");
                _perkStage = 3;
                _perkFrames = 0;
                break;
            case 3 when ++_perkFrames > 10:
                ShotRequest = Path.Combine(Path.GetTempPath(), "stbeditor_perktree_after.bmp");
                _perkStage = 4;
                _perkFrames = 0;
                break;
            case 4 when ++_perkFrames > 2:
                for (var i = 0; i < 3; i++) Document.Undo();
                Log.Info($"autotest-perktree: after three Ctrl+Z pending {Document.PendingCount}");
                _perkStage = 9;
                break;
        }
    }

    private int _navGenStage, _navGenFrames, _navGenBefore;

    /// <summary>
    /// --autotest-navgen (with --cell): Generate navmesh for the open cell with the default settings, as the Navmesh panel
    /// does; logs the triangles before and after and the summary, a screenshot from above (%TEMP%\stbeditor_navgen_after.bmp),
    /// then Ctrl+Z until nothing is pending. Nothing is saved.
    /// </summary>
    private void RunNavGenAutotest()
    {
        if (!Environment.GetCommandLineArgs().Contains("--autotest-navgen") || Session is null || Loader is not null || _navGenStage == 9) return;
        int Triangles() => RenderWindow.Scene?.Navmeshes.Sum(n => n.Record.Data?.Triangles.Count ?? 0) ?? 0;
        switch (_navGenStage)
        {
            case 0 when RenderWindow.Scene is not null && RenderWindow.IsSettled && Session.Doors.IsCompleted && ++_navGenFrames > 20:
                _navGenBefore = Triangles();
                RenderWindow.SetNavmeshMode(true);
                if (!RenderWindow.GenerateNavmesh(this, new Core.NavmeshGenerationSettings { KeepUnreachedArea = RenderWindow.Scene!.IsInterior ? 0 : 40000 })) { Log.Error("autotest-navgen: did not start"); _navGenStage = 9; break; }
                Log.Info($"autotest-navgen: started, {_navGenBefore} navmesh triangles loaded");
                _navGenStage = 1;
                _navGenFrames = 0;
                break;
            case 1 when !RenderWindow.NavmeshGenerating && ++_navGenFrames > 5:
                Log.Info($"autotest-navgen: {_navGenBefore} -> {Triangles()} triangles; {RenderWindow.NavmeshGenerationSummary}; undo '{Document.History.UndoName}', pending {Document.PendingCount}");
                RenderWindow.TopViewForTest();
                ShotRequest = Path.Combine(Path.GetTempPath(), "stbeditor_navgen_after.bmp");
                _navGenStage = 2;
                _navGenFrames = 0;
                break;
            case 2 when ++_navGenFrames > 30:
                for (var i = 0; i < 4 && Document.PendingCount > 0; i++) Document.Undo();
                RenderWindow.RefreshNavmeshesNow(this);
                Log.Info($"autotest-navgen: after Ctrl+Z {Triangles()} triangles (was {_navGenBefore}), pending {Document.PendingCount}");
                _navGenStage = 9;
                break;
        }
    }

    private int _faceGenStage, _faceGenFrames;
    private SceneObject? _faceGenActor;

    /// <summary>
    /// --autotest-facegen &lt;dir&gt; [NPC FormID] (with --cell): a draft active file in &lt;dir&gt;; FaceGen exported for the NPC
    /// (default Orgnar, in RiverwoodSleepingGiantInn) as Ctrl+F4 does; the files under &lt;dir&gt;, the editor's asset view
    /// pointing at them, the actor's head reloaded; the camera on its face for the screenshot. Restore settings.json after.
    /// </summary>
    private void RunFaceGenAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-facegen");
        if (arg < 0 || arg + 1 >= cli.Length || Session is not { } session || Loader is not null || _faceGenStage == 9) return;
        switch (_faceGenStage)
        {
            case 0 when RenderWindow.Scene is { } scene && RenderWindow.IsSettled && ++_faceGenFrames > 20:
            {
                // The CK's head first, for a picture to compare with.
                var text0 = arg + 2 < cli.Length && !cli[arg + 2].StartsWith("--", StringComparison.Ordinal) ? cli[arg + 2] : "00013479";
                _faceGenActor = scene.Objects.FirstOrDefault(o => o.Base == session.Workspace.ParseFormReference(text0));
                if (_faceGenActor is not null) RenderWindow.LookAtHeadForTest(_faceGenActor);
                if (RenderWindow.Options is { } options) options.HighlightActors = false; // true colours for the pictures
                _faceGenStage = 2;
                _faceGenFrames = 0;
                break;
            }
            case 2 when RenderWindow.IsSettled && ++_faceGenFrames > 20:
                ShotRequest = Path.Combine(cli[arg + 1], "facegen_before.bmp");
                _faceGenStage = 3;
                _faceGenFrames = 0;
                break;
            case 3 when RenderWindow.Scene is { } scene && ++_faceGenFrames > 2:
            {
                var dir = cli[arg + 1];
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(dir, "STBEditorFaceGenAutotest.esp"), false, false, session.Game, session.Workspace.DefaultEncoding));
                var text = arg + 2 < cli.Length && !cli[arg + 2].StartsWith("--", StringComparison.Ordinal) ? cli[arg + 2] : "00013479";
                var npc = session.Workspace.ParseFormReference(text);
                _faceGenActor = scene.Objects.FirstOrDefault(o => o.Base == npc);
                if (npc is not { } key) { Log.Error("autotest-facegen: bad NPC"); _faceGenStage = 9; break; }
                var outcome = StbEditor.App.Services.FaceGenExports.Export(this, [key]);
                var nif = Path.Combine(dir, StbEditor.Render.FaceGenExport.NifPath(key));
                var dds = Path.Combine(dir, StbEditor.Render.FaceGenExport.TintPath(key));
                var source = session.Assets.Find(StbEditor.Render.FaceGenExport.NifPath(key));
                Log.Info($"autotest-facegen: exported {outcome.Exported}, notes [{string.Join("; ", outcome.Problems)}]; nif {(File.Exists(nif) ? new FileInfo(nif).Length : -1)} bytes, dds {(File.Exists(dds) ? new FileInfo(dds).Length : -1)} bytes; asset view now reads it from {source?.LoosePath}");
                try
                {
                    var model = StbEditor.Nif.NifFile.Load(File.ReadAllBytes(nif), nif);
                    var tint = StbEditor.Render.TextureImage.Decode(File.ReadAllBytes(dds));
                    Log.Info($"autotest-facegen: reads back: {model.Shapes.Count} shapes [{string.Join(", ", model.Shapes.Select(x => $"{x.Name} {x.Positions.Length}v"))}], warnings {model.Warnings.Count}; tint {tint.Width}x{tint.Height}");
                }
                catch (Exception ex) { Log.Error($"autotest-facegen: read back failed: {ex.Message}"); }
                if (_faceGenActor is not null) RenderWindow.LookAtHeadForTest(_faceGenActor);
                _faceGenStage = 1;
                _faceGenFrames = 0;
                break;
            }
            case 1 when RenderWindow.IsSettled && ++_faceGenFrames > 30:
                Log.Info($"autotest-facegen: actor {_faceGenActor?.BaseEditorId} model {(_faceGenActor?.Model is { } m ? $"{m.Shapes.Count} shapes" : "none")}, failed {_faceGenActor?.ModelFailed}");
                _faceGenStage = 9;
                break;
        }
    }

    private int _placeStage, _placeFrames;
    private SceneObject? _placed;
    private System.Numerics.Vector2 _placeMouse;
    private (System.Numerics.Vector3 Position, float Z) _placeStart;

    /// <summary>
    /// --autotest-place &lt;dir&gt; [base FormID] (with --cell): a draft active file in &lt;dir&gt;; the base (default
    /// XMarkerHeading) dropped at the centre of the view as from the Object Window; its model, a click on it, the
    /// Inspector and a right-drag turn go to the log. Nothing is saved (restore settings.json).
    /// </summary>
    private void RunPlaceAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-place");
        if (arg < 0 || arg + 1 >= cli.Length || Session is not { } session || Loader is not null || _placeStage == 9) return;
        switch (_placeStage)
        {
            case 0 when RenderWindow.Scene is not null && RenderWindow.IsSettled && ++_placeFrames > 30:
            {
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[arg + 1], "STBEditorPlaceAutotest.esp"), false, false, session.Game, session.Workspace.DefaultEncoding));
                var text = arg + 2 < cli.Length && !cli[arg + 2].StartsWith("--", StringComparison.Ordinal) ? cli[arg + 2] : "00000034";
                _placed = session.Workspace.ParseFormReference(text) is { } baseKey ? RenderWindow.DropAtCentreForTest(this, baseKey) : null;
                Log.Info($"autotest-place: dropped {text} -> {_placed?.BaseEditorId} [{_placed?.BaseSignature}] at {_placed?.Position}, ref {_placed?.Reference}, selected {Selected}");
                _placeStage = _placed is null ? 9 : 1;
                _placeFrames = 0;
                break;
            }
            case 1 when ++_placeFrames > 60:
            {
                var o = _placed!;
                var options = RenderWindow.Options!;
                var shapes = o.Model?.Shapes.Select(s => $"{s.Source.Name} visible {SceneRenderer.IsShapeVisible(s.Source, options)} blended {s.Material.AlphaBlend}") ?? [];
                Log.Info($"autotest-place: kind {o.Kind}, model {o.ModelPath} loaded {o.Model is not null} failed {o.ModelFailed}, radius {o.BoundsRadius:0}, shapes [{string.Join("; ", shapes)}]");
                Log.Info($"autotest-place: a click on it picks {RenderWindow.PickOnScreenForTest(o)?.BaseEditorId ?? "nothing"}; inspector {(Inspector.Form.Error ?? "shows the reference")}");
                var before = o.Rotation.Z;
                RenderWindow.RotateSelectionForTest(90);
                Log.Info($"autotest-place: right-drag turn {before * 180 / MathF.PI:0.#} -> {o.Rotation.Z * 180 / MathF.PI:0.#} deg, undo '{Document.History.UndoName}'");
                RenderWindow.GizmoTestTarget = o;
                RenderWindow.GizmoTestSnap = true;
                _placeStage = 2;
                _placeFrames = 0;
                break;
            }
            case 2:
                // The rotate gizmo on the placed object (same steps as --autotest-gizmo).
                if (!RenderWindow.GizmoTestStep()) break;
                // Then once round with the gizmo: from above, and with the camera almost level (snap 15° both times).
                if (!RenderWindow.GizmoTestSweep || !RenderWindow.GizmoTestLevel)
                {
                    RenderWindow.GizmoTestLevel = RenderWindow.GizmoTestSweep;
                    RenderWindow.GizmoTestSweep = true;
                    RenderWindow.RestartGizmoTest();
                    break;
                }
                RenderWindow.GizmoTestSweep = RenderWindow.GizmoTestLevel = false;
                RenderWindow.GizmoTestTarget = null;
                RenderWindow.GizmoTestSnap = false;
                RenderWindow.LookAtForTest(_placed!, 260);
                RenderWindow.SetAngleSnapForTest(true, 15f);
                _placeStage = 10;
                _placeFrames = 0;
                break;
            case 10 when ++_placeFrames > 15:
            {
                // Right button held on the object and moved to the right, angle snap 15° (as a person turns it).
                if (RenderWindow.ScreenPointForTest(_placed!) is not { } point) { Log.Error("autotest-place: object not on screen"); _placeStage = 3; break; }
                _placeMouse = point;
                _placeStart = (_placed!.Position, _placed.Rotation.Z);
                RenderWindow.ToolForTest = EditTool.Move; // as in the report: Move tool, right button
                Log.Info($"autotest-place: right button on the object at {point}; a click there picks {RenderWindow.PickOnScreenForTest(_placed)?.BaseEditorId ?? "nothing"}");
                StbEditor.App.Ui.ImGuiLayer.TestMouseButton = 1;
                StbEditor.App.Ui.ImGuiLayer.TestMouse = (point, true);
                _placeStage = 11;
                _placeFrames = 0;
                break;
            }
            case 11:
                if (++_placeFrames <= 300)
                {
                    // 720 px: once round (0.5° a pixel), held still at 180° for 60 frames on the way (a turn held past
                    // 60° used to grow its own rounding error every frame until the object flew away).
                    var pixels = _placeFrames <= 120 ? _placeFrames * 3 : _placeFrames <= 180 ? 360 : (_placeFrames - 60) * 3;
                    StbEditor.App.Ui.ImGuiLayer.TestMouse = (_placeMouse + new System.Numerics.Vector2(pixels, 0), true);
                    if (_placeFrames % 30 == 0)
                        Log.Info($"autotest-place: right-drag +{pixels} px ({RenderWindow.DragStateForTest}): moved {System.Numerics.Vector3.Distance(_placeStart.Position, _placed!.Position):0.###}, rotation {_placed.Rotation.Z * 180 / MathF.PI:0.#} deg");
                    break;
                }
                StbEditor.App.Ui.ImGuiLayer.TestMouse = (_placeMouse + new System.Numerics.Vector2(720, 0), false);
                _placeStage = 12;
                _placeFrames = 0;
                break;
            case 12 when ++_placeFrames > 5:
                StbEditor.App.Ui.ImGuiLayer.TestMouse = null;
                StbEditor.App.Ui.ImGuiLayer.TestMouseButton = 0;
                Log.Info($"autotest-place: right-drag with 15° snap: position {_placeStart.Position} -> {_placed!.Position}, rotation {_placeStart.Z * 180 / MathF.PI:0.#} -> {_placed.Rotation.Z * 180 / MathF.PI:0.#} deg, undo '{Document.History.UndoName}'");
                RenderWindow.SetAngleSnapForTest(false, 15f);
                RenderWindow.LookFromSideForTest(_placed, 300, 12);
                RenderWindow.ToolForTest = EditTool.Rotate; // left drags: first with Rotate, then with Move
                _placeStage = 13;
                _placeFrames = 0;
                break;
            case 13 when ++_placeFrames > 10:
            {
                // Left button held on the object with the camera almost level (as in a mine), mouse moved up a little.
                if (RenderWindow.ScreenPointForTest(_placed!) is not { } point) { Log.Error("autotest-place: object not on screen"); _placeStage = 3; break; }
                _placeMouse = point;
                _placeStart = (_placed!.Position, _placed.Rotation.Z);
                StbEditor.App.Ui.ImGuiLayer.TestMouse = (point, true);
                Log.Info($"autotest-place: left button on the object, tool {RenderWindow.ToolForTest}, camera level with it; a click there picks {RenderWindow.PickOnScreenForTest(_placed)?.BaseEditorId ?? "nothing"}");
                _placeStage = 14;
                _placeFrames = 0;
                break;
            }
            case 14:
                if (++_placeFrames <= 24)
                {
                    StbEditor.App.Ui.ImGuiLayer.TestMouse = (_placeMouse + new System.Numerics.Vector2(_placeFrames * 2, -_placeFrames * 0.5f), true);
                    if (_placeFrames % 4 == 0)
                        Log.Info($"autotest-place: left-drag +{_placeFrames * 2},-{_placeFrames * 0.5f} px ({RenderWindow.DragStateForTest}): position {_placed!.Position}, rotation {_placed.Rotation.Z * 180 / MathF.PI:0.#} deg");
                    break;
                }
                StbEditor.App.Ui.ImGuiLayer.TestMouse = (_placeMouse + new System.Numerics.Vector2(48, -12), false);
                _placeStage = 15;
                _placeFrames = 0;
                break;
            case 15 when ++_placeFrames > 5:
                StbEditor.App.Ui.ImGuiLayer.TestMouse = null;
                Log.Info($"autotest-place: left-drag in {RenderWindow.ToolForTest}: position {_placeStart.Position} -> {_placed!.Position} ({System.Numerics.Vector3.Distance(_placeStart.Position, _placed.Position):0} units), rotation {_placeStart.Z * 180 / MathF.PI:0.#} -> {_placed.Rotation.Z * 180 / MathF.PI:0.#} deg, undo '{Document.History.UndoName}'");
                // The same drag with the Move tool: the object moves, but stays near the cursor.
                if (RenderWindow.ToolForTest == EditTool.Rotate)
                {
                    RenderWindow.ToolForTest = EditTool.Move;
                    RenderWindow.LookFromSideForTest(_placed, 300, 12);
                    _placeStage = 13;
                }
                else _placeStage = 3;
                _placeFrames = 0;
                break;
            case 3 when ++_placeFrames > 20:
            {
                // A field of the new reference set in the Inspector's All fields, then saved with its placement.
                Log.Info($"autotest-place: inspector {(Inspector.Form.Error ?? "shows the reference")}, rotation {_placed!.Rotation.Z * 180 / MathF.PI:0.#} deg, pending {Document.PendingCount}");
                Inspector.Form.Target?.Apply("EditorID", r => r.EditorID = "STBPlaceAutotestRef");
                Log.Info($"autotest-place: EditorID set through the Inspector: {StbEditor.Core.OverrideEditing.Override(Document, _placed.Reference)?.Record.EditorID}; saving");
                Save();
                _placeStage = 4;
                break;
            }
            case 4 when !Saver.IsBusy && Document.PendingCount == 0 && RenderWindow.IsSettled:
            {
                var path = Path.Combine(cli[arg + 1], "STBEditorPlaceAutotest.esp");
                using (var mod = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(path, session.Game.Release))
                {
                    var saved = mod.EnumerateMajorRecords<Mutagen.Bethesda.Skyrim.IPlacedObjectGetter>().FirstOrDefault(r => r.FormKey == _placed!.Reference);
                    Log.Info($"autotest-place: saved {(saved is null ? "NOT FOUND" : $"{saved.EditorID} base {saved.Base.FormKey} rotation z {saved.Placement?.Rotation.Z * 180 / MathF.PI:0.#} deg at {saved.Placement?.Position}")}");
                }
                // Selected again after the reload, the Inspector reads the saved reference (not the record it had before).
                Select(_placed!.Reference);
                _placeStage = 5;
                _placeFrames = 0;
                break;
            }
            case 5 when ++_placeFrames > 10:
                Log.Info($"autotest-place: after the reload the inspector shows {string.Join(", ", Inspector.Form.Versions.Select(v => $"{v.Plugin}: {v.Record.EditorID}"))}{(Inspector.Form.Error is { } error ? " / " + error : "")}");
                _placeStage = 9;
                break;
        }
    }

    private int _paletteStage, _paletteFrames;

    /// <summary>
    /// --autotest-palette &lt;dir&gt; (with --cell): a scratch active file in &lt;dir&gt;; three rocks in the palette, five objects
    /// placed around the view's focus (random choice, turn, size); each one undo step; the window stays open. Nothing saved.
    /// </summary>
    private void RunPaletteAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-palette");
        if (arg < 0 || arg + 1 >= cli.Length || Session is not { } session || Loader is not null || _paletteStage == 9) return;
        var scene = RenderWindow.Scene;
        switch (_paletteStage)
        {
            case 0 when scene is not null && ++_paletteFrames > 30:
            {
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[arg + 1], "STBEditorPaletteAutotest.esp"), false, false, session.Game, session.Workspace.DefaultEncoding));
                var rocks = session.Catalog.BySignature["STAT"].Where(r => r.EditorId?.StartsWith("RockPile", StringComparison.OrdinalIgnoreCase) == true).Take(3).Select(r => r.FormKey).ToList();
                RenderWindow.Palette.Objects.AddRange(rocks);
                RenderWindow.Palette.MaxTilt = 10;
                PaletteWindow.Show();
                RenderWindow.SetPaletteMode(true);
                var before = Document.PendingCount;
                var placed = new List<string>();
                for (var i = 0; i < 5; i++)
                {
                    var point = scene.Focus + new System.Numerics.Vector3(i * 150 - 300, 200, 0);
                    if (RenderWindow.PlacePaletteObject(this, point) is { } obj) placed.Add($"{obj.BaseEditorId} rot {obj.Rotation.Z * 180 / MathF.PI:0} scale {obj.Scale:0.00}");
                }
                Log.Info($"autotest-palette: palette [{string.Join(", ", rocks.Select(k => session.Catalog.Get(k)?.EditorId))}]; placed {placed.Count}: {string.Join("; ", placed)}; pending {before} -> {Document.PendingCount}, undo '{Document.History.UndoName}'");
                _paletteStage = 1;
                _paletteFrames = 0;
                break;
            }
            case 1 when ++_paletteFrames > 20:
                _paletteStage = 9;
                break;
        }
    }

    private int _layersStage, _layersFrames;
    private Panels.Layer? _testLayer;

    /// <summary>
    /// --autotest-layers (with --cell): a layer of the cell's first 40 statics is made, hidden (they leave drawing and
    /// picking), then shown and frozen (drawn, not selectable); the window stays open with the layer hidden for the
    /// screenshot; the test layer is removed at the end (layers.json keeps the user's own).
    /// </summary>
    private void RunLayersAutotest()
    {
        if (!Environment.GetCommandLineArgs().Contains("--autotest-layers") || Session is null || Loader is not null || _layersStage == 9) return;
        var scene = RenderWindow.Scene;
        switch (_layersStage)
        {
            case 0 when scene is not null && RenderWindow.Options is { } options && ++_layersFrames > 30:
            {
                var statics = scene.Objects.Where(o => o.BaseSignature == "STAT" && !options.Hides(o)).Take(40).ToList();
                _testLayer = LayersWindow.Add("STB autotest layer", statics.Select(o => o.Reference));
                LayersWindow.Set(_testLayer, hidden: true);
                LayersWindow.Draw(this);
                var hidden = statics.Count(o => options.Hides(o));
                LayersWindow.Set(_testLayer, hidden: false, frozen: true);
                LayersWindow.Draw(this);
                var shown = statics.Count(o => !options.Hides(o));
                var frozen = statics.Count(o => options.FrozenReferences.Contains(o.Reference));
                Log.Info($"autotest-layers: {statics.Count} statics in the layer; hidden {hidden}; shown again {shown}, frozen {frozen}");
                LayersWindow.Set(_testLayer, hidden: true, frozen: false);
                LayersWindow.Show();
                _layersStage = 1;
                _layersFrames = 0;
                break;
            }
            case 1 when ++_layersFrames > 20:
                if (_testLayer is not null) LayersWindow.Remove(_testLayer);
                Log.Info($"autotest-layers: test layer removed, {LayersWindow.Layers.Count} layer(s) left");
                _layersStage = 9;
                break;
        }
    }

    private int _smStage, _smFrames;

    /// <summary>--autotest-storymanager [filter]: the Story Manager tree built and shown (filtered); its size logged.</summary>
    private void RunStoryManagerAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var arg = Array.IndexOf(cli, "--autotest-storymanager");
        if (arg < 0 || Session is null || Loader is not null || _smStage == 9) return;
        switch (_smStage)
        {
            case 0:
            {
                StoryManagerWindow.Show(this);
                static int Count(IEnumerable<StbEditor.Core.StoryNode> nodes) => nodes.Sum(n => 1 + Count(n.Children));
                var tree = StoryManagerWindow.Tree ?? [];
                Log.Info($"autotest-storymanager: {tree.Count} root(s), {Count(tree)} node(s), {tree.Sum(Quests)} quest slot(s); events: {string.Join(", ", tree.Where(r => r.Signature == "SMEN").Take(8).Select(r => $"{r.EditorId}[{r.Detail}]:{r.Children.Count}"))}");
                static int Quests(StbEditor.Core.StoryNode n) => n.Quests.Count + n.Children.Sum(Quests);
                _smStage = 1;
                break;
            }
            case 1 when ++_smFrames > 15:
                _smStage = 9;
                break;
        }
    }

    private int _condStage, _condFrames;
    private FormKey _condPerk, _condOther;

    /// <summary>
    /// --autotest-conditions: a perk's form on its Conditions tab; through the form's own edit target: function,
    /// parameter, operator, value, OR, Use Global, a new row, reorder; copy all and paste into another perk; all
    /// undone and redone (the screenshot shows the edited list); nothing saved.
    /// </summary>
    private void RunConditionsAutotest()
    {
        if (!Environment.GetCommandLineArgs().Contains("--autotest-conditions") || Session is not { } session || Loader is not null || _condStage == 9) return;
        var lookup = new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog);
        Mutagen.Bethesda.Skyrim.IPerkGetter? Perk(FormKey k) => (Mutagen.Bethesda.Skyrim.IPerkGetter?)StbEditor.Core.OverrideEditing.Override(Document, k)?.Record
            ?? lookup.Resolve<Mutagen.Bethesda.Skyrim.IPerkGetter>(k);
        List<string> Lines(FormKey k) => Perk(k)?.Conditions.Select(c => StbEditor.Core.Conditions.Describe(c, x => StbEditor.App.Ui.ConditionsEditor.NameOf(this, x))).ToList() ?? [];
        switch (_condStage)
        {
            case 0:
            {
                var perks = session.Catalog.BySignature["PERK"].Where(r => r.Plugins[0].Name == "Skyrim.esm").ToList();
                _condPerk = perks.First(r => lookup.Resolve<Mutagen.Bethesda.Skyrim.IPerkGetter>(r.FormKey)?.Conditions.Count >= 2).FormKey;
                _condOther = perks.First(r => r.FormKey != _condPerk && lookup.Resolve<Mutagen.Bethesda.Skyrim.IPerkGetter>(r.FormKey)?.Conditions.Count == 0).FormKey;
                FormWindows.Open(_condPerk);
                StbEditor.App.Ui.ConditionsEditor.ForceSelect = 0;
                Log.Info($"autotest-conditions: {session.Catalog.Get(_condPerk)?.EditorId}: {string.Join(" | ", Lines(_condPerk))}");
                _condStage = 1;
                break;
            }
            case 1 when ++_condFrames > 15 && FormWindows.FormOf(_condPerk)?.Target is { } target:
            {
                Log.Info($"autotest-conditions: drawn: {string.Join(" | ", StbEditor.App.Ui.ConditionsEditor.LastLines)}");
                object[] list = ["Conditions"];
                var before = Document.History.UndoName;
                target.Apply("function", r => StbEditor.Core.Conditions.SetFunction((Mutagen.Bethesda.Skyrim.Condition)StbEditor.Core.Conditions.ListAt(r, list)[0]!, Mutagen.Bethesda.Skyrim.Condition.Function.GetActorValue));
                target.Apply("parameter", r => StbEditor.Core.RecordFields.Set(r, ["Conditions", 0, "Data", "ActorValue"], Mutagen.Bethesda.Skyrim.ActorValue.Health));
                target.Apply("operator", r => ((Mutagen.Bethesda.Skyrim.Condition)StbEditor.Core.Conditions.ListAt(r, list)[0]!).CompareOperator = Mutagen.Bethesda.Skyrim.CompareOperator.GreaterThanOrEqualTo);
                target.Apply("value", r => StbEditor.Core.RecordFields.Set(r, ["Conditions", 0, "ComparisonValue"], 50f));
                target.Apply("or", r => ((Mutagen.Bethesda.Skyrim.Condition)StbEditor.Core.Conditions.ListAt(r, list)[0]!).Flags |= Mutagen.Bethesda.Skyrim.Condition.Flag.OR);
                target.Apply("global", r => { var l = StbEditor.Core.Conditions.ListAt(r, list); l[1] = StbEditor.Core.Conditions.WithGlobal((Mutagen.Bethesda.Skyrim.Condition)l[1]!, true); });
                var timescale = session.Catalog.BySignature["GLOB"].First(g => g.EditorId == "TimeScale").FormKey;
                target.Apply("global value", r => StbEditor.Core.RecordFields.Set(r, ["Conditions", 1, "ComparisonValue"], timescale));
                target.Apply("add", r => StbEditor.Core.Conditions.ListAt(r, list).Add(StbEditor.Core.Conditions.New()));
                target.Apply("move", r => { var l = StbEditor.Core.Conditions.ListAt(r, list); var item = l[^1]; l.RemoveAt(l.Count - 1); l.Insert(0, item); });
                Log.Info($"autotest-conditions: edited: {string.Join(" | ", Lines(_condPerk))}");

                StbEditor.App.Ui.ConditionsEditor.CopyForTest(Perk(_condPerk)!.Conditions);
                var other = Perk(_condOther)!;
                var copy = (Mutagen.Bethesda.Plugins.Records.IMajorRecord)StbEditor.Core.OverrideEditing.WorkingCopy(Document, _condOther, other);
                foreach (var c in StbEditor.Core.Conditions.Copy(Perk(_condPerk)!.Conditions)) StbEditor.Core.Conditions.ListAt(copy, list).Add(c);
                StbEditor.Core.OverrideEditing.CommitEdit(Document, "paste conditions", copy, StbEditor.Core.RecordPlace.TopLevel);
                Log.Info($"autotest-conditions: pasted into {session.Catalog.Get(_condOther)?.EditorId}: {Lines(_condOther).Count} condition(s), clipboard {StbEditor.App.Ui.ConditionsEditor.ClipboardCount}");

                var steps = 0;
                while (Document.History.UndoName != before && steps < 20) { Document.Undo(); steps++; }
                Log.Info($"autotest-conditions: {steps} undo steps: pending {Document.PendingCount}, lines {string.Join(" | ", Lines(_condPerk))}");
                for (var i = 0; i < steps; i++) Document.Redo();
                Log.Info($"autotest-conditions: redone: {string.Join(" | ", Lines(_condPerk))}; pending {Document.PendingCount}");
                _condStage = 2;
                _condFrames = 0;
                break;
            }
            case 2 when ++_condFrames > 15:
                Log.Info($"autotest-conditions: drawn after: {string.Join(" | ", StbEditor.App.Ui.ConditionsEditor.LastLines)}");
                _condStage = 9;
                break;
        }
    }

    private int _refMoreStage, _refMoreFrames;
    private StbEditor.Render.SceneObject[] _refMore = [];

    /// <summary>
    /// --autotest-refmore (with --cell): two references selected; «More properties» lists its fields,
    /// one change (ignored by sandbox, encounter zone) goes to both as one undo step; Ctrl+Z checked; nothing saved.
    /// </summary>
    private void RunRefMoreAutotest()
    {
        if (!Environment.GetCommandLineArgs().Contains("--autotest-refmore") || Session is not { } session || Loader is not null || _refMoreStage == 9) return;
        bool Ignored(StbEditor.Render.SceneObject o) => (StbEditor.Core.OverrideEditing.Override(Document, o.Reference)?.Record as Mutagen.Bethesda.Skyrim.IPlacedObjectGetter
            ?? o.Record as Mutagen.Bethesda.Skyrim.IPlacedObjectGetter)?.IsIgnoredBySandbox == true;
        switch (_refMoreStage)
        {
            case 0 when RenderWindow.IsSettled && RenderWindow.Scene is { IsInterior: true } scene:
                _refMore = scene.Objects.Where(o => o.Record is Mutagen.Bethesda.Skyrim.IPlacedObjectGetter { IsIgnoredBySandbox: false } && o.BaseSignature is "FURN" or "STAT" or "MISC").Take(2).ToArray();
                if (_refMore.Length < 2) { Log.Warn("autotest-refmore: fewer than 2 references"); _refMoreStage = 9; return; }
                RenderWindow.SelectForTest(_refMore);
                Select(_refMore[0].Reference);
                Log.Info($"autotest-refmore: fields: {string.Join(", ", ReferenceProperties.MoreMembers((Mutagen.Bethesda.Skyrim.IPlacedGetter)_refMore[0].Record!))}");
                var target = ReferenceProperties.MoreTarget(this, _refMore);
                target.Apply("Ignored by sandbox", r => StbEditor.Core.RecordFields.Set(r, ["IsIgnoredBySandbox"], true));
                var zone = session.Catalog.BySignature["ECZN"].First();
                target.Apply("Encounter zone", r => StbEditor.Core.RecordFields.Set(r, ["EncounterZone"], zone.FormKey));
                var zones = _refMore.Select(o => (StbEditor.Core.OverrideEditing.Override(Document, o.Reference)?.Record as Mutagen.Bethesda.Skyrim.IPlacedObjectGetter)?.EncounterZone.FormKeyNullable == zone.FormKey).Count(x => x);
                Log.Info($"autotest-refmore: ignored by sandbox {_refMore.Count(Ignored)}/2, encounter zone {zones}/2 ({Document.History.UndoName})");
                Document.Undo();
                Document.Undo();
                Log.Info($"autotest-refmore: after 2x Ctrl+Z ignored {_refMore.Count(Ignored)}/2, pending {Document.PendingCount}");
                target.Apply("Ignored by sandbox", r => StbEditor.Core.RecordFields.Set(r, ["IsIgnoredBySandbox"], true));
                _refMoreStage = 1;
                break;
            case 1 when ++_refMoreFrames > 20:
                _refMoreStage = 9;
                break;
        }
    }

    private int _sheetStage, _sheetFrames;

    /// <summary>--autotest-sheet &lt;SIG&gt;: the spreadsheet of a type; logs rows, empty cells per column, first rows; edits one weight (undone).</summary>
    private void RunSheetAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(cli, "--autotest-sheet");
        if (i < 0 || i + 1 >= cli.Length || Session is not { } session || Loader is not null || _sheetStage == 9) return;
        switch (_sheetStage)
        {
            case 0:
                Spreadsheets.Show(cli[i + 1]);
                _sheetStage = 1;
                break;
            case 1 when ++_sheetFrames > 10:
                Log.Info($"autotest-sheet: {cli[i + 1]}: {Spreadsheets.RowCount} rows; empty: {Spreadsheets.EmptyCounts}");
                foreach (var line in Spreadsheets.FirstRows(4)) Log.Info($"autotest-sheet:   {line}");
                var first = session.Catalog.BySignature[cli[i + 1]].First(r => r.EditorId?.StartsWith("Iron", StringComparison.Ordinal) == true || r.EditorId?.StartsWith("Armor", StringComparison.Ordinal) == true);
                object[] path = cli[i + 1] == "WEAP" ? ["BasicStats", "Weight"] : ["Weight"];
                var ok = Spreadsheets.EditForTest(this, first.FormKey, path, 12.5f);
                Log.Info($"autotest-sheet: edit {first.EditorId} weight: {ok}, undo: {Document.History.UndoName}, pending {Document.PendingCount}");
                Document.Undo();
                _sheetStage = 9;
                break;
        }
    }

    private int _togetherStage, _togetherFrames;
    private FormKey[] _together = [];

    /// <summary>--autotest-together: three armors edited together through one form (weight 9.5 on all); undo checked; nothing saved.</summary>
    private void RunTogetherAutotest()
    {
        if (!Environment.GetCommandLineArgs().Contains("--autotest-together") || Session is not { } session || Loader is not null || _togetherStage == 9) return;
        float? Weight(FormKey k) => ((Mutagen.Bethesda.Skyrim.IArmorGetter?)StbEditor.Core.OverrideEditing.Override(Document, k)?.Record
            ?? new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog).Resolve<Mutagen.Bethesda.Skyrim.IArmorGetter>(k))?.Weight;
        switch (_togetherStage)
        {
            case 0:
                _together = new[] { "ArmorIronHelmet", "ArmorIronBoots", "ArmorIronGauntlets" }
                    .Select(id => session.Catalog.BySignature["ARMO"].First(r => r.EditorId == id).FormKey).ToArray();
                FormWindows.OpenBatch(_together);
                _togetherStage = 1;
                break;
            case 1 when ++_togetherFrames > 5 && FormWindows.FormOf(_together[0])?.Target is { } target:
                var before = string.Join(", ", _together.Select(Weight));
                target.Apply("Weight", r => StbEditor.Core.RecordFields.Set(r, ["Weight"], 9.5f));
                var after = string.Join(", ", _together.Select(Weight));
                var undoName = Document.History.UndoName;
                Document.Undo();
                Log.Info($"autotest-together: {target.Count} records; weights {before} -> {after} ({undoName}); after Ctrl+Z {string.Join(", ", _together.Select(Weight))}");
                _togetherStage = 9;
                break;
        }
    }

    private int _batchStage, _batchFrames;
    private FormKey[] _batchRefs = [];
    private FormKey _batchBase, _batchMisc;

    /// <summary>
    /// --autotest-batch &lt;folder&gt; (with --cell of an interior): Persistent set on three references at once
    /// (Ctrl+Z / Ctrl+Y checked), the first one's base replaced (scene model swaps), a MISC copied as override;
    /// save into a new plugin, reload, check.
    /// </summary>
    private void RunBatchAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(cli, "--autotest-batch");
        if (i < 0 || i + 1 >= cli.Length || Session is not { } session || Loader is not null || _batchStage == 9) return;
        bool Persistent(FormKey k) => RenderWindow.Scene?.Objects.FirstOrDefault(o => o.Reference == k) is { } o
            && ((StbEditor.Core.OverrideEditing.Override(Document, k)?.Record ?? o.Record) as Mutagen.Bethesda.Skyrim.IPlacedGetter) is { } r && (r.MajorRecordFlagsRaw & 0x400) != 0;
        switch (_batchStage)
        {
            case 0 when RenderWindow.IsSettled && RenderWindow.Scene is { IsInterior: true } scene:
            {
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[i + 1], "STBEditorBatchAutotest.esp"), false, false, session.Game, session.Workspace.DefaultEncoding));
                var objects = scene.Objects.Where(o => o.Kind == StbEditor.Render.SceneObjectKind.Mesh && o.Record is Mutagen.Bethesda.Skyrim.IPlacedObjectGetter r && (r.MajorRecordFlagsRaw & 0x400) == 0 && !o.IsPersistent).Take(3).ToArray();
                _batchRefs = objects.Select(o => o.Reference).ToArray();
                ReferenceProperties.Apply(this, objects, "Set Persistent", r => r.MajorRecordFlagsRaw |= 0x400);
                var set = _batchRefs.Count(Persistent);
                var undoName = Document.History.UndoName;
                Document.Undo();
                var undone = _batchRefs.Count(Persistent);
                Document.Redo();
                Log.Info($"autotest-batch: Persistent on {set}/3 ({undoName}); after Ctrl+Z {undone}/3, after Ctrl+Y {_batchRefs.Count(Persistent)}/3");

                _batchBase = session.Catalog.BySignature["STAT"].First(r => r.EditorId == "Barrel02Static").FormKey;
                ReferenceProperties.Apply(this, [objects[0]], "Replace base", r => ((Mutagen.Bethesda.Skyrim.PlacedObject)r).Base.SetTo(_batchBase));
                _batchMisc = session.Catalog.BySignature["MISC"].First(r => r.EditorId == "BasicFork01").FormKey;
                RecordActions.CopyAsOverride(this, [_batchMisc]);
                _batchStage = 1;
                break;
            }
            case 1 when ++_batchFrames > 5:
                Log.Info($"autotest-batch: replaced base, scene shows {RenderWindow.Scene?.Objects.FirstOrDefault(o => o.Reference == _batchRefs[0])?.BaseEditorId}; MISC override pending: {StbEditor.Core.OverrideEditing.Override(Document, _batchMisc) is not null}; saving");
                Save();
                _batchStage = 2;
                break;
            case 2 when !Saver.IsBusy && Document.PendingCount == 0 && RenderWindow.IsSettled:
            {
                var scene = RenderWindow.Scene!;
                var first = scene.Objects.FirstOrDefault(o => o.Reference == _batchRefs[0]);
                Log.Info($"autotest-batch: reloaded; persistent {_batchRefs.Count(Persistent)}/3, first base {first?.BaseEditorId} from {first?.SourcePlugin}; BasicFork01 winner {session.Catalog.Get(_batchMisc)?.WinningPlugin.Name}");
                _batchStage = 9;
                break;
            }
        }
    }

    private int _useStage;
    private int _useFrames;
    private FormKey _useRef;

    /// <summary>
    /// --autotest-useinfo &lt;FormID|EditorID&gt; [goto]: opens Use Info, waits for the index and the field
    /// checks, logs the counts; with goto, opens the cell of the first placed reference and selects it.
    /// </summary>
    private void RunUseInfoAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(cli, "--autotest-useinfo");
        if (i < 0 || i + 1 >= cli.Length || Session is not { } session || Loader is not null || _useStage == 9) return;
        switch (_useStage)
        {
            case 0:
            {
                var key = session.Workspace.ParseFormReference(cli[i + 1])
                    ?? session.Catalog.ByKey.Values.FirstOrDefault(r => string.Equals(r.EditorId, cli[i + 1], StringComparison.OrdinalIgnoreCase))?.FormKey;
                if (key is null) { Log.Warn($"autotest-useinfo: {cli[i + 1]} not found"); _useStage = 9; return; }
                UseInfo.Open(key.Value);
                _useStage = 1;
                break;
            }
            case 1 when UseInfo.LastCounts is { Pending: 0 } counts:
                Log.Info($"autotest-useinfo: {counts.Confirmed} record(s) confirmed, {counts.Rejected} rejected raw hit(s), {counts.Placed} placed reference(s); index {session.Uses.Result.Count:N0} links in {session.Uses.Result.BuildTime.TotalSeconds:0.0} s");
                foreach (var line in UseInfo.LastRecordLines.Take(8)) Log.Info($"autotest-useinfo:   {line}");
                if (cli.Skip(i + 2).FirstOrDefault() == "goto" && UseInfo.LastPlaced.FirstOrDefault(u => u.Parent is not null) is { Parent: { } cell } first)
                {
                    _useRef = first.Source;
                    RenderWindow.GoToReference(this, first.Source, cell);
                    Log.Info($"autotest-useinfo: go to {session.Workspace.FormIdText(first.Source)} in {session.Catalog.Get(cell)?.EditorId ?? session.Workspace.FormIdText(cell)}");
                    _useStage = 2;
                }
                else _useStage = 9;
                break;
            case 2 when RenderWindow.IsSettled && ++_useFrames > 30:
                Log.Info($"autotest-useinfo: scene {RenderWindow.Scene?.Title}, selected {(RenderWindow.SingleSelection is { } s ? session.Workspace.FormIdText(s.Reference) + " " + s.BaseEditorId : "-")} (wanted {session.Workspace.FormIdText(_useRef)})");
                _useStage = 9;
                break;
        }
    }

    private int _mastersStage;
    private FormKey _mastersMisc;

    /// <summary>
    /// --autotest-masters &lt;folder&gt;: a new plugin with a MISC pointing at a Skyrim and a Dawnguard keyword,
    /// Update.esm added as an extra master (unused, between the two) → save; then Clean Masters → save:
    /// Update.esm must go, Dawnguard move down a slot and the keywords still point at the same records.
    /// </summary>
    private void RunMastersAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(cli, "--autotest-masters");
        if (i < 0 || i + 1 >= cli.Length || Session is not { } session || Loader is not null || _mastersStage == 9) return;
        string Masters() => ActivePlugin is { } a && File.Exists(a.Path) ? string.Join(", ", StbEditor.Data.Raw.PluginHeader.Read(a.Path).Masters) : "-";
        string Keywords() => new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog).Resolve<Mutagen.Bethesda.Skyrim.IMiscItemGetter>(_mastersMisc)?.Keywords?
            .Select(k => session.Catalog.Get(k.FormKey)?.EditorId ?? k.FormKey.ToString()).Aggregate((a, b) => a + ", " + b) ?? "-";
        switch (_mastersStage)
        {
            case 0:
            {
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[i + 1], "STBEditorMastersAutotest.esp"), false, false, session.Game, session.Workspace.DefaultEncoding));
                _mastersMisc = NewForm.CreateForTest("MISC", 0, "STBMastersMisc", "") ?? default;
                var misc = (Mutagen.Bethesda.Skyrim.MiscItem)StbEditor.Core.OverrideEditing.Override(Document, _mastersMisc)!.Record.DeepCopy();
                var skyrim = session.Catalog.BySignature["KYWD"].First(k => k.FormKey.ModKey.FileName == "Skyrim.esm" && k.EditorId == "VendorItemClutter");
                var dawnguard = session.Catalog.BySignature["KYWD"].First(k => k.FormKey.ModKey.FileName == "Dawnguard.esm");
                misc.Keywords = [new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.IKeywordGetter>(skyrim.FormKey), new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.IKeywordGetter>(dawnguard.FormKey)];
                StbEditor.Core.OverrideEditing.CommitEdit(Document, "keywords", misc, StbEditor.Core.RecordPlace.TopLevel);
                Document.EditMasters("add", new StbEditor.Core.MasterEdit([Mutagen.Bethesda.Plugins.ModKey.FromFileName("Update.esm")], false));
                Log.Info($"autotest-masters: MISC with {skyrim.EditorId} + {dawnguard.EditorId}, Update.esm requested; saving");
                Save();
                _mastersStage = 1;
                break;
            }
            case 1 when !Saver.IsBusy && Document.PendingCount == 0:
                Log.Info($"autotest-masters: saved; masters [{Masters()}], keywords [{Keywords()}]; cleaning");
                Document.EditMasters("clean", new StbEditor.Core.MasterEdit([], true));
                Save();
                _mastersStage = 2;
                break;
            case 2 when Saver.AwaitingConfirmation is { } pending:
                Log.Warn($"autotest-masters: save check asks: {string.Join("; ", pending.LossyRecords)}");
                Saver.Confirm(this);
                break;
            case 2 when !Saver.IsBusy && Document.PendingCount == 0:
                Log.Info($"autotest-masters: cleaned; masters [{Masters()}], keywords [{Keywords()}]");
                _mastersStage = 9;
                break;
        }
    }

    private int _filterStage;
    private System.Diagnostics.Stopwatch? _filterClock;

    /// <summary>
    /// --autotest-filter &lt;plugin&gt; [conflicts] (with --type SIG): the Object Window's plugin filter, then
    /// the conflict filter; logs the row counts and how long the field-by-field check took.
    /// </summary>
    private void RunFilterAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(cli, "--autotest-filter");
        if (i < 0 || i + 1 >= cli.Length || Session is null || _filterStage == 2) return;
        var conflicts = cli.Skip(i + 2).FirstOrDefault() == "conflicts";
        switch (_filterStage)
        {
            case 0:
                ObjectWindow.SetFiltersForTest(cli[i + 1], conflicts);
                _filterClock = System.Diagnostics.Stopwatch.StartNew();
                _filterStage = 1;
                break;
            case 1 when !ObjectWindow.IsScanning && _filterClock!.ElapsedMilliseconds > 500:
                Log.Info($"autotest-filter: plugin {cli[i + 1]}{(conflicts ? ", conflicts" : "")}: {ObjectWindow.RowCount} row(s); {ObjectWindow.ConflictsChecked} record(s) checked in {_filterClock.ElapsedMilliseconds} ms");
                foreach (var key in ObjectWindow.VisibleKeys.Take(6))
                {
                    var versions = CompareWindows.Versions(this, key);
                    var root = RecordCompare.Build(this, versions.Select(v => (object?)v.Record).Reverse().ToList(), versions: true, showHeader: false);
                    var lost = new List<string>();
                    void Walk(CompareNode n, string path) { foreach (var c in n.Children) { if (c.Children.Count == 0 && c.State == CompareState.Conflict) lost.Add($"{path}{c.Label}: {string.Join(" | ", c.Text)}"); Walk(c, path + c.Label + "/"); } }
                    Walk(root, "");
                    Log.Info($"autotest-filter:   {Session!.Catalog.Get(key)?.EditorId} [{string.Join(", ", versions.Select(v => v.Plugin).Reverse())}]: {string.Join("; ", lost.Take(4))}");
                }
                _filterStage = 2;
                break;
        }
    }

    private int _opsStage;
    private FormKey _opsMisc, _opsGlobal, _opsDropped, _opsSourceRef;
    private System.Numerics.Vector3 _opsSourcePosition;
    private int _opsSourceFlags;

    /// <summary>
    /// --autotest-ops &lt;folder&gt; (with --cell of an interior): the Object Window's New (a named MISC, a float
    /// global), Delete of a new record, copy + paste of a reference (its record copied), save into a new plugin,
    /// reload and check; then Delete of the plugin's own record, save, reload, check it is gone.
    /// </summary>
    private void RunOpsAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(cli, "--autotest-ops");
        if (i < 0 || i + 1 >= cli.Length || Session is not { } session || Loader is not null || _opsStage == 9) return;
        switch (_opsStage)
        {
            case 0 when RenderWindow.IsSettled && RenderWindow.Scene is { IsInterior: true } scene:
            {
                SetActivePlugin(StbEditor.Core.ActivePlugin.ForNew(Path.Combine(cli[i + 1], "STBEditorOpsAutotest.esp"), false, false, session.Game, session.Workspace.DefaultEncoding));
                _opsMisc = NewForm.CreateForTest("MISC", 0, "STBOpsMisc", "Тестовая вещь") ?? default;
                _opsGlobal = NewForm.CreateForTest("GLOB", 0, "STBOpsGlobal", "") ?? default;
                _opsDropped = NewForm.CreateForTest("MISC", 0, "STBOpsDropped", "") ?? default;
                var removal = RecordActions.RemovalOf(this, _opsDropped);
                RecordActions.Remove(this, _opsDropped);
                Log.Info($"autotest-ops: new {Describe(_opsMisc)}, {Describe(_opsGlobal)}; dropped ({removal}) still in catalog: {session.Catalog.Get(_opsDropped) is not null}");

                var source = scene.Objects.First(o => o.Kind == StbEditor.Render.SceneObjectKind.Mesh && o.Record is Mutagen.Bethesda.Skyrim.IPlacedObjectGetter p && p.Placement is not null && !o.IsPersistent);
                _opsSourceRef = source.Reference;
                _opsSourcePosition = source.Position;
                _opsSourceFlags = ((Mutagen.Bethesda.Skyrim.IPlacedObjectGetter)source.Record!).MajorRecordFlagsRaw;
                RenderWindow.SelectForTest(source);
                RenderWindow.CopySelection(this);
                var before = scene.Objects.Count;
                RenderWindow.Paste(this, inPlace: false, new System.Numerics.Vector2(-1, -1));
                var pasted = RenderWindow.SingleSelection;
                Log.Info($"autotest-ops: copied {source.BaseEditorId} {source.Reference} at {source.Position}; pasted {pasted?.Reference} at {pasted?.Position}, objects {before} -> {scene.Objects.Count}, record copied: {StbEditor.Core.OverrideEditing.Override(Document, pasted!.Reference) is not null}; undo: {Document.History.UndoName}");
                Save();
                _opsStage = 1;
                break;
            }
            case 1 when !Saver.IsBusy && Document.PendingCount == 0 && RenderWindow.IsSettled && RenderWindow.Scene is { } scene:
            {
                var misc = session.Catalog.Get(_opsMisc);
                var mine = scene.Objects.Where(o => o.Reference.ModKey == ActivePlugin!.ModKey).ToList();
                var copy = mine.FirstOrDefault(o => o.Base == scene.Objects.FirstOrDefault(s => s.Reference == _opsSourceRef)?.Base);
                var flags = copy?.Record is Mutagen.Bethesda.Skyrim.IPlacedObjectGetter p ? p.MajorRecordFlagsRaw : -1;
                Log.Info($"autotest-ops: reloaded; MISC {misc?.EditorId} '{misc?.Name}' from {misc?.WinningPlugin.Name}, GLOB {session.Catalog.Get(_opsGlobal)?.EditorId}, dropped present: {session.Catalog.Get(_opsDropped) is not null}; " +
                    $"own references in the cell {mine.Count}, copy at {copy?.Position} flags 0x{flags:X} (source 0x{_opsSourceFlags:X})");
                var removal = RecordActions.RemovalOf(this, _opsMisc);
                RecordActions.Remove(this, _opsMisc);
                Log.Info($"autotest-ops: delete {misc?.EditorId}: {removal}, marked {StbEditor.Core.OverrideEditing.IsRemoved(Document, _opsMisc)}; saving");
                Save();
                _opsStage = 2;
                break;
            }
            case 2 when !Saver.IsBusy && Document.PendingCount == 0 && RenderWindow.IsSettled:
                Log.Info($"autotest-ops: reloaded; deleted MISC present: {session.Catalog.Get(_opsMisc) is not null}, GLOB present: {session.Catalog.Get(_opsGlobal) is not null}");
                _opsStage = 9;
                break;
        }

        string Describe(FormKey key) => session.Catalog.Get(key) is { } r ? $"{r.Signature} {r.EditorId} \"{r.Name}\" {key}" : $"{key} (not in catalog)";
    }

    private int _saveCheckStage;

    /// <summary>
    /// --autotest-savecheck &lt;folder&gt; &lt;plugin&gt;: on a copy of the plugin in the folder, override a record of
    /// a plugin that is not its master and save: the save check (new master, records written differently)
    /// stays open for a screenshot. Nothing is written.
    /// </summary>
    private void RunSaveCheckAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(cli, "--autotest-savecheck");
        if (i < 0 || i + 2 >= cli.Length || Session is not { } session || _saveCheckStage == 2) return;
        if (_saveCheckStage == 1)
        {
            if (Saver.AwaitingConfirmation is not { } pending) return;
            Log.Info($"autotest-savecheck: added masters [{string.Join(", ", pending.AddedMasters)}], {pending.LossyRecords.Count} record(s) written differently: {string.Join("; ", pending.LossyRecords)}");
            _saveCheckStage = 2;
            return;
        }

        var plugin = session.Workspace.Active.First(p => p.Name.Equals(cli[i + 2], StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(cli[i + 1]);
        var copy = Path.Combine(cli[i + 1], plugin.Name);
        File.Copy(plugin.Location.Path, copy, overwrite: true);
        SetActivePlugin(StbEditor.Core.ActivePlugin.ForExisting(plugin, session.Game).RetargetedTo(copy));

        var masters = StbEditor.Data.Raw.PluginHeader.Read(copy).Masters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var order = session.Workspace.Active.Select(p => p.Name.ToLowerInvariant()).ToList();
        var self = order.IndexOf(plugin.Name.ToLowerInvariant());
        var target = session.Catalog.BySignature["MISC"].FirstOrDefault(r => r.FormKey.ModKey.FileName.String is var origin
            && !masters.Contains(origin) && order.IndexOf(origin.ToLowerInvariant()) is var at && at >= 0 && at < self);
        if (target is null) { Log.Warn("autotest-savecheck: no record outside the masters"); _saveCheckStage = 2; return; }
        var winning = new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog).Resolve<Mutagen.Bethesda.Skyrim.IMiscItemGetter>(target.FormKey)!;
        var edited = StbEditor.Core.OverrideEditing.WorkingCopy(Document, target.FormKey, winning);
        edited.EditorID += "Edited";
        StbEditor.Core.OverrideEditing.CommitEdit(Document, "edit", edited, StbEditor.Core.RecordPlace.TopLevel);
        Log.Info($"autotest-savecheck: edited {target.EditorId} from {target.FormKey.ModKey}, saving into a copy of {plugin.Name}");
        Save();
        _saveCheckStage = 1;
    }

    private int _compareStage;
    private int _compareFrames;

    /// <summary>
    /// --autotest-compare &lt;FormID&gt; [FormID...] [versions|records]: opens the version comparison of the
    /// first record, then picks all the given records in the Object Window (as Ctrl+click) and compares
    /// them; logs rows per colour state and the differing rows. Leaves the windows open for a screenshot
    /// (versions / records: keep only that window).
    /// </summary>
    private void RunCompareAutotest()
    {
        var cli = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(cli, "--autotest-compare");
        if (i < 0 || Session is not { } session || _compareStage == 3) return;
        var keys = cli.Skip(i + 1).TakeWhile(a => !a.StartsWith("--")).Select(a => session.Workspace.ParseFormReference(a)).OfType<FormKey>().ToList();
        var only = cli.Skip(i + 1).TakeWhile(a => !a.StartsWith("--")).FirstOrDefault(a => a is "versions" or "records");
        if (keys.Count == 0) { Log.Warn("autotest-compare: no FormIDs"); _compareStage = 3; return; }
        if (++_compareFrames < 20) return;
        _compareFrames = 0;

        void Report(string what)
        {
            var root = Compare.LastRoot;
            if (root is null) { Log.Warn($"autotest-compare: {what}: nothing drawn"); return; }
            var all = new List<CompareNode>();
            void Walk(CompareNode n) { foreach (var c in n.Children) { all.Add(c); Walk(c); } }
            Walk(root);
            var leaves = all.Where(n => n.Children.Count == 0).ToList();
            Log.Info($"autotest-compare: {what}: columns [{string.Join(" | ", Compare.LastColumns.Select(c => c.Title))}], {all.Count} rows, " +
                string.Join(", ", leaves.GroupBy(n => n.State).Select(g => $"{g.Key} {g.Count()}")));
            foreach (var n in leaves.Where(n => n.State != CompareState.Same).OrderByDescending(n => n.State == CompareState.Conflict).Take(14))
                Log.Info($"autotest-compare:   {n.State,-8} {n.Label,-22} {string.Join(" | ", n.Text.Select(t => t.Length > 40 ? t[..40] + "…" : t))}");
        }

        switch (_compareStage)
        {
            case 0:
                Compare.OpenVersions(keys[0]);
                _compareStage = 1;
                break;
            case 1:
                Report($"versions of {session.Catalog.Get(keys[0])?.EditorId}");
                if (only == "records") Compare.CloseAll();
                if (keys.Count > 1)
                {
                    ObjectWindow.Pick(keys);
                    Select(keys[0]);
                    Compare.OpenRecords(keys);
                }
                _compareStage = 2;
                break;
            case 2:
                if (keys.Count > 1) Report($"{keys.Count} records");
                if (only == "versions" && keys.Count > 1) Compare.CloseLast();
                _compareStage = 3;
                break;
        }
    }

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
            if (ImGui.MenuItem(T("Masters..."), (string?)null, false, Session is not null)) _mastersWindow.Show();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("The active file's masters: add one, remove unused ones (as xEdit)"));
            if (ImGui.MenuItem(T("Check and clean..."), (string?)null, false, Session is not null)) PluginCheck.Show();
            if (ImGui.MenuItem(T("Pack into BSA..."), (string?)null, false, Session is not null)) Archive.Show();
            if (ImGui.MenuItem(T("Compact FormIDs..."), (string?)null, false, Session is not null)) Compact.Show();
            if (ImGui.MenuItem(T("Texts for translation..."), (string?)null, false, Session is not null)) Texts.Show(this);
            if (ImGui.MenuItem(T("Scripts..."), (string?)null, false, Session is not null)) ScriptsWindow.Show();
            if (ImGui.MenuItem(T("Export data..."), (string?)null, false, Session is not null)) ExportDataWindow.Show(this);
            if (ImGui.MenuItem(T("ModGroups..."), (string?)null, false, Session is not null)) ModGroupsWindow.Show();
            if (ImGui.MenuItem(T("Compare with another file..."), (string?)null, false, Session is not null)) CompareFile.Show(this);
            if (ImGui.MenuItem(T("Merge leveled lists..."), (string?)null, false, Session is not null)) LeveledMergeWindow.Show();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Renumber the active file's records into 0x800-0xFFF and mark it light (xEdit: Compact FormIDs for ESL)"));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Meshes, textures, scripts and sounds next to the active file into its archives (CK: Create Archive)"));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Errors, ITM and deleted references of a plugin (xEdit: Check for Errors, Quick Auto Clean)"));
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
            if (ImGui.MenuItem(T("Find text..."), T("Ctrl+Shift+F"), false, Session is not null)) FindText.Show();
            ImGui.Separator();
            // Render Window references, as the CK's Cut / Copy / Paste Render.
            if (ImGui.MenuItem(T("Cut objects"), T("Ctrl+X"), false, RenderWindow.HasSelection)) RenderWindow.CutSelection(this);
            if (ImGui.MenuItem(T("Copy objects"), T("Ctrl+C"), false, RenderWindow.HasSelection)) RenderWindow.CopySelection(this);
            if (ImGui.MenuItem(T("Paste objects"), T("Ctrl+V"), false, RenderWindow.ClipboardCount > 0)) RenderWindow.Paste(this, inPlace: false, new System.Numerics.Vector2(-1, -1));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Into the loaded cell, at the centre of the view (Ctrl+V over the view: under the cursor)"));
            if (ImGui.MenuItem(T("Paste in place"), T("Ctrl+Shift+V"), false, RenderWindow.ClipboardCount > 0)) RenderWindow.Paste(this, inPlace: true, default);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Same coordinates as the originals (copy in one cell, paste in another)"));
            ImGui.Separator();
            ImGui.TextColored(Theme.TextDim, TF($"{Document.ChangedCount} changed reference(s), {Document.Drafts.Count} record edit(s), not saved"));
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu(T("World")))
        {
            var ready = Session is not null;
            if (ImGui.MenuItem(T("New Interior Cell..."), (string?)null, false, ready)) _newRecordDialog.ShowInteriorCell();
            if (ImGui.MenuItem(T("New Worldspace..."), (string?)null, false, ready)) _newRecordDialog.ShowWorldspace();
            ImGui.Separator();
            if (ImGui.MenuItem(T("Object palette..."), (string?)null, false, ready)) PaletteWindow.Show();
            if (ImGui.MenuItem(T("Layers..."), (string?)null, false, ready)) LayersWindow.Show();
            if (ImGui.MenuItem(T("Story Manager..."), (string?)null, false, ready)) StoryManagerWindow.Show(this);
            if (ImGui.MenuItem(T("Idle animations..."), (string?)null, false, ready)) IdleTreeWindow.Show();
            if (ImGui.MenuItem(T("Voice files..."), (string?)null, false, ready)) VoiceFilesWindow.Show(this);
            if (ImGui.MenuItem(T("Regions..."), (string?)null, false, ready)) RegionsWindow.Show(this);
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
            if (ImGui.MenuItem(T("Spreadsheets..."), (string?)null, false, Session is not null)) Spreadsheets.Show();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Weapons, armor, ammunition and other items as sortable tables, edited in place (as xEdit's spreadsheets)"));
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

    /// <summary>Ctrl+= / Ctrl+- / Ctrl+0: interface larger / smaller / 100 % (saved like the setting).</summary>
    public void SetUiScale(float scale)
    {
        Theme.SetUiScale(scale);
        _settings.UiScale = Theme.UiScale;
    }

    private void HandleShortcuts()
    {
        var keys = ImGui.GetIO();
        if (keys.KeyCtrl && !keys.WantTextInput)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Equal, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadAdd, false)) SetUiScale(Theme.UiScale + 0.1f);
            if (ImGui.IsKeyPressed(ImGuiKey.Minus, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadSubtract, false)) SetUiScale(Theme.UiScale - 0.1f);
            if (ImGui.IsKeyPressed(ImGuiKey.Key0, false) || ImGui.IsKeyPressed(ImGuiKey.Keypad0, false)) SetUiScale(1f);
        }
        var io = ImGui.GetIO();
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.O, false)) _dataDialog.Show();
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S, false)) Save();
        if (io.KeyCtrl && io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.F, false) && Session is not null) FindText.Show();
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
