using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data.Catalog;

namespace StbEditor.App.Panels;

/// <summary>
/// Shows the selected record: one tab per plugin that contains it (winner first), each with the
/// full field tree. Read-only in stage 0; editing builds on the same tree.
/// </summary>
internal sealed class InspectorPanel : Panel
{
    private FormKey? _loadedFor;
    private List<(string Plugin, IMajorRecordGetter Record)> _versions = [];
    private string? _error;
    private bool _editMode;

    /// <summary>Records that do not sit in a top-level group (or need their own tools) are not field-edited here.</summary>
    private static readonly HashSet<string> NotFieldEditable = ["CELL", "WRLD", "LAND", "NAVM", "NAVI", "DIAL", "INFO", "QUST", "SCEN", "TES4"];

    public override string Name => "Inspector";

    protected override void DrawContent(EditorApp app)
    {
        if (!RequireSession(app)) return;
        var session = app.Session!;
        ReferenceProperties.Update(app);

        if (app.Selected is not { } key)
        {
            ImGui.TextColored(Theme.TextDim, "Select a record in the Object Window or Cell View.");
            return;
        }

        if (_loadedFor != key) Load(app, key);

        var catalog = session.Catalog.Get(key);
        var placedObject = catalog is null ? app.RenderWindow.Scene?.Objects.FirstOrDefault(o => o.Reference == key) : null;
        var type = catalog is not null ? RecordTypes.Get(catalog.Signature)
            : placedObject is not null ? new RecordTypeInfo(placedObject.Kind == StbEditor.Render.SceneObjectKind.Actor ? "ACHR" : "REFR", "Reference", "") : null;

        ImGui.PushFont(app.Ui.HeaderFont, 0);
        ImGui.Text(catalog?.EditorId ?? placedObject?.Record?.EditorID ?? (placedObject is not null ? $"→ {placedObject.BaseEditorId}" : "(no editor id)"));
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextDim, $"{type?.Name ?? "?"} [{type?.Signature}]  {session.Workspace.FormIdText(key)}");
        if (placedObject is not null)
        {
            ImGui.TextColored(Theme.TextDim, $"base {placedObject.BaseEditorId} [{placedObject.BaseSignature}]");
            ImGui.SameLine();
            if (ImGui.SmallButton("Open base")) app.Select(placedObject.Base);
        }
        if (catalog?.Name is { Length: > 0 } name) ImGui.TextColored(Theme.Accent, name);

