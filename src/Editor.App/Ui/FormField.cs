using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Ui;

/// <summary>
/// A field holding a form reference, as in the Creation Kit's pick boxes: shows the form; typing
/// lists matching records of the accepted types under it (EditorID, name or FormID); ▼ lists them
/// all; a drop from the Object Window or a click in the Render Window (references) also sets it.
/// </summary>
internal static class FormField
{
    private static readonly Dictionary<string, string> Typing = new();

    /// <summary>Field whose suggestion list is open, the highlighted row, and whether the mouse is on the list.</summary>
    private static string? _suggestFor;
    private static int _highlight;
    private static bool _suggestHovered;
    private static (string Id, string Query, List<Option> Results)? _cache;
    private static string _browseFilter = string.Empty;
    private static string? _activeField;

    /// <summary>A candidate: what to store, what to show.</summary>
    private readonly record struct Option(FormKey Key, string EditorId, string Text);

    private const int MaxResults = 300;

    /// <summary>Autotests: where each field was drawn last frame, and how many suggestions were shown.</summary>
    public static Dictionary<string, (Vector2 Min, Vector2 Max)> LastRects { get; } = new();
    public static int LastSuggestionCount { get; private set; } = -1;
    public static string LastQuery { get; private set; } = "";

    /// <summary>
    /// Draws the field; returns true with <paramref name="value"/> changed when the user set it.
    /// <paramref name="signatures"/> limits what is accepted (null: anything). Typing shows matching
    /// records under the field (↑/↓ and Enter, or click); ▼ lists them all, as the CK's drop-downs.
    /// </summary>
    public static bool Draw(EditorApp app, string id, ref FormKey? value, string[]? signatures, bool references = false)
    {
        var changed = false;
        var session = app.Session!;
        ImGui.PushID(id);

        var width = ImGui.GetContentRegionAvail().X;
        var buttonCount = references ? 3 : 2;
        var buttons = (ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X) * buttonCount;
        ImGui.SetNextItemWidth(Math.Max(width - buttons, 60));

        var shown = value is { } v ? Describe(app, v) : string.Empty;
        var text = Typing.TryGetValue(id, out var typing) ? typing : shown;
        var hint = references ? T("FormID, or + then click in the Render Window") : T("FormID, EditorID or name: type to search");
        // No EnterReturnsTrue: with it the binding hands back the typed text only on Enter, so the
        // search never saw what was being typed. Enter is read from the key, before the field
        // handles it (it ends editing, and ImGui reports that only a frame later).
        var enter = _activeField == id && (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false));
        if (ImGui.InputTextWithHint("##text", hint, ref text, 256, ImGuiInputTextFlags.AutoSelectAll))
        {
            Typing[id] = text;
            _highlight = 0;
        }
        var inputActive = ImGui.IsItemActive();
        // The arrows choose a suggestion: keep keyboard navigation from moving focus to the next widget.
        if (inputActive && _suggestFor == id)
        {
            ImGuiP.SetItemKeyOwner(ImGuiKey.DownArrow, ImGuiInputFlags.None);
            ImGuiP.SetItemKeyOwner(ImGuiKey.UpArrow, ImGuiInputFlags.None);
        }
        if (inputActive) _activeField = id;
        else if (_activeField == id) _activeField = null;
        var fieldMin = ImGui.GetItemRectMin();
        var fieldMax = ImGui.GetItemRectMax();
        LastRects[id] = (fieldMin, fieldMax);
        if (ImGui.IsItemHovered() && value is { } hover && !inputActive) ImGui.SetTooltip($"{hover}");

        // Suggestions while typing, and while the mouse is on the list so that a click can land.
        var query = Typing.TryGetValue(id, out var typed) ? typed.Trim() : string.Empty;
        if (inputActive && query.Length > 0) _suggestFor = id;
        // Enter ends editing in the same frame, so it counts as still typing.
        var suggesting = _suggestFor == id && query.Length > 0 && (inputActive || enter || _suggestHovered);
        var options = suggesting ? Search(app, id, query, signatures, references) : [];
        if (suggesting) LastQuery = $"{query} [{(signatures is null ? "any" : string.Join(",", signatures))}] -> {options.Count}";

