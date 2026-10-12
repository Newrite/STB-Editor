using System.Globalization;
using System.Numerics;
using Hexa.NET.ImGui;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Strings;
using Noggog;
using StbEditor.Core;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Ui;

/// <summary>
/// Makes field views editable: an edit is made on a fresh working copy of the record and committed
/// as one undo step. <see cref="Notes"/>: fields the plugin file stores differently from what was
/// typed (path → stored value), from <see cref="RecordRoundTrip"/>.
/// </summary>
internal sealed class EditTarget
{
    private readonly IReadOnlyList<Func<IMajorRecord>> _copies;
    private readonly Action<string, IReadOnlyList<IMajorRecord>> _commit;
    private readonly Func<IMajorRecord, string?>? _validate;

    public EditTarget(Func<IMajorRecord> copy, Action<string, IMajorRecord> commit, Func<IMajorRecord, string?>? validate = null)
        : this([copy], (name, edited) => commit(name, edited[0]), validate)
    {
    }

    /// <summary>Several records edited together (xEdit: copy to selected records): each change goes to every one, one undo step.</summary>
    public EditTarget(IReadOnlyList<Func<IMajorRecord>> copies, Action<string, IReadOnlyList<IMajorRecord>> commit, Func<IMajorRecord, string?>? validate = null)
    {
        _copies = copies;
        _commit = commit;
        _validate = validate;
    }

    public IReadOnlyDictionary<string, string> Notes { get; set; } = new Dictionary<string, string>();

    /// <summary>How many records an edit goes to.</summary>
    public int Count => _copies.Count;

    /// <summary>
    /// Applies a change to a fresh working copy and commits it — unless <c>validate</c> finds the
    /// record no longer writes and reads back (an empty row some lists cannot hold): then nothing
    /// changes and the log says why, instead of a plugin the game or the editor cannot read.
    /// With several records, one that the change does not fit is left out (and logged).
    /// </summary>
    public void Apply(string name, Action<IMajorRecord> change)
    {
        var edited = new List<IMajorRecord>(_copies.Count);
        foreach (var copy in _copies)
        {
            try
            {
                var working = copy();
                change(working);
                if (_validate?.Invoke(working) is { } problem)
                {
                    Services.Log.Warn(TF($"{name}: not applied — the record would not save correctly ({problem})"));
                    if (_copies.Count == 1) return;
                    continue;
                }
                edited.Add(working);
            }
            catch (Exception ex)
            {
                Services.Log.Warn($"edit {name}: {ex.InnerException?.Message ?? ex.Message}");
                if (_copies.Count == 1) return;
            }
        }
        if (edited.Count > 0) _commit(name, edited);
    }
}

/// <summary>
/// One editor per field kind, shared by the form tabs and the full field tree. Numbers are typed as
/// text and kept with every digit (no "%.2f" rounding as the Creation Kit does in perk entries);
/// text commits when the field loses focus, so one change is one undo step.
/// </summary>
internal static class FieldEditors
{
    /// <summary>Text being typed / colour being dragged, by the widget's ImGui ID (rows of a table share field paths).</summary>
    private static readonly Dictionary<uint, string> Typing = new();
    private static readonly Dictionary<uint, Vector4> Colors = new();

    public static string PathKey(IReadOnlyList<object> path) => string.Join('/', path);

    /// <summary>"PlayerSkills" → "Player Skills", "AIData" → "AI Data", then translated.</summary>
    public static string Label(string name)
    {
        if (name.StartsWith('[')) return name;
        var spaced = System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        return T(spaced);
    }