        var editable = placedObject is not null || (catalog is not null && !NotFieldEditable.Contains(catalog.Signature));
        ImGui.BeginDisabled(!editable);
        if (_editMode) ImGui.PushStyleColor(ImGuiCol.Button, Theme.ButtonActive);
        if (ImGui.SmallButton(_editMode ? "Editing fields" : "Edit fields")) _editMode = !_editMode;
        if (_editMode) ImGui.PopStyleColor();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(editable ? "Edit any field; each change is one Ctrl+Z, saved into the active file" : "Cells, worldspaces, dialogue and navmesh have their own tools");
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy FormKey")) ImGui.SetClipboardText(key.ToString());
        if (placedObject is null && editable && _versions.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Duplicate")) Duplicate(app, key);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("New record in the active file: a copy with a new FormID and EditorID (as the CK's Duplicate)");
        }

        if (placedObject is not null && app.RenderWindow.SingleSelection == placedObject)
        {
            DrawPlacement(app, placedObject);
            ReferenceProperties.Draw(app, placedObject);
        }

        if (_error is not null)
        {
            ImGui.TextColored(Theme.Error, _error);
            return;
        }

        // Scripts of a base object (references show theirs with the reference properties).
        if (placedObject is null && catalog is not null && _versions.Count > 0 && !NotFieldEditable.Contains(catalog.Signature))
        {
            var current = (Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter?)app.Document.Override(key)?.Record ?? _versions[0].Record;
            if (ScriptsEditor.Supports(current))
            {
                ScriptsEditor.Draw(app, current, (name, change) =>
                {
                    var copy = app.Document.WorkingCopy(key, current);
                    change(copy);
                    app.Document.CommitEdit(name, copy, RecordPlace.TopLevel);
                });
            }
        }

        if (!ImGui.BeginTabBar("##versions", ImGuiTabBarFlags.FittingPolicyScroll)) return;

        // Editing: the edited copy if there is one, else the winner, with field editors.
        if (_editMode && editable && _versions.Count > 0)
        {
            var current = (Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter?)app.Document.Override(key)?.Record ?? _versions[0].Record;
            if (ImGui.BeginTabItem(app.Document.Override(key) is null ? "edit###editing" : "edit (unsaved)###editing"))
            {
                var place = placedObject is not null ? RecordPlace.Reference : RecordPlace.TopLevel;
                var context = placedObject is not null ? app.RenderWindow.RefContextOf(placedObject) : null;
                if (context is not null) app.Document.RegisterContext(key, context);
                var target = new EditTarget(
                    () => app.Document.WorkingCopy(key, current),
                    (name, edited) => app.Document.CommitEdit(name, edited, place, context));
                PropertyTree.Draw(app, current, target);
                ImGui.EndTabItem();
            }
        }
        else if (app.Document.Override(key) is { } edited && ImGui.BeginTabItem("edited (unsaved)###edited"))
        {
            PropertyTree.Draw(app, edited.Record);
            ImGui.EndTabItem();
        }

        for (var i = 0; i < _versions.Count; i++)
        {
            var (plugin, record) = _versions[i];
            var label = i == 0 && _versions.Count > 1 ? $"{plugin} (winner)###v{i}" : $"{plugin}###v{i}";
            if (!ImGui.BeginTabItem(label)) continue;

            PropertyTree.Draw(app, record);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    /// <summary>Editable position / rotation (degrees, as the Creation Kit shows them) / scale.</summary>
    private static void DrawPlacement(EditorApp app, StbEditor.Render.SceneObject obj)
    {
        ImGui.SeparatorText(obj.IsModified ? "Placement  (changed)" : "Placement");

        var state = obj.State;
        var position = state.Position;
        var degrees = new System.Numerics.Vector3(
            StbEditor.Render.TransformMath.Degrees(state.Rotation.X),
            StbEditor.Render.TransformMath.Degrees(state.Rotation.Y),
            StbEditor.Render.TransformMath.Degrees(state.Rotation.Z));
        var scale = state.Scale;
        var width = ImGui.GetContentRegionAvail().X - ImGui.GetFontSize() * 5;

        ImGui.SetNextItemWidth(width);
        if (ImGui.DragFloat3("Position", ref position, 1f, 0, 0, "%.2f"))
            app.RenderWindow.PreviewFieldEdit(obj, state with { Position = position });
        if (ImGui.IsItemDeactivatedAfterEdit()) app.RenderWindow.CommitFieldEdit("Position");

        ImGui.SetNextItemWidth(width);
        if (ImGui.DragFloat3("Rotation", ref degrees, 0.5f, 0, 0, "%.2f°"))
        {
            var radians = new System.Numerics.Vector3(
                StbEditor.Render.TransformMath.Radians(degrees.X),
                StbEditor.Render.TransformMath.Radians(degrees.Y),
                StbEditor.Render.TransformMath.Radians(degrees.Z));
            app.RenderWindow.PreviewFieldEdit(obj, state with { Rotation = radians });
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) app.RenderWindow.CommitFieldEdit("Rotation");

        ImGui.SetNextItemWidth(width);
        if (ImGui.DragFloat("Scale", ref scale, 0.005f, 0.01f, 10f, "%.3f"))
            app.RenderWindow.PreviewFieldEdit(obj, state with { Scale = Math.Clamp(scale, 0.01f, 10f) });
        if (ImGui.IsItemDeactivatedAfterEdit()) app.RenderWindow.CommitFieldEdit("Scale");

        ImGui.Spacing();
    }

    private void Duplicate(EditorApp app, FormKey key)
    {
        if (app.ActivePlugin is not { } active)
        {
            app.ShowActiveFileDialog("A duplicate is a new record of the active file — choose it first.");
            return;
        }

        var source = (Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter?)app.Document.Override(key)?.Record ?? _versions[0].Record;
        var copy = (Mutagen.Bethesda.Plugins.Records.IMajorRecord)source.Duplicate(active.Allocate());
        var catalog = app.Session!.Catalog;
        var baseId = (source.EditorID ?? "Record") + "Copy";
        var id = baseId;
        for (var n = 2; catalog.ByKey.Values.Any(r => string.Equals(r.EditorId, id, StringComparison.OrdinalIgnoreCase)); n++) id = baseId + n;
        copy.EditorID = id;
        app.Document.CommitEdit($"Duplicate {source.EditorID} as {id}", copy, RecordPlace.TopLevel);
        app.Select(copy.FormKey);
        _editMode = true;
        StbEditor.App.Services.Log.Info($"duplicate: {source.EditorID} -> {id} {copy.FormKey}");
    }

    private void Load(EditorApp app, FormKey key)
    {
        _loadedFor = key;
        _versions = [];
        _error = null;

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
            else if (app.RenderWindow.Scene?.Objects.FirstOrDefault(o => o.Reference == key) is { Record: { } placed })
            {
                // Placed references are not in the catalog; the loaded scene holds the winner.
                _versions.Add(("winning version", placed));
            }

            if (_versions.Count == 0 && app.Document.Override(key) is { Place: RecordPlace.TopLevel } created)
                _versions.Add(("new (unsaved)", created.Record));

            if (_versions.Count == 0) _error = "Record not found in the loaded plugins (placed references show once their cell is loaded).";
        }
        catch (Exception ex)
        {
            _error = $"Cannot read record: {ex.Message}";
        }
    }
}
