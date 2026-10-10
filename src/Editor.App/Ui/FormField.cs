using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;

namespace StbEditor.App.Ui;

/// <summary>
/// A field holding a form reference, as in the Creation Kit's pick boxes: shows the form, takes a
/// FormID / EditorID typed in, a drop from the Object Window, or a click in the Render Window.
/// </summary>
internal static class FormField
{
    private static readonly Dictionary<string, string> Typing = new();

    /// <summary>
    /// Draws the field; returns true with <paramref name="value"/> changed when the user set it.
    /// <paramref name="signatures"/> limits what is accepted (null: anything; "REFR" means placed references).
    /// </summary>
    public static bool Draw(EditorApp app, string id, ref FormKey? value, string[]? signatures, bool references = false)
    {
        var changed = false;
        var session = app.Session!;
        ImGui.PushID(id);

        var width = ImGui.GetContentRegionAvail().X;
        var buttons = ImGui.GetFrameHeight() * (references ? 2 : 1) + ImGui.GetStyle().ItemSpacing.X * (references ? 2 : 1);
        ImGui.SetNextItemWidth(Math.Max(width - buttons, 60));

        var shown = value is { } v ? Describe(app, v) : string.Empty;
        var text = Typing.TryGetValue(id, out var typing) ? typing : shown;
        if (ImGui.InputTextWithHint("##text", references ? "FormID, or + then click in the Render Window" : "FormID or EditorID", ref text, 256))
            Typing[id] = text;
        if (ImGui.IsItemDeactivated() && Typing.Remove(id, out var entered))
        {
            if (entered.Trim().Length == 0)
            {
                value = null;
                changed = true;
            }
            else if (Resolve(app, entered, signatures, references) is { } found)
            {
                value = found;
                changed = true;
            }
            else
            {
                Services.Log.Warn($"{entered}: no matching form");
            }
        }
        if (ImGui.IsItemHovered() && value is { } hover) ImGui.SetTooltip($"{hover}");

        // Drop from the Object Window (base forms) — references come from the Render Window.
        if (!references && ImGui.BeginDragDropTarget())
        {
            if (!ImGui.AcceptDragDropPayload(FormDrag.PayloadType).IsNull && FormDrag.Current is { } dropped
                && (signatures is null || signatures.Contains(session.Catalog.Get(dropped)?.Signature)))
            {
                value = dropped;
                changed = true;
            }
            ImGui.EndDragDropTarget();
        }

        if (references)
        {
            ImGui.SameLine();
            var picking = app.RenderWindow.PickRequestId == id;
            if (picking) ImGui.PushStyleColor(ImGuiCol.Button, Theme.ButtonActive);
            if (ImGui.Button("+", new Vector2(ImGui.GetFrameHeight(), 0))) app.RenderWindow.RequestPick(id);
            if (picking) ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Pick: click a reference in the Render Window");
            if (app.RenderWindow.TakePick(id) is { } picked)
            {
                value = picked.Reference;
                changed = true;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("x", new Vector2(ImGui.GetFrameHeight(), 0)) && value is not null)
        {
            value = null;
            changed = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clear");

        ImGui.PopID();
        return changed;
    }

    /// <summary>EditorID and FormID of a base form, or "→ base" for a reference in the loaded scene.</summary>
    public static string Describe(EditorApp app, FormKey key)
    {
        var session = app.Session!;
        var id = session.Workspace.FormIdText(key);
        if (session.Catalog.Get(key) is { } record) return $"{record.EditorId ?? "?"}  {id}";
        if (app.RenderWindow.Scene?.Objects.FirstOrDefault(o => o.Reference == key) is { } placed)
            return $"{placed.Record?.EditorID ?? "ref"} → {placed.BaseEditorId}  {id}";
        if (key == PlayerRef) return $"PlayerRef  {id}";
        return id;
    }

    public static readonly FormKey PlayerRef = FormKey.Factory("000014:Skyrim.esm");

    private static FormKey? Resolve(EditorApp app, string text, string[]? signatures, bool references)
    {
        var session = app.Session!;
        if (session.Workspace.ParseFormReference(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? text) is { } key)
        {
            if (references) return key;
            var signature = session.Catalog.Get(key)?.Signature;
            if (signature is not null && (signatures is null || signatures.Contains(signature))) return key;
        }

        var name = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;
        if (references)
        {
            return app.RenderWindow.Scene?.Objects.FirstOrDefault(o => string.Equals(o.Record?.EditorID, name, StringComparison.OrdinalIgnoreCase))?.Reference
                ?? (string.Equals(name, "PlayerRef", StringComparison.OrdinalIgnoreCase) ? PlayerRef : null);
        }

        var pool = signatures is null ? session.Catalog.ByKey.Values : signatures.SelectMany(s => session.Catalog.BySignature.GetValueOrDefault(s) ?? []);
        return pool.FirstOrDefault(r => string.Equals(r.EditorId, name, StringComparison.OrdinalIgnoreCase))?.FormKey;
    }
}