        if (suggesting && inputActive && options.Count > 0)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) _highlight = Math.Min(_highlight + 1, options.Count - 1);
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) _highlight = Math.Max(_highlight - 1, 0);
        }

        if (enter && suggesting && options.Count > 0)
        {
            value = options[Math.Clamp(_highlight, 0, options.Count - 1)].Key;
            changed = true;
            Typing.Remove(id);
            _suggestFor = null;
        }
        else if (!inputActive && !(_suggestFor == id && _suggestHovered) && Typing.Remove(id, out var entered))
        {
            // Left the field with typed text: an exact FormID / EditorID, else the only match.
            if (_suggestFor == id) _suggestFor = null;
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
            else if (Search(app, id, entered.Trim(), signatures, references) is [var only])
            {
                value = only.Key;
                changed = true;
            }
            else
            {
                Services.Log.Warn(TF($"{entered}: no matching form, pick one from the list"));
            }
        }

        if (suggesting && Typing.ContainsKey(id))
        {
            if (DrawSuggestions(id, options, fieldMin, fieldMax) is { } picked)
            {
                value = picked;
                changed = true;
                Typing.Remove(id);
                _suggestFor = null;
                _suggestHovered = false;
            }
        }
        else if (_suggestFor == id && !inputActive)
        {
            _suggestFor = null;
            _suggestHovered = false;
        }

        // Drop from the Object Window (base forms); references come from the Render Window.
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

        // ▼: every record that fits, with a filter.
        ImGui.SameLine();
        if (ImGui.ArrowButton("##browse", ImGuiDir.Down))
        {
            _browseFilter = string.Empty;
            ImGui.OpenPopup("##browselist");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Choose from the list"));
        if (DrawBrowse(app, id, signatures, references, fieldMin, fieldMax) is { } chosen)
        {
            value = chosen;
            changed = true;
        }

        if (references)
        {
            ImGui.SameLine();
            var picking = app.RenderWindow.PickRequestId == id;
            if (picking) ImGui.PushStyleColor(ImGuiCol.Button, Theme.ButtonActive);
            if (ImGui.Button("+", new Vector2(ImGui.GetFrameHeight(), 0))) app.RenderWindow.RequestPick(id);
            if (picking) ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Pick: click a reference in the Render Window"));
            if (app.RenderWindow.TakePick(id) is { } pickedRef)
            {
                value = pickedRef.Reference;
                changed = true;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("x", new Vector2(ImGui.GetFrameHeight(), 0)) && value is not null)
        {
            value = null;
            changed = true;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(T("Clear"));

        ImGui.PopID();
        return changed;
    }

    /// <summary>The list under the field; returns the clicked record.</summary>
    private static FormKey? DrawSuggestions(string id, List<Option> options, Vector2 fieldMin, Vector2 fieldMax)
    {
        FormKey? picked = null;
        var rowHeight = ImGui.GetTextLineHeightWithSpacing();
        var rows = Math.Clamp(options.Count, 1, 12);
        ImGui.SetNextWindowPos(new Vector2(fieldMin.X, fieldMax.Y));
        ImGui.SetNextWindowSize(new Vector2(Math.Max(fieldMax.X - fieldMin.X, 320), (rows + 1) * rowHeight + ImGui.GetStyle().WindowPadding.Y * 2));
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoNav;
        LastSuggestionCount = options.Count;
        if (ImGui.Begin("##suggest" + id, flags))
        {
            ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindow());
            _suggestHovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) || (ImGui.IsMouseDown(ImGuiMouseButton.Left) && _suggestHovered);
            if (options.Count == 0)
            {
                ImGui.TextColored(Theme.TextDim, T("Nothing matches."));
            }
            else
            {
                ImGui.TextColored(Theme.TextDim, options.Count >= MaxResults
                    ? TF($"first {MaxResults} matches: type more to narrow")
                    : TF($"{options.Count} match(es): Up/Down and Enter, or click"));
                var clipper = new ImGuiListClipper();
                clipper.Begin(options.Count);
                while (clipper.Step())
                {
                    for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        ImGui.PushID(i);
                        if (ImGui.Selectable(options[i].Text, i == _highlight)) picked = options[i].Key;
                        if (i == _highlight && (ImGui.IsKeyPressed(ImGuiKey.DownArrow) || ImGui.IsKeyPressed(ImGuiKey.UpArrow))) ImGui.SetScrollHereY();
                        ImGui.PopID();
                    }
                }
                clipper.End();
            }
        }
        else
        {
            _suggestHovered = false;
        }
        ImGui.End();
        return picked;
    }

    /// <summary>▼ popup: a filter box and every fitting record.</summary>
    private static FormKey? DrawBrowse(EditorApp app, string id, string[]? signatures, bool references, Vector2 fieldMin, Vector2 fieldMax)
    {
        FormKey? chosen = null;
        ImGui.SetNextWindowSize(app.Scaled(560, 460), ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(new Vector2(fieldMin.X, fieldMax.Y), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("##browselist")) return null;
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", T("Filter: EditorID, name or FormID"), ref _browseFilter, 128);
        var all = Search(app, id + "#browse", _browseFilter.Trim(), signatures, references, limit: 50_000);
        ImGui.TextColored(Theme.TextDim, signatures is { Length: > 0 }
            ? TF($"{all.Count:N0} record(s): {string.Join(", ", signatures)}")
            : TF($"{all.Count:N0} record(s)"));
        if (ImGui.BeginChild("##all", Vector2.Zero, ImGuiChildFlags.Borders))
        {
            var clipper = new ImGuiListClipper();
            clipper.Begin(all.Count);
            while (clipper.Step())
            {
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    ImGui.PushID(i);
                    if (ImGui.Selectable(all[i].Text))
                    {
                        chosen = all[i].Key;
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.PopID();
                }
            }
            clipper.End();
        }
        ImGui.EndChild();
        ImGui.EndPopup();
        return chosen;
    }

    /// <summary>
    /// Records (or loaded references) matching every word of <paramref name="query"/> in their
    /// EditorID, name or FormID; EditorIDs starting with the query come first. Empty query: all.
    /// </summary>
    private static List<Option> Search(EditorApp app, string id, string query, string[]? signatures, bool references, int limit = MaxResults)
    {
        if (_cache is { } c && c.Id == id && c.Query == query) return c.Results;
        var session = app.Session!;
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var results = new List<(int Rank, Option Option)>();

        bool Matches(string haystack) => words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
        int Rank(string? editorId) => query.Length > 0 && editorId is not null && editorId.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1;

        if (references)
        {
            foreach (var o in (app.RenderWindow.Scene?.Objects ?? []).DistinctBy(o => o.Reference))
            {
                if (signatures is not null && !signatures.Contains(o.BaseSignature)) continue;
                var formId = session.Workspace.FormIdText(o.Reference);
                var editorId = o.Record?.EditorID ?? string.Empty;
                var text = $"{(editorId.Length > 0 ? editorId : "ref")} -> {o.BaseEditorId}  {formId}  [{o.BaseSignature}]";
                if (Matches(text)) results.Add((Rank(editorId), new Option(o.Reference, editorId, text)));
            }
            if (Matches("PlayerRef")) results.Add((Rank("PlayerRef"), new Option(PlayerRef, "PlayerRef", $"PlayerRef  {session.Workspace.FormIdText(PlayerRef)}")));
        }
        else
        {
            var pool = signatures is null ? session.Catalog.ByKey.Values : signatures.SelectMany(s => session.Catalog.BySignature.GetValueOrDefault(s) ?? []);
            foreach (var r in pool)
            {
                var formId = session.Workspace.FormIdText(r.FormKey);
                if (!Matches($"{r.EditorId} {r.Name} {formId}")) continue;
                var text = r.Name is { Length: > 0 } name
                    ? $"{r.EditorId ?? "?"}  «{name}»  {formId}  [{r.Signature}]"
                    : $"{r.EditorId ?? "?"}  {formId}  [{r.Signature}]";
                results.Add((Rank(r.EditorId), new Option(r.FormKey, r.EditorId ?? string.Empty, text)));
            }
        }

        var list = results.OrderBy(x => x.Rank).ThenBy(x => x.Option.EditorId, StringComparer.OrdinalIgnoreCase)
            .Take(limit).Select(x => x.Option).ToList();
        _cache = (id, query, list);
        return list;
    }

    /// <summary>EditorID and FormID of a base form, or "-> base" for a reference in the loaded scene.</summary>
    public static string Describe(EditorApp app, FormKey key)
    {
        var session = app.Session!;
        var id = session.Workspace.FormIdText(key);
        if (session.Catalog.Get(key) is { } record) return $"{record.EditorId ?? "?"}  {id}";
        if (app.RenderWindow.Scene?.Objects.FirstOrDefault(o => o.Reference == key) is { } placed)
            return $"{placed.Record?.EditorID ?? "ref"} -> {placed.BaseEditorId}  {id}";
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
