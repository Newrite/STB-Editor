using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Strings;
using Noggog;
using StbEditor.App.Panels;
using StbEditor.Core;
using StbEditor.Render;
using ToolResult = StbEditor.App.Services.ClaudeBridge.ToolResult;

namespace StbEditor.App.Services;

/// <summary>
/// The tools the Claude bridge offers. Each runs on the UI thread with the editor as it is; tools that change something
/// (<see cref="Tool.Edits"/>) make ordinary undoable steps and never save — the user saves.
/// </summary>
internal static class ClaudeTools
{
    public sealed record Tool(string Name, string Description, JsonObject Schema, bool Edits, Func<EditorApp, JsonElement, Task<ToolResult>> Run)
    {
        public JsonObject Describe() => new() { ["name"] = Name, ["description"] = Description, ["inputSchema"] = Schema.DeepClone() };
    }

    public const string Instructions =
        "STB Editor is a Skyrim SE Creation Kit replacement; these tools drive the copy the user has open. World units: Skyrim units " +
        "(Z up, one cell = 4096); headings in degrees, 0 = north, clockwise; pitch in degrees, negative looks down. Records and " +
        "references are named by EditorID, FormID as the editor shows it (e.g. 0001A2B3, load-order prefixed) or 'ID:Plugin.esp'. " +
        "Every change is one undoable step in the editor and is NOT saved: the user reviews and saves (Ctrl+S). Look with screenshot " +
        "after changing things; leave notes for the user with annotate; the user marks places for you with K (get_user_marks).";

