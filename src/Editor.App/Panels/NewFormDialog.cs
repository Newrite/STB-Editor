using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Strings;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Core;
using StbEditor.Data;
using StbEditor.Data.Catalog;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// Object Window → New: a blank record of any type in the active file (as the CK's New), then its
/// form window opens. Globals and game settings ask which kind (float, int…).
/// </summary>
internal sealed class NewFormDialog(EditorApp app)
{
    private static string PopupId => T("New record") + "###newform";

    private bool _open;
    private string _signature = "";
    private IReadOnlyList<Type> _classes = [];
    private int _class;
    private string _editorId = "";
    private string _name = "";
    private string? _error;

    /// <summary>Whether the Object Window offers New for this type.</summary>
    public static bool CanCreate(string signature) =>
        !NewRecords.NotCreatable.Contains(signature) && RecordLookup.GetterType(signature) is { } getter && NewRecords.RecordClasses(getter).Count > 0;

    public void Show(string signature)
    {
        if (app.Session is null || !CanCreate(signature)) return;
        if (app.ActivePlugin is null)
        {
            app.ShowActiveFileDialog(T("New records go into the active file — choose it first."));
            return;
        }
        _signature = signature;
        _classes = NewRecords.RecordClasses(RecordLookup.GetterType(signature)!);
        _class = 0;
        _editorId = SuggestId();
        _name = "";
        _error = null;
        _open = true;
    }

    /// <summary>"NewWeapon", "NewWeapon2"… (a game setting starts with its kind's letter).</summary>
    private string SuggestId()
    {
        var type = RecordTypes.Get(_signature).Name.Replace(" ", "").Replace("-", "");
        var prefix = NewRecords.GameSettingPrefix(_classes[_class]) ?? "";
        var id = prefix + "New" + type;
        var catalog = app.Session!.Catalog;
        var taken = catalog.BySignature.GetValueOrDefault(_signature)?.Select(r => r.EditorId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var result = id;
        for (var n = 2; taken.Contains(result); n++) result = id + n;
        return result;
    }

    public void Draw()
    {
        if (_open && !ImGui.IsPopupOpen(PopupId)) ImGui.OpenPopup(PopupId);
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(520, 0) * (ImGui.GetFontSize() / 16f), ImGuiCond.Appearing);

        var open = true;
        if (!ImGui.BeginPopupModal(PopupId, ref open, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!open) _open = false;
            return;
        }
        if (app.Session is not { } session || app.ActivePlugin is not { } active)
        {
            Close();
            ImGui.EndPopup();
            return;
        }

        var type = RecordTypes.Get(_signature);
        ImGui.Text(TF($"New {T(type.Name)} [{_signature}]"));
        ImGui.TextColored(Theme.TextDim, TF($"Created in {active.Name} when you save (Ctrl+S); Ctrl+Z removes it."));
        ImGui.Separator();

        var labelWidth = ImGui.GetFontSize() * 8;
        if (_classes.Count > 1)
        {
            Label(T("Kind"), labelWidth);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##kind", KindName(_classes[_class])))
            {
                for (var i = 0; i < _classes.Count; i++)
                {
                    if (!ImGui.Selectable(KindName(_classes[i]), i == _class)) continue;
                    var oldPrefix = NewRecords.GameSettingPrefix(_classes[_class]);
                    _class = i;
                    // A game setting's first letter is its kind: keep the EditorID in step.
                    if (oldPrefix is not null && _editorId.StartsWith(oldPrefix, StringComparison.Ordinal) && NewRecords.GameSettingPrefix(_classes[i]) is { } prefix)
                        _editorId = prefix + _editorId[oldPrefix.Length..];
                }
                ImGui.EndCombo();
            }
        }

        Label(T("Editor ID"), labelWidth);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.InputText("##eid", ref _editorId, 200);

        var named = typeof(INamed).IsAssignableFrom(_classes[_class]) && NewRecords.GameSettingPrefix(_classes[_class]) is null;
        if (named)
        {
            Label(T("Name (shown in game)"), labelWidth);
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##name", ref _name, 200);
        }

        if (_error is not null) ImGui.TextColored(Theme.Error, _error);
        ImGui.Spacing();

        var width = ImGui.GetFontSize() * 8;
        if (ImGui.Button(T("Create"), new Vector2(width, 0)) || ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter))
            Create(session, active, named);
        ImGui.SameLine();
        if (ImGui.Button(T("Cancel"), new Vector2(width, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape)) Close();
        ImGui.EndPopup();
    }

    private static string KindName(Type cls) => cls.Name switch
    {
        "GlobalFloat" => T("Float"),
        "GlobalInt" => T("Long (integer)"),
        "GlobalShort" => T("Short (integer)"),
        "GameSettingFloat" => T("Float (f…)"),
        "GameSettingInt" => T("Integer (i…)"),
        "GameSettingString" => T("String (s…)"),
        "GameSettingBool" => T("Bool (b…)"),
        _ => cls.Name,
    };

    private static void Label(string text, float width)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextDim, text);
        ImGui.SameLine(width);
    }

    private void Create(EditorSession session, ActivePlugin active, bool named)
    {
        var id = _editorId.Trim();
        var cls = _classes[_class];
        if (!NewRecords.IsValidEditorId(id))
        {
            _error = T("Editor ID: letters, digits and _, starting with a letter.");
            return;
        }
        if (NewRecords.GameSettingPrefix(cls) is { } prefix && !id.StartsWith(prefix, StringComparison.Ordinal))
        {
            _error = TF($"A game setting of this kind starts with \"{prefix}\".");
            return;
        }
        if (session.Catalog.ByKey.Values.Any(r => string.Equals(r.EditorId, id, StringComparison.OrdinalIgnoreCase)))
        {
            _error = TF($"{id} is already used.");
            return;
        }

        var record = NewRecords.Blank(cls, active.Allocate(), session.Game.Release, id);
        if (named && !string.IsNullOrWhiteSpace(_name) && record is INamed withName)
            withName.Name = _name.Trim();
        app.Document.CommitEdit(TF($"New {id}"), record, RecordPlace.TopLevel);
        app.Select(record.FormKey);
        app.FormWindows.Open(record.FormKey);
        Log.Info($"new record: {_signature} {cls.Name} {id} {record.FormKey}");
        _open = false;
        if (ImGui.IsPopupOpen(PopupId)) ImGui.CloseCurrentPopup();
    }

    /// <summary>What the dialog does on Create, without the dialog (autotests).</summary>
    public Mutagen.Bethesda.Plugins.FormKey? CreateForTest(string signature, int kind, string editorId, string name)
    {
        Show(signature);
        _class = kind;
        _editorId = editorId;
        _name = name;
        var before = app.Selected;
        Create(app.Session!, app.ActivePlugin!, typeof(INamed).IsAssignableFrom(_classes[_class]) && NewRecords.GameSettingPrefix(_classes[_class]) is null);
        _open = false;
        if (_error is not null) StbEditor.App.Services.Log.Warn($"new record refused: {_error}");
        return app.Selected != before ? app.Selected : null;
    }

    private void Close()
    {
        _open = false;
        ImGui.CloseCurrentPopup();
    }
}
