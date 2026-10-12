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
/// File → Check and clean (xEdit: Check for Errors, Quick Auto Clean): a plugin's errors and warnings,
/// overrides identical to their master (ITM) and deleted references (UDR). For the active file the
/// fixes are edits like any other (one Ctrl+Z, saved with Ctrl+S): ITM overrides taken out, deleted
/// references undeleted and disabled.
/// </summary>
internal sealed class PluginCheckWindow
{
    private bool _open;
    private string? _plugin;
    private Task<(List<CheckIssue> Issues, CleanReport Clean)>? _task;
    private (string Plugin, List<CheckIssue> Issues, CleanReport Clean)? _result;
    private volatile string _progress = "";

    public bool IsBusy => _task is not null;

    /// <summary>Autotests: the last result.</summary>
    public (List<CheckIssue> Issues, CleanReport Clean)? Result => _result is { } r ? (r.Issues, r.Clean) : null;

    public void Show(string? plugin = null)
    {
        _open = true;
        if (plugin is not null) _plugin = plugin;
    }

    public void Start(EditorApp app, string pluginName)
    {
        if (app.Session is not { } session || _task is not null) return;
        if (session.Workspace.Active.FirstOrDefault(p => p.Name.Equals(pluginName, StringComparison.OrdinalIgnoreCase)) is not { IsLoaded: true } plugin)
        {
            Log.Warn(TF($"{pluginName} is not loaded"));
            return;
        }
        _plugin = plugin.Name;
        var progress = new Progress<string>(m => _progress = m);
        _task = Task.Run(() =>
        {
            var issues = PluginChecks.Errors(session.Workspace, session.Catalog, plugin, session.Assets.Exists, progress,
                assetOrigin: path => session.Assets.Find(path) is { } source ? source.LoosePath ?? source.ArchivePath : null);
            var clean = PluginChecks.Clean(session.Workspace, session.Catalog, plugin, progress);
            return (issues, clean);
        });
    }