    // ------------------------------------------------------------------ schema helpers

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, description, isRequired) in properties)
        {
            JsonObject p = type switch
            {
                "vector" => new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" }, ["minItems"] = 3, ["maxItems"] = 3 },
                "strings" => new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                "notes" => new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["text"] = new JsonObject { ["type"] = "string" },
                            ["ref_id"] = new JsonObject { ["type"] = "string" },
                            ["position"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" } },
                            ["color"] = new JsonObject { ["type"] = "string", ["description"] = "red, green, yellow, blue, white, orange or #RRGGBB" },
                        },
                        ["required"] = new JsonArray("text"),
                    },
                },
                "any" => new JsonObject(),
                _ => new JsonObject { ["type"] = type },
            };
            p["description"] = description;
            props[name] = p;
            if (isRequired) required.Add(name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required };
    }

    private static string? Str(JsonElement a, string name) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static float? Num(JsonElement a, string name) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;
    private static bool? Bool(JsonElement a, string name) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static Vector3? Vec(JsonElement a, string name)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var n = v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetSingle()).ToArray();
        return n.Length >= 3 ? new Vector3(n[0], n[1], n[2]) : null;
    }

    private static List<string> Strings(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : Str(a, name) is { } one ? [one] : [];

    private static Task<ToolResult> Done(ToolResult r) => Task.FromResult(r);
    private static Task<ToolResult> Fail(string text) => Task.FromResult(ToolResult.Text(text, true));
    private static Task<ToolResult> Ok(JsonNode node) => Task.FromResult(ToolResult.Json(node));

    private static JsonArray V(Vector3 v, int digits = 1) => new(Math.Round(v.X, digits), Math.Round(v.Y, digits), Math.Round(v.Z, digits));
    private static float Deg(float radians) => MathF.Round(radians * 180 / MathF.PI, 2);

    /// <summary>A record or reference named by EditorID, FormID («0001A2B3») or «ID:Plugin.esp».</summary>
    private static FormKey? Key(EditorApp app, string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || app.Session is not { } session) return null;
        text = text.Trim();
        if (FormKey.TryFactory(text, out var direct)) return direct;
        return session.Workspace.ParseFormReference(text) ?? session.ByEditorId(text);
    }

    private static string Id(EditorApp app, FormKey key) => app.Session!.Workspace.FormIdText(key);

    private static string Label(EditorApp app, FormKey key) => app.Session!.Catalog.Get(key)?.EditorId ?? Id(app, key);

    private static JsonObject Describe(EditorApp app, SceneObject o, Vector3? from = null) => new()
    {
        ["ref_id"] = Id(app, o.Reference),
        ["base"] = o.BaseEditorId ?? Id(app, o.Base),
        ["base_id"] = Id(app, o.Base),
        ["type"] = o.BaseSignature,
        ["position"] = V(o.Position),
        ["rotation_deg"] = new JsonArray(Deg(o.Rotation.X), Deg(o.Rotation.Y), Deg(o.Rotation.Z)),
        ["scale"] = Math.Round(o.Scale, 4),
        ["distance"] = from is { } f ? Math.Round(Vector3.Distance(f, o.Position), 0) : null,
        ["modified"] = o.IsModified ? true : null,
        ["deleted"] = o.IsDeleted ? true : null,
        ["model"] = o.ModelPath,
    };

    private static List<SceneObject> Objects(EditorApp app, JsonElement a, out string? missing)
    {
        missing = null;
        var list = new List<SceneObject>();
        foreach (var id in Strings(a, "ref_ids"))
        {
            if (Key(app, id) is { } key && app.RenderWindow.ObjectOf(key) is { } obj) list.Add(obj);
            else missing = id;
        }
        return list;
    }

    private static uint Colour(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "red" => 0xFF5050FFu,
        "green" => 0xFF60E060u,
        "blue" => 0xFFFFA050u,
        "white" => 0xFFFFFFFFu,
        "orange" => 0xFF30A0FFu,
        { Length: 7 } hex when hex[0] == '#' && uint.TryParse(hex[1..], NumberStyles.HexNumber, null, out var rgb)
            => 0xFF000000u | ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF),
        _ => 0xFF40E0FFu, // yellow
    };

    // ------------------------------------------------------------------ the tools

    public static readonly Tool[] All =
    [
        new("get_status", "What the editor has open: load order, active file, unsaved changes, the Render Window (cell, camera, selection), marks and notes.",
            Schema(), false, (app, _) => Ok(Status(app))),

        new("screenshot", "A picture of the Render Window from its camera, with a caption (cell, camera). Waits until meshes and textures have streamed in.",
            Schema(("max_width", "integer", "Scale the picture down to at most this width (default 1024)", false),
                   ("wait_for_streaming", "boolean", "Wait for meshes / textures to load (default true)", false),
                   ("overlay", "boolean", "With what is drawn over the view: notes, the user's marks, the corner text, the gizmo (default true)", false)),
            false, Screenshot),

        new("set_camera", "Puts the Render Window camera at a position with a heading and pitch (degrees).",
            Schema(("position", "vector", "x, y, z in world units", true), ("yaw_deg", "number", "heading, 0 = north, clockwise", false),
                   ("pitch_deg", "number", "pitch, negative looks down", false)),
            false, (app, a) =>
            {
                if (Vec(a, "position") is not { } p) return Fail("position [x, y, z] is required");
                var cam = app.RenderWindow.ViewCamera;
                app.RenderWindow.SetCamera(p, Num(a, "yaw_deg") ?? Deg(cam.Yaw), Num(a, "pitch_deg") ?? Deg(cam.Pitch));
                return Ok(Status(app)["render"]!.DeepClone());
            }),

        new("look_at", "Turns the camera onto a loaded reference or a point, from a distance, heading and pitch.",
            Schema(("ref_id", "string", "a loaded reference", false), ("position", "vector", "or a point", false),
                   ("distance", "number", "from the target (default 500)", false), ("yaw_deg", "number", "heading to look along (default: current)", false),
                   ("pitch_deg", "number", "default -20", false)),
            false, (app, a) =>
            {
                Vector3 target;
                if (Str(a, "ref_id") is { } id)
                {
                    if (Key(app, id) is not { } key || app.RenderWindow.ObjectOf(key) is not { } obj) return Fail($"{id} is not loaded in the Render Window (load_cell first)");
                    target = obj.BoundsCenter;
                }
                else if (Vec(a, "position") is { } p) target = p;
                else return Fail("ref_id or position is required");
                var cam = app.RenderWindow.ViewCamera;
                app.RenderWindow.LookAtPoint(target, Num(a, "distance") ?? 500, Num(a, "yaw_deg") ?? Deg(cam.Yaw), Num(a, "pitch_deg") ?? -20);
                return Ok(Status(app)["render"]!.DeepClone());
            }),

        new("load_cell", "Loads a cell into the Render Window: an interior or exterior by EditorID / FormID, or an exterior by worldspace and grid.",
            Schema(("cell", "string", "cell EditorID or FormID", false), ("worldspace", "string", "worldspace EditorID (with grid_x / grid_y)", false),
                   ("grid_x", "integer", "", false), ("grid_y", "integer", "", false)),
            false, (app, a) =>
            {
                var catalog = app.Session!.Catalog;
                FormKey? cell = Key(app, Str(a, "cell"));
                if (cell is null && Key(app, Str(a, "worldspace")) is { } world && Num(a, "grid_x") is { } gx && Num(a, "grid_y") is { } gy)
                    cell = (catalog.BySignature.GetValueOrDefault("CELL") ?? []).FirstOrDefault(c => c.Winner.Cell is { IsInterior: false, IsPersistentCell: false } p
                        && p.Worldspace == world && p.GridX == (int)gx && p.GridY == (int)gy)?.FormKey;
                if (cell is not { } key || catalog.Get(key)?.Signature != "CELL") return Fail("no such cell");
                app.RenderWindow.OpenCell(app, key);
                return Done(ToolResult.Text($"loading {Label(app, key)}; screenshot and list_objects wait for it"));
            }),

        new("list_objects", "Loaded objects near the camera (or a point), nearest first: reference, base, type, place, rotation, scale.",
            Schema(("radius", "number", "default 2000", false), ("filter", "string", "part of the base EditorID or a type (STAT, NPC_…)", false),
                   ("limit", "integer", "default 100", false), ("around", "vector", "centre instead of the camera", false)),
            false, (app, a) =>
            {
                if (app.RenderWindow.Scene is not { } scene) return Fail("nothing is loaded in the Render Window");
                var centre = Vec(a, "around") ?? app.RenderWindow.ViewCamera.Position;
                var radius = Num(a, "radius") ?? 2000;
                var filter = Str(a, "filter");
                var limit = (int)(Num(a, "limit") ?? 100);
                var found = scene.Objects.Where(o => !o.IsDeleted && o.CollectionPart <= 0 && Vector3.Distance(o.Position, centre) <= radius
                        && (filter is null || (o.BaseEditorId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) || o.BaseSignature.Equals(filter, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(o => Vector3.Distance(o.Position, centre)).ToList();
                var list = new JsonArray();
                foreach (var o in found.Take(limit)) list.Add(Describe(app, o, centre));
                return Ok(new JsonObject { ["count"] = found.Count, ["shown"] = list.Count, ["objects"] = list });
            }),

        new("pick", "What is at a point of the Render Window picture (fractions 0..1 across and down, as in a screenshot): the object and the world point.",
            Schema(("x", "number", "0 = left, 1 = right", true), ("y", "number", "0 = top, 1 = bottom", true)),
            false, (app, a) =>
            {
                var (obj, point) = app.RenderWindow.PickFraction(Num(a, "x") ?? 0.5f, Num(a, "y") ?? 0.5f);
                return Ok(new JsonObject { ["object"] = obj is null ? null : Describe(app, obj), ["point"] = point is { } p ? V(p) : null });
            }),

        new("select", "Selects loaded references in the Render Window (the user sees them selected; the Inspector shows the first).",
            Schema(("ref_ids", "strings", "references to select", true)),
            false, (app, a) =>
            {
                var objects = Objects(app, a, out var missing);
                app.RenderWindow.SelectObjects(objects);
                if (objects.Count > 0) app.Select(objects[0].Reference);
                return Done(ToolResult.Text($"selected {objects.Count}" + (missing is null ? "" : $"; not loaded: {missing}")));
            }),

        new("move_objects", "Moves, turns or scales loaded references as one undoable step: an offset, an absolute position, a rotation (degrees, absolute or added) and / or a scale.",
            Schema(("ref_ids", "strings", "references", true), ("offset", "vector", "added to the position", false), ("position", "vector", "absolute (one object)", false),
                   ("rotation_deg", "vector", "absolute rotation x, y, z", false), ("add_rotation_deg", "vector", "added rotation", false), ("scale", "number", "absolute scale", false)),
            true, (app, a) =>
            {
                var objects = Objects(app, a, out var missing);
                if (objects.Count == 0) return Fail($"no loaded reference{(missing is null ? "" : $" ({missing})")}");
                var offset = Vec(a, "offset");
                var position = Vec(a, "position");
                var rotation = Vec(a, "rotation_deg");
                var turn = Vec(a, "add_rotation_deg");
                var scale = Num(a, "scale");
                var changes = objects.Select(o =>
                {
                    var s = o.State;
                    if (position is { } p) s = s with { Position = p };
                    if (offset is { } d) s = s with { Position = s.Position + d };
                    if (rotation is { } r) s = s with { Rotation = r * (MathF.PI / 180) };
                    if (turn is { } t) s = s with { Rotation = s.Rotation + t * (MathF.PI / 180) };
                    if (scale is { } sc) s = s with { Scale = Math.Clamp(sc, 0.01f, 10f) };
                    return (o, s);
                }).ToList();
                app.RenderWindow.SetStates("Claude: move", changes);
                var list = new JsonArray();
                foreach (var o in objects) list.Add(Describe(app, o));
                return Ok(new JsonObject { ["moved"] = list, ["undo"] = app.Document.History.UndoName });
            }),

        new("place_object", "Places a new reference of a base record (static, furniture, NPC…) in the loaded cell, as a drop from the Object Window. Needs an active file.",
            Schema(("base", "string", "base record EditorID or FormID", true), ("position", "vector", "x, y, z", true),
                   ("rotation_deg", "vector", "x, y, z", false), ("scale", "number", "default 1", false)),
            true, (app, a) =>
            {
                if (Key(app, Str(a, "base")) is not { } baseKey) return Fail("no such base record");
                if (Vec(a, "position") is not { } p) return Fail("position is required");
                if (app.ActivePlugin is null) return Fail("there is no active file: the user picks one (File → Active file) before anything new is made");
                var created = app.RenderWindow.PlaceNew(app, baseKey, p, (Vec(a, "rotation_deg") ?? Vector3.Zero) * (MathF.PI / 180), Num(a, "scale") ?? 1);
                return created is null ? Fail($"{Label(app, baseKey)} cannot be placed here") : Ok(Describe(app, created));
            }),

        new("delete_objects", "Deletes loaded references (one undoable step; saving decides how: own ones removed, master ones disabled).",
            Schema(("ref_ids", "strings", "references", true)),
            true, (app, a) =>
            {
                var objects = Objects(app, a, out var missing);
                if (objects.Count == 0) return Fail($"no loaded reference{(missing is null ? "" : $" ({missing})")}");
                app.RenderWindow.SetStates("Claude: delete", objects.Select(o => (o, o.State with { Deleted = true })).ToList());
                return Done(ToolResult.Text($"deleted {objects.Count} (Ctrl+Z brings them back)"));
            }),

        new("undo", "Undoes (or with redo = true redoes) the last steps — the user's or Claude's.",
            Schema(("steps", "integer", "default 1", false), ("redo", "boolean", "redo instead", false)),
            true, (app, a) =>
            {
                var steps = Math.Clamp((int)(Num(a, "steps") ?? 1), 1, 50);
                var redo = Bool(a, "redo") == true;
                for (var i = 0; i < steps; i++)
                {
                    if (redo ? !app.Document.History.CanRedo : !app.Document.History.CanUndo) break;
                    if (redo) app.Document.Redo(); else app.Document.Undo();
                }
                return Done(ToolResult.Text($"next undo: {app.Document.History.UndoName ?? "nothing"}; unsaved changes: {app.Document.PendingCount}"));
            }),

        new("find_records", "Searches the load order's records by part of the EditorID or name, optionally of one type (4-letter signature: WEAP, NPC_, STAT, CELL…).",
            Schema(("text", "string", "part of the EditorID or name", true), ("type", "string", "signature", false), ("limit", "integer", "default 50", false)),
            false, (app, a) =>
            {
                var text = Str(a, "text") ?? "";
                var type = Str(a, "type");
                var limit = (int)(Num(a, "limit") ?? 50);
                var list = new JsonArray();
                foreach (var r in app.Session!.Catalog.ByKey.Values.Where(r => (type is null || r.Signature.Equals(type, StringComparison.OrdinalIgnoreCase))
                             && ((r.EditorId?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) || (r.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)))
                             .Take(limit))
                    list.Add(new JsonObject { ["id"] = Id(app, r.FormKey), ["editor_id"] = r.EditorId, ["name"] = r.Name, ["type"] = r.Signature, ["plugin"] = r.WinningPlugin.Name });
                return Ok(list);
            }),

        new("get_record", "A record's fields as the editor reads them (its unsaved edit if there is one, else the winning version), with the plugins that have it.",
            Schema(("id", "string", "EditorID or FormID", true), ("max_chars", "integer", "default 16000", false)),
            false, (app, a) =>
            {
                if (Key(app, Str(a, "id")) is not { } key || app.Session!.Catalog.Get(key) is null) return Fail("no such record (references: use list_objects)");
                var versions = CompareWindows.Versions(app, key);
                if (versions.Count == 0) return Fail("no version of the record could be read");
                var current = (IMajorRecordGetter?)app.Document.Override(key)?.Record ?? versions[0].Record;
                var text = new StringBuilder();
                text.AppendLine($"{Label(app, key)} [{app.Session.Catalog.Get(key)!.Signature}] {Id(app, key)}; versions: {string.Join(" > ", versions.Select(v => v.Plugin))}"
                    + (app.Document.Override(key) is not null ? "; edited, not saved" : ""));
                Dump(app, current, text, 0, (int)(Num(a, "max_chars") ?? 16000));
                return Done(ToolResult.Text(text.ToString()));
            }),

        new("set_field", "Sets one field of a record (not a placed reference) as one undoable step: path of field names and list indices like 'Name', 'Configuration.HealthOffset', 'Keywords[1]'; links take an EditorID / FormID.",
            Schema(("id", "string", "EditorID or FormID", true), ("path", "string", "field path", true), ("value", "any", "new value (string, number, true / false, null)", true)),
            true, (app, a) => SetField(app, a)),

        new("use_info", "Which records use a record and where it is placed (CK Use Info).",
            Schema(("id", "string", "EditorID or FormID", true), ("limit", "integer", "default 60", false)),
            false, async (app, a) =>
            {
                if (Key(app, Str(a, "id")) is not { } key) return ToolResult.Text("no such record", true);
                var uses = await app.Session!.Uses;
                var all = uses.UsesOf(key);
                var list = new JsonArray();
                foreach (var u in all.Take((int)(Num(a, "limit") ?? 60)))
                    list.Add(new JsonObject
                    {
                        ["by"] = Id(app, u.Source), ["editor_id"] = app.Session.Catalog.Get(u.Source)?.EditorId, ["type"] = u.Type, ["placed"] = u.IsPlaced ? true : null,
                        ["cell"] = u.Parent is { } c ? Label(app, c) : null, ["field"] = u.Subrecord, ["plugin"] = u.Plugin.Name,
                    });
                return ToolResult.Json(new JsonObject { ["count"] = all.Count, ["uses"] = list });
            }),

        new("framework_info", "What SKSE framework configs (SPID, KID, BOS, FLM, DESC, SRD, OAR, DAR) do with a record; for an NPC also what SPID gives it.",
            Schema(("id", "string", "EditorID or FormID", true)),
            false, async (app, a) =>
            {
                if (Key(app, Str(a, "id")) is not { } key) return ToolResult.Text("no such record", true);
                var index = await app.Session!.Frameworks;
                var list = new JsonArray();
                foreach (var r in index.Of(key).Take(80))
                    list.Add(new JsonObject { ["framework"] = r.Framework, ["role"] = r.Role, ["kind"] = r.Kind, ["line"] = r.Text, ["file"] = $"{r.Mod}: {Path.GetFileName(r.File)}:{r.Line}" });
                var result = new JsonObject { ["references"] = list };
                if (new StbEditor.Data.RecordLookup(app.Session.Workspace, app.Session.Catalog).Resolve<Mutagen.Bethesda.Skyrim.INpcGetter>(key) is { } npc)
                {
                    var gifts = new JsonArray();
                    foreach (var g in await FrameworksView.GiftsOf(app.Session, npc))
                        gifts.Add(new JsonObject { ["kind"] = g.Ref.Kind, ["form"] = Label(app, g.Ref.Form), ["chance"] = SpidMatch.Chance(g.Ref.Fields), ["unsure"] = g.Unsure ? true : null });
                    result["spid_gives"] = gifts;
                }
                return ToolResult.Json(result);
            }),

        new("annotate", "Draws notes over the Render Window for the user: a circle and text at a reference or a point (kept until cleared).",
            Schema(("notes", "notes", "the notes", true), ("clear_first", "boolean", "remove the earlier notes first", false)),
            false, (app, a) =>
            {
                if (Bool(a, "clear_first") == true) app.RenderWindow.Annotations.Clear();
                var added = 0;
                if (a.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Array)
                    foreach (var n in notes.EnumerateArray())
                    {
                        var text = Str(n, "text") ?? "";
                        FormKey? reference = Key(app, Str(n, "ref_id"));
                        var place = Vec(n, "position") ?? (reference is { } r && app.RenderWindow.ObjectOf(r) is { } o ? o.BoundsCenter : null);
                        if (place is null) continue;
                        app.RenderWindow.Annotations.Add(new RenderWindow.Annotation(place.Value, text, Colour(Str(n, "color")), reference));
                        added++;
                    }
                return Done(ToolResult.Text($"{added} note(s) on the view, {app.RenderWindow.Annotations.Count} in all"));
            }),

        new("clear_annotations", "Removes Claude's notes from the Render Window.", Schema(), false, (app, _) =>
        {
            app.RenderWindow.Annotations.Clear();
            return Done(ToolResult.Text("cleared"));
        }),

        new("get_user_marks", "Places the user marked for Claude (K over the Render Window), with their notes, the cell and the object under each.",
            Schema(), false, (app, _) =>
            {
                var list = new JsonArray();
                foreach (var m in app.RenderWindow.Marks)
                    list.Add(new JsonObject
                    {
                        ["number"] = m.Number, ["note"] = m.Note, ["position"] = V(m.Position),
                        ["cell"] = m.Cell is { } c ? Label(app, c) : null, ["worldspace"] = m.World is { } w ? Label(app, w) : null,
                        ["ref_id"] = m.Reference is { } r ? Id(app, r) : null, ["base"] = m.ReferenceLabel,
                    });
                return Ok(list);
            }),
    ];

    // ------------------------------------------------------------------ bodies

    private static JsonObject Status(EditorApp app)
    {
        var session = app.Session;
        var window = app.RenderWindow;
        var cam = window.ViewCamera;
        var scene = window.Scene;
        JsonObject? render = null;
        if (scene is not null)
        {
            var cameraCell = window.CameraCell;
            render = new JsonObject
            {
                ["title"] = scene.Title,
                ["interior"] = scene.IsInterior,
                ["worldspace"] = scene.Worldspace is { } w && session is not null ? Label(app, w) : null,
                ["loaded_cell"] = window.LoadedCell is { } c && session is not null ? Label(app, c) : null,
                ["camera"] = new JsonObject { ["position"] = V(cam.Position), ["yaw_deg"] = Deg(cam.Yaw), ["pitch_deg"] = Deg(cam.Pitch), ["fov_deg"] = Deg(cam.FieldOfView) },
                ["camera_cell"] = cameraCell is { } cc ? new JsonArray(cc.X, cc.Y) : null,
                ["objects"] = scene.Objects.Count,
                ["loading"] = window.IsLoading,
                ["streaming"] = new JsonObject { ["meshes"] = window.Streaming.Meshes, ["textures"] = window.Streaming.Textures },
                ["selection"] = new JsonArray(window.SelectedObjects.Take(20).Select(o => (JsonNode)Describe(app, o)).ToArray()),
            };
        }
        return new JsonObject
        {
            ["editor"] = typeof(ClaudeTools).Assembly.GetName().Version?.ToString(),
            ["loaded"] = session is not null,
            ["plugins"] = session?.Workspace.Plugins.Count,
            ["active_file"] = app.ActivePlugin?.Name,
            ["unsaved_changes"] = app.Document.PendingCount,
            ["next_undo"] = app.Document.History.UndoName,
            ["claude_may_change"] = app.Settings.ClaudeBridgeEdits,
            ["render"] = render,
            ["user_marks"] = window.Marks.Count,
            ["notes"] = window.Annotations.Count,
        };
    }

    private static async Task<ToolResult> Screenshot(EditorApp app, JsonElement a)
    {
        if (app.RenderWindow.Scene is null) return ToolResult.Text("nothing is loaded in the Render Window (load_cell)", true);
        var (rgba, width, height) = await app.RenderWindow.Capture(Bool(a, "wait_for_streaming") != false, Bool(a, "overlay") != false);
        var maxWidth = (int)(Num(a, "max_width") ?? 1024);
        var png = await Task.Run(() => Png.Encode(rgba, width, height, Math.Clamp(maxWidth, 128, 4096)));
        var cam = app.RenderWindow.ViewCamera;
        return ToolResult.Image(png, $"{app.RenderWindow.Scene?.Title}; camera {cam.Position.X:0} {cam.Position.Y:0} {cam.Position.Z:0}, heading {Deg(cam.Yaw):0}°, pitch {Deg(cam.Pitch):0}°; " +
            $"{width}x{height}; selected {app.RenderWindow.SelectedObjects.Count}");
    }

    private static Task<ToolResult> SetField(EditorApp app, JsonElement a)
    {
        if (Key(app, Str(a, "id")) is not { } key || app.Session!.Catalog.Get(key) is not { } entry) return Fail("no such record (placed references: move_objects)");
        if (entry.Signature is "CELL" or "WRLD" or "LAND" or "NAVM" or "NAVI" or "DIAL" or "INFO" or "TES4") return Fail($"{entry.Signature} records have their own tools in the editor");
        if (app.ActivePlugin is null) return Fail("there is no active file: the user picks one first");
        var pathText = Str(a, "path") ?? "";
        var path = new List<object>();
        foreach (var part in pathText.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var open = part.IndexOf('[');
            if (open < 0) { path.Add(part); continue; }
            path.Add(part[..open]);
            foreach (var index in part[open..].Split(['[', ']'], StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(index, out var i)) path.Add(i);
        }
        if (path.Count == 0) return Fail("path is required");
        var versions = CompareWindows.Versions(app, key);
        var current = (IMajorRecordGetter?)app.Document.Override(key)?.Record ?? versions[0].Record;
        var copy = app.Document.WorkingCopy(key, current);
        object? value = null;
        if (a.TryGetProperty("value", out var v))
            value = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        // A link takes a record named by EditorID / FormID.
        if (RecordFields.Get(copy, path) is IFormLinkGetter && value is string linkText)
        {
            if (Key(app, linkText) is not { } target) return Fail($"no record {linkText}");
            value = target;
        }
        try { RecordFields.Set(copy, path, value); }
        catch (Exception e) { return Fail($"{pathText}: {e.Message}"); }
        var session = app.Session;
        var encoding = StbEditor.Data.Raw.StringEncodingDetector.WriteEncoding(app.ActivePlugin?.Encoding ?? session.Workspace.DefaultEncoding, session.Game.Language);
        if (RecordRoundTrip.Read(copy, session.Game.Release, encoding) is null) return Fail($"the record would not save that way: {RecordRoundTrip.LastError}");
        app.Document.CommitEdit($"Claude: {Label(app, key)}.{pathText}", copy, RecordPlace.TopLevel);
        return Done(ToolResult.Text($"{Label(app, key)}.{pathText} = {RecordFields.Repr(RecordFields.Get(copy, path))} (unsaved; Ctrl+Z undoes)"));
    }

    /// <summary>Fields as indented text: plain values on their line, structures and lists below, links with their EditorID.</summary>
    private static void Dump(EditorApp app, object? value, StringBuilder text, int depth, int max)
    {
        if (value is null || text.Length > max || depth > 7) return;
        var indent = new string(' ', depth * 2);
        foreach (var property in RecordFields.PropertiesOf(value))
        {
            if (text.Length > max) { text.AppendLine($"{indent}… (cut at {max} characters)"); return; }
            if (property.PropertyType == typeof(Type) || property.Name is "FormKey" or "IsCompressed" or "VersionControl" or "FormVersion" or "Version2" or "MajorRecordFlagsRaw" or "SkyrimMajorRecordFlags") continue;
            object? v;
            try { v = property.GetValue(value); } catch { continue; }
            if (v is null) continue;
            WriteValue(app, property.Name, v, text, depth, max);
        }
    }

    private static void WriteValue(EditorApp app, string name, object v, StringBuilder text, int depth, int max)
    {
        var indent = new string(' ', depth * 2);
        switch (v)
        {
            case Mutagen.Bethesda.Plugins.Assets.IAssetLinkGetter asset:
                text.AppendLine($"{indent}{name}: {asset.GivenPath}");
                return;
            case IFormLinkGetter link:
                text.AppendLine($"{indent}{name}: {(link.FormKeyNullable is { IsNull: false } k ? $"{Label(app, k)} [{Id(app, k)}]" : "none")}");
                return;
            case string or ITranslatedStringGetter or Enum or float or double or bool or P3Float or P2Float or System.Drawing.Color:
                text.AppendLine($"{indent}{name}: {RecordFields.Repr(v)}");
                return;
            case ReadOnlyMemory<byte> bytes:
                text.AppendLine($"{indent}{name}: {Convert.ToHexString(bytes.Span[..Math.Min(bytes.Length, 32)])}{(bytes.Length > 32 ? "…" : "")}");
                return;
            case byte[] raw:
                text.AppendLine($"{indent}{name}: {Convert.ToHexString(raw.AsSpan(0, Math.Min(raw.Length, 32)))}{(raw.Length > 32 ? "…" : "")}");
                return;
        }
        // Plain values and small structs (points, colours): one line — a P3Int16's own Point would recurse forever.
        if (v.GetType().IsValueType && !RecordFields.IsComposite(v) || v is IFormattable && !RecordFields.IsComposite(v))
        {
            text.AppendLine($"{indent}{name}: {RecordFields.Repr(v)}");
            return;
        }
        if (v is IEnumerable list)
        {
            var items = list.Cast<object?>().ToList();
            if (items.Count == 0) return;
            text.AppendLine($"{indent}{name} ({items.Count}):");
            for (var i = 0; i < Math.Min(items.Count, 60); i++)
            {
                if (items[i] is not { } item) continue;
                WriteValue(app, $"[{i}]", item, text, depth + 1, max);
                if (text.Length > max) return;
            }
            if (items.Count > 60) text.AppendLine($"{indent}  … {items.Count - 60} more");
            return;
        }
        text.AppendLine($"{indent}{name}:");
        Dump(app, v, text, depth + 1, max);
    }
}
