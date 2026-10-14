using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data.Catalog;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// A record as an editable form (the Inspector and the separate form windows): tabs laid out like
/// the Creation Kit's dialogs (<see cref="FormView"/>), scripts, every field, every plugin's version.
/// </summary>
internal sealed class RecordForm
{
    /// <summary>Records edited together with the shown one (same type): every change goes to all of them.</summary>
    public IReadOnlyList<FormKey>? Batch { get; set; }

    /// <summary>The edit target of the last drawn frame (autotests apply field changes through it).</summary>
    public EditTarget? Target { get; private set; }

    private FormKey? _loadedFor;
    private StbEditor.App.Services.EditorSession? _loadedIn;
    private List<(string Plugin, IMajorRecordGetter Record)> _versions = [];
    private string? _error;
    private StbEditor.Render.Scene? _errorScene;

    /// <summary>Why the record could not be shown, and the versions shown (autotests read them).</summary>
    public string? Error => _error;
    public IReadOnlyList<(string Plugin, IMajorRecordGetter Record)> Versions => _versions;

    /// <summary>Records that do not sit in a top-level group (or need their own tools) are not field-edited here.</summary>
    private static readonly HashSet<string> NotFieldEditable = ["CELL", "WRLD", "LAND", "NAVM", "NAVI", "DIAL", "INFO", "TES4"];

    /// <summary>Draws the form of <paramref name="key"/>; <paramref name="inWindow"/>: a form window (no reference tab, no "open in window").</summary>
    public void Draw(EditorApp app, FormKey key, bool inWindow)
    {
        var session = app.Session!;
        // A placed reference is found through the loaded scene: try again once a scene is there. After a save
        // everything is read again: the versions (and a new reference's record) come from the new load.
        if (_loadedFor != key || _loadedIn != session || (_error is not null && _errorScene != app.RenderWindow.Scene)) Load(app, key);

        var catalog = session.Catalog.Get(key);
        var placedObject = catalog is null ? app.RenderWindow.Scene?.Objects.FirstOrDefault(o => o.Reference == key) : null;
        var type = catalog is not null ? RecordTypes.Get(catalog.Signature)
            : placedObject is not null ? new RecordTypeInfo(placedObject.Kind == StbEditor.Render.SceneObjectKind.Actor ? "ACHR" : "REFR", "Reference", "") : null;

        ImGui.PushFont(app.Ui.HeaderFont, 0);
        ImGui.Text(catalog?.EditorId ?? placedObject?.Record?.EditorID ?? (placedObject is not null ? placedObject.BaseEditorId ?? "" : T("(no editor id)")));
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextDim, $"{T(type?.Name ?? "?")} [{type?.Signature}]  {session.Workspace.FormIdText(key)}");
        if (placedObject is not null)
        {
            ImGui.TextColored(Theme.TextDim, TF($"base {placedObject.BaseEditorId} [{placedObject.BaseSignature}]"));
            ImGui.SameLine();
            if (ImGui.SmallButton(T("Open base"))) app.Select(placedObject.Base);
        }
        if (catalog?.Name is { Length: > 0 } name) ImGui.TextColored(Theme.Accent, name);