    public void Draw(EditorApp app)
    {
        Poll();
        if (!_open || app.Session is not { } session) return;
        ImGui.SetNextWindowSize(app.Scaled(900, 600), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Check and clean") + "###plugincheck", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            _plugin ??= app.ActivePlugin?.Name ?? session.Workspace.Active.LastOrDefault(p => p.IsLoaded)?.Name;
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 18);
            if (ImGui.BeginCombo("##plugin", _plugin ?? ""))
            {
                foreach (var plugin in session.Workspace.Active.Where(p => p.IsLoaded))
                    if (ImGui.Selectable(plugin.Name, plugin.Name == _plugin)) _plugin = plugin.Name;
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(_task is not null || _plugin is null);
            if (ImGui.Button(_task is not null ? T("checking…") : T("Check"))) Start(app, _plugin!);
            ImGui.EndDisabled();
            if (_task is not null)
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.Warning, _progress);
            }
            if (_result is { } result) DrawResult(app, session, result);
            else if (_task is null) ImGui.TextColored(Theme.TextDim, T("Errors (links to missing records, deleted records, leveled lists that contain themselves, NPCs without FaceGen), ITM and deleted references (UDR) of the plugin."));
        }
        ImGui.End();
    }

    private void Poll()
    {
        if (_task is not { IsCompleted: true } task) return;
        _task = null;
        if (task.IsCompletedSuccessfully)
        {
            _result = (_plugin!, task.Result.Issues, task.Result.Clean);
            Log.Info($"check {_plugin}: {task.Result.Issues.Count(i => i.Error)} error(s), {task.Result.Issues.Count(i => !i.Error)} warning(s), {task.Result.Clean.Itm.Count} ITM, {task.Result.Clean.DeletedReferences.Count} deleted reference(s)");
        }
        else Log.Error($"check failed: {task.Exception?.GetBaseException().Message}");
    }

    private void DrawResult(EditorApp app, EditorSession session, (string Plugin, List<CheckIssue> Issues, CleanReport Clean) result)
    {
        var isActive = app.ActivePlugin?.Name.Equals(result.Plugin, StringComparison.OrdinalIgnoreCase) == true;
        if (!isActive) ImGui.TextColored(Theme.TextDim, TF($"Fixes apply to the active file: make {result.Plugin} active to clean it."));
        if (!ImGui.BeginTabBar("##checktabs")) return;
        var errors = result.Issues.Where(i => i.Error).ToList();
        var warnings = result.Issues.Where(i => !i.Error).ToList();
        if (ImGui.BeginTabItem(TF($"Errors ({errors.Count})###errors"))) { DrawIssues(app, session, errors); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem(TF($"Warnings ({warnings.Count})###warnings")))
        {
            var tints = warnings.Where(w => w.Fix == "BodyTint").ToList();
            if (tints.Count > 0)
            {
                ImGui.BeginDisabled(!isActive);
                if (ImGui.Button(TF($"Update {tints.Count} body tint(s)"))) FixBodyTints(app, session, tints);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(T("Body tint from the skin tone, as the CK's Update NPC Body Tint Colors (one undo step)"));
            }
            DrawIssues(app, session, warnings);
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(TF($"ITM ({result.Clean.Itm.Count})###itm")))
        {
            ImGui.TextColored(Theme.TextDim, TF($"Overrides identical to the master's version ({result.Clean.Compared:N0} compared). Cells, worldspaces and topics stay (they hold other records)."));
            var removable = result.Clean.Itm.Where(i => i.Signature is not ("CELL" or "WRLD" or "DIAL")).ToList();
            ImGui.BeginDisabled(!isActive || removable.Count == 0);
            if (ImGui.Button(TF($"Remove {removable.Count} ITM from {result.Plugin}"))) RemoveItm(app, removable);
            ImGui.EndDisabled();
            DrawRows(app, session, result.Clean.Itm.Select(i => (i.Key, i.Signature, i.EditorId, TF($"same as in {i.Master}"))).ToList());
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem(TF($"Deleted references ({result.Clean.DeletedReferences.Count})###udr")))
        {
            ImGui.TextColored(Theme.TextDim, T("A deleted reference crashes the game when a script or package still points at it: undelete it and disable it instead (xEdit: Undelete and Disable References)."));
            ImGui.BeginDisabled(!isActive || result.Clean.DeletedReferences.Count == 0);
            if (ImGui.Button(TF($"Undelete and disable {result.Clean.DeletedReferences.Count}"))) FixDeleted(app, session, result.Clean.DeletedReferences);
            ImGui.EndDisabled();
            DrawRows(app, session, result.Clean.DeletedReferences.Select(d => (d.Key, d.Signature, (string?)null, d.Override ? TF($"in {session.Catalog.Get(d.Cell)?.EditorId ?? d.Cell.ToString()}") : TF($"own reference in {session.Catalog.Get(d.Cell)?.EditorId ?? d.Cell.ToString()}"))).ToList());
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    /// <summary>The NPCs' body tint set from their skin tone (CK: Update NPC Body Tint Colors), one undo step; returns how many.</summary>
    public static int FixBodyTints(EditorApp app, EditorSession session, IReadOnlyList<CheckIssue> issues)
    {
        var lookup = new StbEditor.Data.RecordLookup(session.Workspace, session.Catalog);
        var edits = new TextEdits(app, T("Update NPC body tints"));
        foreach (var issue in issues.Where(i => i.Fix == "BodyTint"))
            edits.Edit(issue.Key, null, copy =>
            {
                if (copy is not Mutagen.Bethesda.Skyrim.Npc npc || lookup.Resolve<Mutagen.Bethesda.Skyrim.IRaceGetter>(npc.Race.FormKey) is not { } race
                    || BodyTint.Expected(npc, race) is not { } color) return false;
                npc.TextureLighting = color;
                return true;
            });
        edits.Commit();
        Log.Info($"body tints updated: {edits.Records}");
        return edits.Records;
    }

    private static void DrawIssues(EditorApp app, EditorSession session, List<CheckIssue> issues) =>
        DrawRows(app, session, issues.Select(i => (i.Key, i.Signature, i.EditorId, i.Message)).ToList());

    private static void DrawRows(EditorApp app, EditorSession session, List<(FormKey Key, string Signature, string? EditorId, string Text)> rows)
    {
        if (rows.Count == 0) { ImGui.TextColored(Theme.TextDim, T("Nothing found.")); return; }
        if (!ImGui.BeginTable("##rows", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("Type"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 3);
        ImGui.TableSetupColumn(T("Editor ID"), ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(T("Form ID"), ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * 5);
        ImGui.TableSetupColumn(T("Problem"), ImGuiTableColumnFlags.WidthStretch, 2.5f);
        ImGui.TableHeadersRow();
        var clipper = new ImGuiListClipper();
        clipper.Begin(rows.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var row = rows[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, row.Signature);
                ImGui.TableNextColumn();
                ImGui.PushID(i);
                if (ImGui.Selectable(row.EditorId ?? session.Catalog.Get(row.Key)?.EditorId ?? "", false, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick)
                    && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && session.Catalog.Get(row.Key) is not null)
                {
                    app.Select(row.Key);
                    app.FormWindows.Open(row.Key);
                }
                ImGui.PopID();
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextDim, session.Workspace.FormIdText(row.Key));
                ImGui.TableNextColumn();
                ImGui.TextWrapped(row.Text);
            }
        }
        clipper.End();
        ImGui.EndTable();
    }

    /// <summary>Every ITM override out of the active file, one undo step.</summary>
    public static void RemoveItm(EditorApp app, IReadOnlyList<ItmRecord> itm)
    {
        var name = TF($"Remove {itm.Count} ITM");
        var steps = itm.Select(i => (IEditCommand)new RemoveRecordCommand(name, app.Document.Draft(i.Key), new RemovalDraft(i.Key, i.Signature, i.EditorId, false), i.Key)).ToList();
        app.Document.Record(new CompositeCommand(name, steps));
        Log.Info(TF($"{itm.Count} ITM override(s) will be taken out of {app.ActivePlugin?.Name} on save"));
    }

    /// <summary>
    /// Deleted references: overrides come back as the master's version, initially disabled under the
    /// world (xEdit's UDR, done by the writer); the plugin's own deleted references are taken out.
    /// </summary>
    public static void FixDeleted(EditorApp app, EditorSession session, IReadOnlyList<DeletedReference> deleted)
    {
        var lookup = new RecordLookup(session.Workspace, session.Catalog);
        var changes = new List<(FormKey, RefState, RefState, RefState)>();
        var drafts = new List<Draft>();
        var removals = new List<IEditCommand>();
        var name = TF($"Undelete and disable {deleted.Count} reference(s)");
        foreach (var d in deleted)
        {
            if (!d.Override)
            {
                removals.Add(new RemoveRecordCommand(name, app.Document.Draft(d.Key), new RemovalDraft(d.Key, d.Signature, null, true), d.Key));
                continue;
            }
            // The version before the plugin's (from its masters): the deleted copy may have lost its data.
            var versions = lookup.CellVersionsByPlugin(d.Cell);
            var masters = app.ActivePlugin is { } active
                ? session.Workspace.Active.FirstOrDefault(p => p.Name == active.Name)?.Header?.Masters.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? []
                : [];
            var master = versions.Where(v => masters.Contains(v.Plugin.Name)).Reverse()
                .Select(v => v.Cell.Persistent.Concat(v.Cell.Temporary).FirstOrDefault(r => r.FormKey == d.Key))
                .FirstOrDefault(r => r is not null) as IPlacedGetter;
            if (master?.Placement is not { } placement) { Log.Warn(TF($"{d.Key}: no master version found, skipped")); continue; }
            var placementCell = session.Catalog.Get(d.Cell)?.Winner.Cell;
            var baseKey = master switch { IPlacedObjectGetter o => o.Base.FormKey, IPlacedNpcGetter n => n.Base.FormKey, _ => FormKey.Null };
            var context = new RefContext(d.Cell, placementCell?.Worldspace, d.Persistent, placementCell?.IsInterior ?? true, master, baseKey,
                session.Catalog.Get(baseKey)?.Signature ?? "");
            app.Document.RegisterContext(d.Key, context);
            drafts.Add(new OverrideDraft((IMajorRecord)master.DeepCopy(), RecordPlace.Reference, context));
            var state = new RefState(new Vector3(placement.Position.X, placement.Position.Y, placement.Position.Z),
                new Vector3(placement.Rotation.X, placement.Rotation.Y, placement.Rotation.Z), (master as IPlacedObjectGetter)?.Scale ?? 1f, false);
            changes.Add((d.Key, state, state, state with { Deleted = true }));
        }
        if (changes.Count > 0) app.Document.Execute(name, changes, drafts);
        if (removals.Count > 0) app.Document.Record(new CompositeCommand(name, removals));
        Log.Info(TF($"{changes.Count} deleted reference(s) will be undeleted and disabled, {removals.Count} own one(s) taken out, on save"));
    }
}