    /// <summary>The editor (or the read-only view) of a leaf; <paramref name="label"/> names the undo step.</summary>
    public static void Draw(EditorApp app, EditTarget? target, string label, IReadOnlyList<object> path, object? value, Type? declared, bool editable)
    {
        var kind = RecordFields.KindOf(value, declared);
        var key = PathKey(path);
        ImGui.PushID(key);
        if (target is null || !editable)
        {
            View(app, value, kind, label);
        }
        else
        {
            var width = ImGui.GetContentRegionAvail().X - (target.Notes.ContainsKey(key) ? ImGui.GetFontSize() * 1.2f : 0);
            ImGui.SetNextItemWidth(width);
            Edit(app, target, label, path, key, value, declared, kind);
        }

        if (target is not null && target.Notes.TryGetValue(key, out var stored))
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.Warning, "!");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(TF($"The plugin file keeps this as: {stored}\n(the format stores it with less precision, or only under another setting)"));
        }
        ImGui.PopID();
    }

    private static void Set(EditTarget target, string label, IReadOnlyList<object> path, object? value) =>
        target.Apply(TF($"Set {label}"), copy => RecordFields.Set(copy, path, value));

    private static void Edit(EditorApp app, EditTarget target, string label, IReadOnlyList<object> path, string key, object? value, Type? declared, FieldKind kind)
    {
        var type = Nullable.GetUnderlyingType(declared ?? value?.GetType() ?? typeof(object)) ?? declared ?? value?.GetType();
        switch (kind)
        {
            case FieldKind.Link:
            {
                var link = value as IFormLinkGetter;
                FormKey? current = link?.FormKeyNullable is { IsNull: false } k ? k : null;
                var signatures = link is null ? null : SignaturesFor(link);
                var references = signatures is null && link is not null && IsReferenceLink(link);
                if (FormField.Draw(app, key, ref current, signatures, references))
                    Set(target, label, path, current);
                break;
            }
            case FieldKind.Bool:
            {
                var b = value as bool? ?? false;
                if (ImGui.Checkbox("##v", ref b)) Set(target, label, path, b);
                break;
            }
            case FieldKind.String:
            case FieldKind.Translated:
            {
                var text = value is ITranslatedStringGetter t ? t.String ?? string.Empty : value as string ?? string.Empty;
                if (TextBox(key, text, label is "Description" || text.Length > 80, out var done)) Set(target, label, path, done);
                break;
            }
            case FieldKind.Integer:
            {
                var current = value is null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
                var hex = label.Contains("Flags", StringComparison.OrdinalIgnoreCase);
                var shown = hex ? $"0x{current:X}" : current.ToString(CultureInfo.InvariantCulture);
                if (TextBox(key, shown, false, out var done))
                {
                    if (RecordFields.TryParseInteger(done, out var parsed) && FitsIn(parsed, type!)) Set(target, label, path, RecordFields.Coerce(parsed, type));
                    else Services.Log.Warn(TF($"{label}: \"{done}\" is not a whole number that fits ({type!.Name})"));
                }
                break;
            }
            case FieldKind.Float:
            case FieldKind.Double:
            {
                var current = value is null ? 0d : Convert.ToDouble(value, CultureInfo.InvariantCulture);
                var shown = kind == FieldKind.Float ? RecordFields.FloatText((float)current) : RecordFields.DoubleText(current);
                if (TextBox(key, shown, false, out var done))
                {
                    if (RecordFields.TryParseNumber(done, out var parsed)) Set(target, label, path, kind == FieldKind.Float ? (float)parsed : parsed);
                    else Services.Log.Warn(TF($"{label}: \"{done}\" is not a number"));
                }
                break;
            }
            case FieldKind.Percent:
            {
                var current = value is Percent p ? p.Value * 100 : 0;
                if (TextBox(key, RecordFields.DoubleText(current), false, out var done) && RecordFields.TryParseNumber(done.TrimEnd('%'), out var parsed))
                    Set(target, label, path, new Percent(Math.Clamp(parsed / 100, 0, 1)));
                ImGui.SameLine(0, 2);
                ImGui.TextColored(Theme.TextDim, "%");
                break;
            }
            case FieldKind.Enum:
            {
                if (!ImGui.BeginCombo("##v", value?.ToString() ?? "—")) break;
                foreach (var option in Enum.GetValues(type!).Cast<object>())
                    if (ImGui.Selectable(option.ToString()!, Equals(option, value)))
                        Set(target, label, path, option);
                ImGui.EndCombo();
                break;
            }
            case FieldKind.Flags:
            {
                var raw = value is null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
                if (!ImGui.BeginCombo("##v", raw == 0 ? T("(none)") : value!.ToString())) break;
                foreach (var flag in Enum.GetValues(type!).Cast<object>())
                {
                    var bit = Convert.ToInt64(flag, CultureInfo.InvariantCulture);
                    if (bit == 0) continue;
                    var on = (raw & bit) == bit;
                    if (ImGui.Checkbox(flag.ToString(), ref on))
                        Set(target, label, path, Enum.ToObject(type!, on ? raw | bit : raw & ~bit));
                }
                ImGui.EndCombo();
                break;
            }
            case FieldKind.P3Float:
            {
                var p = value as P3Float? ?? default;
                if (FloatRow(key, [p.X, p.Y, p.Z], out var v)) Set(target, label, path, new P3Float(v[0], v[1], v[2]));
                break;
            }
            case FieldKind.P2Float:
            {
                var p = value as P2Float? ?? default;
                if (FloatRow(key, [p.X, p.Y], out var v)) Set(target, label, path, new P2Float(v[0], v[1]));
                break;
            }
            case FieldKind.P3Int16:
            {
                var p = value as P3Int16? ?? default;
                if (FloatRow(key, [p.X, p.Y, p.Z], out var v)) Set(target, label, path, new P3Int16((short)v[0], (short)v[1], (short)v[2]));
                break;
            }
            case FieldKind.P2Int:
            {
                var p = value as P2Int? ?? default;
                if (FloatRow(key, [p.X, p.Y], out var v)) Set(target, label, path, new P2Int((int)v[0], (int)v[1]));
                break;
            }
            case FieldKind.Color:
            {
                var c = value as System.Drawing.Color? ?? System.Drawing.Color.Black;
                var colorId = ImGui.GetID("##v");
                var v = Colors.TryGetValue(colorId, out var pending) ? pending : new Vector4(c.R, c.G, c.B, c.A) / 255f;
                if (ImGui.ColorEdit4("##v", ref v, ImGuiColorEditFlags.Uint8 | ImGuiColorEditFlags.AlphaBar)) Colors[colorId] = v;
                if (ImGui.IsItemDeactivatedAfterEdit() && Colors.Remove(colorId, out var done))
                {
                    var b = done * 255f;
                    Set(target, label, path, System.Drawing.Color.FromArgb((int)MathF.Round(b.W), (int)MathF.Round(b.X), (int)MathF.Round(b.Y), (int)MathF.Round(b.Z)));
                }
                if (!ImGui.IsItemActive()) Colors.Remove(colorId);
                break;
            }
            default:
                View(app, value, kind, label);
                break;
        }
    }

    private static bool FitsIn(long value, Type type) =>
        type.GetField("MinValue")?.GetValue(null) is IConvertible min && type.GetField("MaxValue")?.GetValue(null) is IConvertible max
        && value >= min.ToDecimal(CultureInfo.InvariantCulture) && value <= max.ToDecimal(CultureInfo.InvariantCulture);

    /// <summary>A text box that reports its text once editing ends (focus left / Enter), not on every key.</summary>
    public static bool TextBox(string key, string value, bool multiline, out string result)
    {
        var id = ImGui.GetID("##v");
        var text = Typing.TryGetValue(id, out var typing) ? typing : value;
        var changed = multiline
            ? ImGui.InputTextMultiline("##v", ref text, 8192, new Vector2(ImGui.CalcItemWidth(), ImGui.GetTextLineHeight() * 4.5f))
            : ImGui.InputText("##v", ref text, 1024);
        if (changed) Typing[id] = text;
        if (ImGui.IsItemDeactivatedAfterEdit() && Typing.Remove(id, out var done) && done != value)
        {
            result = done;
            return true;
        }
        if (!ImGui.IsItemActive()) Typing.Remove(id);
        result = value;
        return false;
    }

    /// <summary>X / Y / Z as text boxes side by side (exact digits).</summary>
    private static bool FloatRow(string key, float[] values, out float[] result)
    {
        result = (float[])values.Clone();
        var changed = false;
        var spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var width = (ImGui.CalcItemWidth() - spacing * (values.Length - 1)) / values.Length;
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0) ImGui.SameLine(0, spacing);
            ImGui.PushID(i);
            ImGui.SetNextItemWidth(width);
            if (TextBox(key + "/" + i, RecordFields.FloatText(values[i]), false, out var done) && RecordFields.TryParseNumber(done, out var parsed))
            {
                result[i] = (float)parsed;
                changed = true;
            }
            ImGui.PopID();
        }
        return changed;
    }

    /// <summary>Read-only view of a value.</summary>
    public static void View(EditorApp app, object? value, FieldKind kind, string label = "")
    {
        switch (value)
        {
            case null:
                ImGui.TextColored(Theme.TextDim, "—");
                break;
            case IFormLinkGetter link:
                DrawLink(app, link.FormKeyNullable);
                break;
            case FormKey key:
                DrawLink(app, key);
                break;
            case ITranslatedStringGetter translated:
                ImGui.TextWrapped(translated.String ?? string.Empty);
                break;
            case System.Drawing.Color color:
                ImGui.ColorButton("##c", new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, 1f), ImGuiColorEditFlags.NoTooltip,
                    new Vector2(ImGui.GetFontSize() * 1.6f, ImGui.GetFontSize()));
                ImGui.SameLine();
                ImGui.Text($"{color.R}, {color.G}, {color.B}, {color.A}");
                break;
            case float f:
                ImGui.Text(RecordFields.FloatText(f));
                break;
            case double d:
                ImGui.Text(RecordFields.DoubleText(d));
                break;
            case bool b:
                ImGui.TextColored(b ? Theme.Good : Theme.TextDim, b ? "true" : "false");
                break;
            case Enum e:
                ImGui.Text(e.ToString());
                ImGui.SameLine();
                ImGui.TextColored(Theme.TextDim, $"(0x{Convert.ToInt64(e, CultureInfo.InvariantCulture):X})");
                break;
            case int or uint when label.Contains("Flags", StringComparison.OrdinalIgnoreCase):
                ImGui.Text($"0x{Convert.ToUInt32(value is int i ? unchecked((uint)i) : value):X8}");
                break;
            case string s:
                ImGui.TextWrapped(s);
                break;
            default:
                if (kind == FieldKind.Bytes)
                {
                    ImGui.PushFont(app.Ui.MonoFont, 0);
                    ImGui.TextWrapped(Hex(value));
                    ImGui.PopFont();
                }
                else ImGui.TextWrapped(value.ToString() ?? string.Empty);
                break;
        }
    }

    private static string Hex(object value)
    {
        ReadOnlySpan<byte> bytes = value switch
        {
            byte[] a => a,
            ReadOnlyMemorySlice<byte> s => s.Span,
            MemorySlice<byte> m => m.Span,
            _ => [],
        };
        const int max = 64;
        var text = Convert.ToHexString(bytes.Length > max ? bytes[..max] : bytes);
        var spaced = string.Join(' ', Enumerable.Range(0, text.Length / 2).Select(i => text.Substring(i * 2, 2)));
        return bytes.Length > max ? TF($"{spaced} … ({bytes.Length} bytes)") : TF($"{spaced}  ({bytes.Length} bytes)");
    }

    public static void DrawLink(EditorApp app, FormKey? key)
    {
        if (key is not { IsNull: false } k)
        {
            ImGui.TextColored(Theme.TextDim, "NULL");
            return;
        }
        var session = app.Session!;
        var record = session.Catalog.Get(k);
        var text = record is null
            ? TF($"{session.Workspace.FormIdText(k)}  (not in catalog)")
            : $"{record.EditorId ?? record.Name ?? "?"}  [{record.Signature}:{session.Workspace.FormIdText(k)}]";
        ImGui.PushStyleColor(ImGuiCol.Text, record is null ? Theme.TextDim : Theme.Accent);
        if (ImGui.Selectable(text, false)) app.Select(k);
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(TF($"{k}\nClick to open"));
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, string[]?> LinkSignatures = new();

    /// <summary>Record signatures a link can point to (from its target getter type).</summary>
    public static string[]? SignaturesFor(IFormLinkGetter link)
    {
        var target = link.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IFormLinkGetter<>))?.GetGenericArguments()[0];
        if (target is null) return null;
        return LinkSignatures.GetOrAdd(target, static t =>
        {
            var list = StbEditor.Data.Catalog.RecordTypes.Signatures
                .Where(sig => StbEditor.Data.RecordLookup.GetterType(sig) is { } g && t.IsAssignableFrom(g)).ToArray();
            return list.Length == 0 || list.Length > 40 ? null : list;
        });
    }

    public static bool IsReferenceLink(IFormLinkGetter link) =>
        link.GetType().GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IFormLinkGetter<>)
            && i.GetGenericArguments()[0].Name is "IPlacedGetter" or "IPlacedObjectGetter" or "IPlacedNpcGetter" or "ILinkedReferenceGetter" or "IPlacedSimpleGetter");

    /// <summary>
    /// Fields of <paramref name="edited"/> the plugin file would store differently (path → stored
    /// value): the record written alone and read back.
    /// </summary>
    public static Dictionary<string, string> FormatNotes(IMajorRecordGetter edited, EditorApp app)
    {
        var notes = new Dictionary<string, string>();
        var session = app.Session!;
        var encoding = StbEditor.Data.Raw.StringEncodingDetector.WriteEncoding(
            app.ActivePlugin?.Encoding ?? session.Workspace.DefaultEncoding, session.Game.Language);
        if (RecordRoundTrip.Read(edited, session.Game.Release, encoding) is not { } stored) return notes;
        var back = RecordFields.Leaves(stored).ToDictionary(l => l.PathText, l => RecordFields.Repr(l.Value));
        foreach (var leaf in RecordFields.Leaves(edited))
        {
            if (!leaf.Editable) continue;
            var mine = RecordFields.Repr(leaf.Value);
            var theirs = back.GetValueOrDefault(leaf.PathText, "null");
            if (mine != theirs && !(leaf.Value is null && theirs is "" or "0")) notes[leaf.PathText] = theirs == "null" ? T("(not saved)") : theirs;
        }
        return notes;
    }
}