        var removed = app.Document.IsRemoved(key);
        var editable = !removed && (placedObject is not null || (catalog is not null && !NotFieldEditable.Contains(catalog.Signature)));
        if (!inWindow && placedObject is null && _versions.Count > 0)
        {
            if (ImGui.SmallButton(T("Open in window"))) app.FormWindows.Open(key);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("A separate, larger window for this record (as the Creation Kit's form dialogs); double-click in the Object Window does the same"));
            ImGui.SameLine();
        }
        if (ImGui.SmallButton(T("Copy FormKey"))) ImGui.SetClipboardText(key.ToString());
        ImGui.SameLine();
        if (ImGui.SmallButton(T("Use Info"))) app.UseInfo.Open(key);
        // Framework configs that give, swap or list this record (SPID, KID, BOS…): a chip that opens them in Use Info.
        if (app.Session!.Frameworks is { IsCompletedSuccessfully: true } frameworks && frameworks.Result.Of(key) is { Count: > 0 } refs)
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
            if (ImGui.SmallButton(TF($"Frameworks: {string.Join(", ", refs.Select(r => r.Framework).Distinct())} ({refs.Count})"))) app.UseInfo.Open(key);
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(string.Join("\n", refs.Take(8).Select(r => $"{r.Framework}: {FrameworksView.RoleText(r)} — {r.Mod}")) + (refs.Count > 8 ? "\n…" : ""));
        }
        else _ = app.Session!.Frameworks;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Which records use this one and where it is placed in the world (CK: Use Info, xEdit: Referenced By)"));
        if (placedObject is null && editable && _versions.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(T("Duplicate"))) Duplicate(app, key);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("New record in the active file: a copy with a new FormID and EditorID (as the CK's Duplicate)"));
        }
        if (placedObject is null && !removed && RecordActions.RemovalOf(app, key) is var removal && removal != RecordActions.Removal.None)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(RecordActions.Label(app, removal))) RecordActions.Remove(app, key);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(RecordActions.Tooltip(app, removal, key));
        }
        if (app.Document.Override(key) is not null)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.Warning, T("changed, not saved (Ctrl+S)"));
        }
        if (removed)
        {
            ImGui.TextColored(Theme.Error, TF($"Taken out of {app.ActivePlugin?.Name ?? T("the active file")} when you save (Ctrl+Z brings it back)."));
            ImGui.TextColored(Theme.TextDim, T("Shown as it is now; editing is off until the removal is undone."));
        }

        if (_error is not null)
        {
            ImGui.TextColored(Theme.Error, _error);
            return;
        }
        if (_versions.Count == 0) return;
        // A placed reference whose cell has just been unloaded (the camera flew on and the view streamed other cells in):
        // nothing to show it with until it is loaded again.
        if (catalog is null && placedObject is null)
        {
            ImGui.TextColored(Theme.TextDim, T("This reference's cell is no longer loaded in the Render Window."));
            return;
        }

        // What is shown and edited: the unsaved edit if there is one, else the winning version.
        var current = (IMajorRecordGetter?)app.Document.Override(key)?.Record ?? _versions[0].Record;
        EditTarget? target = null;
        if (editable)
        {
            var place = placedObject is not null ? RecordPlace.Reference : RecordPlace.TopLevel;
            var context = placedObject is not null ? app.RenderWindow.RefContextOf(placedObject) : null;
            if (context is not null) app.Document.RegisterContext(key, context);
            target = Batch is { Count: > 1 } batch && placedObject is null
                ? new EditTarget(
                    batch.Select(k => (Func<IMajorRecord>)(() => app.Document.WorkingCopy(k, k == key ? current : WinningOf(app, k)))).ToList(),
                    (name, edited) => app.Document.CommitEdits(TF($"{name} ({edited.Count} records)"), edited.Select(e => (e, RecordPlace.TopLevel, (RefContext?)null)).ToList()),
                    edited => Validate(app, edited))
                : new EditTarget(
                    () => app.Document.WorkingCopy(key, current),
                    (name, edited) => app.Document.CommitEdit(name, edited, place, context),
                    edited => Validate(app, edited))
                {
                    Notes = Notes(app, key),
                };
            if (Batch is { Count: > 1 } together)
            {
                ImGui.TextColored(Theme.Warning, TF($"Editing {together.Count} records together: values shown are this one's; a change sets that field on all of them."));
                ImGui.TextColored(Theme.TextDim, string.Join(", ", together.Take(12).Select(k => app.Session!.Catalog.Get(k)?.EditorId ?? k.ToString())) + (together.Count > 12 ? "…" : ""));
            }
        }

        Target = target;
        if (!ImGui.BeginTabBar("##inspector", ImGuiTabBarFlags.FittingPolicyScroll)) return;
        var signature = placedObject is not null ? (placedObject.Kind == StbEditor.Render.SceneObjectKind.Actor ? "ACHR" : "REFR") : catalog!.Signature;

        if (!inWindow && placedObject is not null && app.RenderWindow.IsSelected(placedObject) && Tab(T("Reference")))
        {
            Body(() =>
            {
                if (app.RenderWindow.SingleSelection == placedObject) DrawPlacement(app, placedObject);
                ReferenceProperties.Draw(app, placedObject);
            });
        }

        if (placedObject is null)
        {
            if (_tabsFor != (key, current, app.Settings.ShowRecordHeader))
            {
                _tabsFor = (key, current, app.Settings.ShowRecordHeader);
                _tabs = FormView.Tabs(current, signature, app.Settings.ShowRecordHeader);
            }
            foreach (var tab in _tabs)
            {
                if (!Tab(T(tab.Title) + "###" + tab.Title)) continue;
                if (!editable) ImGui.TextColored(Theme.TextDim, T("Cells, worldspaces, dialogue and navmesh have their own tools"));
                if (!editable && catalog?.Signature is "CELL" or "WRLD" && ImGui.SmallButton(T("Properties...") + "##place")) app.PlaceProperties.Open(key);
                Body(() => FormView.DrawTab(app, current, tab, target));
            }

            if (current is Mutagen.Bethesda.Skyrim.IQuestGetter quest && Batch is not { Count: > 1 } && Tab(T("Dialogue") + "###dialogue"))
                Body(() => DialogueEditor.Draw(app, quest, editable));

            if (Batch is not { Count: > 1 } && ScriptsEditor.Supports(current) && Tab(TF($"Scripts ({ScriptsEditor.Count(current)})###scripts")))
            {
                Body(() => ScriptsEditor.Draw(app, current, (name, change) =>
                {
                    if (removed) return; // a removal is undone first, else this edit would replace it
                    var copy = app.Document.WorkingCopy(key, current);
                    change(copy);
                    app.Document.CommitEdit(name, copy, RecordPlace.TopLevel);
                }));
            }
        }

        if (Tab(T("All fields") + "###allfields"))
        {
            ImGui.TextColored(Theme.TextDim, T("Every field of the record as Mutagen reads it."));
            PropertyTree.Draw(app, current, target);
            ImGui.EndTabItem();
        }

        if (Tab(TF($"Versions ({_versions.Count})###versions")))
        {
            if (placedObject is null)
            {
                if (ImGui.SmallButton(T("Open in a compare window"))) app.Compare.OpenVersions(key);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Every plugin's version side by side in a separate, larger window (as xEdit)"));
            }
            if (_versionColumnsAt != (key, app.Document.History.Version))
            {
                _versionColumnsAt = (key, app.Document.History.Version);
                _versionColumns = placedObject is null
                    ? CompareWindows.VersionColumns(app, key)
                    : _versions.Select(v => new CompareColumn(v.Plugin, v.Plugin, v.Record)).ToList();
            }
            Body(() => _compare.Draw(app, _versionColumns, versions: true));
        }

        ImGui.EndTabBar();
    }

    private (FormKey, IMajorRecordGetter, bool)? _tabsFor;
    private readonly CompareView _compare = new();
    private (FormKey, int)? _versionColumnsAt;
    private List<CompareColumn> _versionColumns = [];
    private List<FormTab> _tabs = [];
    private (FormKey Key, int Version) _notesFor;
    private Dictionary<string, string> _notes = [];

    /// <summary>The record as it is now: the unsaved edit, else the winning version.</summary>
    private static IMajorRecordGetter WinningOf(EditorApp app, FormKey key) =>
        (IMajorRecordGetter?)app.Document.Override(key)?.Record ?? CompareWindows.Versions(app, key)[0].Record;

    /// <summary>Why an edited record would not save (it does not write and read back), or null.</summary>
    private static string? Validate(EditorApp app, IMajorRecord edited)
    {
        if (edited is Mutagen.Bethesda.Skyrim.IPlacedGetter) return null; // references: the writer checks them on save
        var session = app.Session!;
        var encoding = StbEditor.Data.Raw.StringEncodingDetector.WriteEncoding(app.ActivePlugin?.Encoding ?? session.Workspace.DefaultEncoding, session.Game.Language);
        return StbEditor.Core.RecordRoundTrip.Read(edited, session.Game.Release, encoding) is null ? StbEditor.Core.RecordRoundTrip.LastError ?? "unknown" : null;
    }

    /// <summary>--inspector-tab &lt;id&gt;: open that tab (screenshots); the id is the tab's English title.</summary>
    private static string? _forceTab = Environment.GetCommandLineArgs().SkipWhile(x => x != "--inspector-tab").Skip(1).FirstOrDefault();
    private static int _forceFrames = 30;

    private static unsafe bool Tab(string label)
    {
        var force = _forceTab is not null && _forceFrames > 0 && label.Contains(_forceTab, StringComparison.OrdinalIgnoreCase);
        if (force) _forceFrames--;
        return ImGui.BeginTabItem(label, (bool*)null, force ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None);
    }

    /// <summary>A tab's content in its own scrolling area; ends the tab item.</summary>
    private static void Body(Action draw)
    {
        if (ImGui.BeginChild("##body", System.Numerics.Vector2.Zero)) draw();
        ImGui.EndChild();
        ImGui.EndTabItem();
    }

    /// <summary>Fields of the edited record the file would keep differently, recomputed after each edit.</summary>
    private Dictionary<string, string> Notes(EditorApp app, FormKey key)
    {
        if (app.Document.Override(key) is not { } edited) return [];
        var version = app.Document.History.Version;
        if (_notesFor == (key, version)) return _notes;
        _notesFor = (key, version);
        _notes = FieldEditors.FormatNotes(edited.Record, app);
        return _notes;
    }

    /// <summary>Editable position / rotation (degrees, as the Creation Kit shows them) / scale.</summary>
    private static void DrawPlacement(EditorApp app, StbEditor.Render.SceneObject obj)
    {
        ImGui.SeparatorText(obj.IsModified ? T("Placement  (changed)") : T("Placement"));

        var state = obj.State;
        var position = state.Position;
        var degrees = new System.Numerics.Vector3(
            StbEditor.Render.TransformMath.Degrees(state.Rotation.X),
            StbEditor.Render.TransformMath.Degrees(state.Rotation.Y),
            StbEditor.Render.TransformMath.Degrees(state.Rotation.Z));
        var scale = state.Scale;
        var width = ImGui.GetContentRegionAvail().X - ImGui.GetFontSize() * 5;

        ImGui.SetNextItemWidth(width);
        if (ImGui.DragFloat3(T("Position"), ref position, 1f, 0, 0, "%.2f"))
            app.RenderWindow.PreviewFieldEdit(obj, state with { Position = position });
        if (ImGui.IsItemDeactivatedAfterEdit()) app.RenderWindow.CommitFieldEdit(T("Position"));

        ImGui.SetNextItemWidth(width);
        if (ImGui.DragFloat3(T("Rotation"), ref degrees, 0.5f, 0, 0, "%.2f°"))
        {
            var radians = new System.Numerics.Vector3(
                StbEditor.Render.TransformMath.Radians(degrees.X),
                StbEditor.Render.TransformMath.Radians(degrees.Y),
                StbEditor.Render.TransformMath.Radians(degrees.Z));
            app.RenderWindow.PreviewFieldEdit(obj, state with { Rotation = radians });
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) app.RenderWindow.CommitFieldEdit(T("Rotation"));

        ImGui.SetNextItemWidth(width);
        if (ImGui.DragFloat(T("Scale"), ref scale, 0.005f, 0.01f, 10f, "%.3f"))
            app.RenderWindow.PreviewFieldEdit(obj, state with { Scale = Math.Clamp(scale, 0.01f, 10f) });
        if (ImGui.IsItemDeactivatedAfterEdit()) app.RenderWindow.CommitFieldEdit(T("Scale"));

        ImGui.Spacing();
    }

    private void Duplicate(EditorApp app, FormKey key)
    {
        if (app.ActivePlugin is not { } active)
        {
            app.ShowActiveFileDialog(T("A duplicate is a new record of the active file — choose it first."));
            return;
        }

        var source = (Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter?)app.Document.Override(key)?.Record ?? _versions[0].Record;
        var copy = (Mutagen.Bethesda.Plugins.Records.IMajorRecord)source.Duplicate(active.Allocate());
        var catalog = app.Session!.Catalog;
        var baseId = (source.EditorID ?? T("Record")) + T("Copy");
        var id = baseId;
        for (var n = 2; catalog.ByKey.Values.Any(r => string.Equals(r.EditorId, id, StringComparison.OrdinalIgnoreCase)); n++) id = baseId + n;
        copy.EditorID = id;
        app.Document.CommitEdit(TF($"Duplicate {source.EditorID} as {id}"), copy, RecordPlace.TopLevel);
        app.Select(copy.FormKey);
        StbEditor.App.Services.Log.Info($"duplicate: {source.EditorID} -> {id} {copy.FormKey}");
    }

    private void Load(EditorApp app, FormKey key)
    {
        _loadedFor = key;
        _loadedIn = app.Session;
        _versions = [];
        _error = null;
        _errorScene = app.RenderWindow.Scene;

        try
        {
            var session = app.Session!;
            var signature = session.Catalog.Get(key)?.Signature;
            var type = signature is null ? null : StbEditor.Data.RecordLookup.GetterType(signature);

            if (type is not null)
            {
                // Typed: only this record type gets indexed, not the whole load order.
                foreach (var context in session.Workspace.LinkCache.ResolveAllSimpleContexts(key, type))
                    _versions.Add((context.ModKey.FileName.String, context.Record));
            }
            else if (app.RenderWindow.Scene?.Objects.FirstOrDefault(o => o.Reference == key) is { } obj)
            {
                // Placed references are not in the catalog; the loaded scene holds the winner. One made in this
                // session (dropped, pasted, duplicated) is in no plugin yet: its edits start from an empty record.
                if (obj.Record is { } placed) _versions.Add((T("winning version"), placed));
                else if (ReferenceProperties.NewRecord(session, obj) is { } fresh) _versions.Add((T("new (unsaved)"), fresh));
            }

            if (_versions.Count == 0 && app.Document.Override(key) is { Place: RecordPlace.TopLevel } created)
                _versions.Add((T("new (unsaved)"), created.Record));

            if (_versions.Count == 0) _error = "Record not found in the loaded plugins (placed references show once their cell is loaded).";
        }
        catch (Exception ex)
        {
            _error = TF($"Cannot read record: {ex.Message}");
        }
    }
}
