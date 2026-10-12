using System.Globalization;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Strings;
using Noggog;
using StbEditor.Core;
using StbEditor.Data;
using StbEditor.Data.Catalog;

/// <summary>
/// formtest &lt;game&gt; &lt;out folder&gt; [records per type] [signature…]: for records of every editable type,
/// change every field the Inspector can edit (through <see cref="RecordFields"/>, as the UI does),
/// save into a new plugin and read it back. Every changed field must read back as written.
/// </summary>
internal static class FormTest
{
    /// <summary>Records that are not field-edited in the Inspector (the same list as InspectorPanel).</summary>
    private static readonly HashSet<string> NotFieldEditable = ["CELL", "WRLD", "LAND", "NAVM", "NAVI", "DIAL", "INFO", "SCEN", "TES4"];

    public static void Run(Workspace ws, GameInstall game, RecordCatalog catalog, string outDir, int perType, string[] only)
    {
        Directory.CreateDirectory(outDir);
        var active = ActivePlugin.ForNew(Path.Combine(outDir, "STBEditorFormTest.esp"), light: false, master: false, game, ws.DefaultEncoding);
        var lookup = new RecordLookup(ws, catalog);
        var doc = new EditDocument();
        var expected = new Dictionary<FormKey, List<(IReadOnlyList<object> Path, string Value)>>();
        var setErrors = new Dictionary<string, List<string>>();
        var counts = new Dictionary<string, (int Records, int Fields)>();
        var copies = new Dictionary<FormKey, IMajorRecord>();
        var refused = new Dictionary<string, List<string>>();

        foreach (var (signature, records) in catalog.BySignature.OrderBy(kv => kv.Key))
        {
            if (NotFieldEditable.Contains(signature) || (only.Length > 0 && !only.Contains(signature))) continue;
            // A few records from different plugins (vanilla and mods), the fuller ones first.
            var picks = records.Where(r => !r.IsTransient).GroupBy(r => r.WinningPlugin.Name).Select(g => g.First()).Take(perType).ToList();
            foreach (var record in picks)
            {
                if (lookup.Resolve(record.FormKey) is not { } winner) continue;
                IMajorRecord copy;
                try { copy = doc.WorkingCopy(record.FormKey, winner); }
                catch (Exception ex) { Note(setErrors, signature, $"copy {record.EditorId}: {ex.Message}"); continue; }

                var changes = new List<(IReadOnlyList<object> Path, string Value)>();
                foreach (var leaf in RecordFields.Leaves(copy).Where(l => l.Editable))
                {
                    // Condition flags carry derived bits (UseGlobal follows the condition type): the condition editor's job.
                    if (leaf.Path.Count >= 2 && leaf.Path[^1] is "Flags" && leaf.Path.Contains("Conditions")) continue;
                    if (NewValue(leaf) is not { } value) continue;
                    try
                    {
                        RecordFields.Set(copy, leaf.Path, value.Value);
                        changes.Add((leaf.Path, RecordFields.Repr(RecordFields.Get(copy, leaf.Path))));
                    }
                    catch (Exception ex)
                    {
                        Note(setErrors, signature, $"set {leaf.PathText} ({leaf.Kind}): {ex.InnerException?.Message ?? ex.Message}");
                    }
                }
                // «+ add» on every list of the record (each kind for abstract items): the plugin must still write and read.
                foreach (var member in RecordFields.PropertiesOf(copy))
                {
                    if (RecordFields.ClassProperty(copy, member.Name) is not { } property) continue;
                    if (!typeof(System.Collections.IList).IsAssignableFrom(property.PropertyType) || property.GetValue(copy) is not System.Collections.IList { IsFixedSize: false } list) continue;
                    if (RecordFields.ElementType(list) is not { } element) continue;
                    var kinds = RecordFields.Creatable(element) is not null || element.IsValueType || element == typeof(string) ? [null] : RecordFields.ConcreteTypes(element).Cast<Type?>().ToList();
                    foreach (var kind in kinds)
                    {
                        try
                        {
                            RecordFields.AddItem(copy, [member.Name], kind);
                            // As the Inspector does: a change that leaves the record unreadable is refused.
                            string? broken = null;
                            try { copy.DeepCopy(); } catch (Exception ex) { broken = (ex.InnerException ?? ex).Message; }
                            if (broken is null && RecordRoundTrip.Read(copy, game.Release, StbEditor.Data.Raw.StringEncodingDetector.WriteEncoding(active.Encoding, game.Language)) is null)
                                broken = RecordRoundTrip.LastError;
                            if (broken is not null)
                            {
                                list.RemoveAt(list.Count - 1);
                                Note(refused, signature, $"add {member.Name} {kind?.Name}: {broken}");
                                continue;
                            }
                            changes.RemoveAll(c => c.Path.Count == 2 && Equals(c.Path[0], member.Name) && Equals(c.Path[1], "#count"));
                            changes.Add(([member.Name, "#count"], list.Count.ToString()));
                        }
                        catch (Exception ex) { Note(setErrors, signature, $"add {member.Name} {kind?.Name}: {ex.InnerException?.Message ?? ex.Message}"); }
                    }
                }
                if (changes.Count == 0) continue;
                doc.CommitEdit($"formtest {record.EditorId}", copy, RecordPlace.TopLevel);
                expected[record.FormKey] = changes;
                copies[record.FormKey] = copy;
                var c = counts.GetValueOrDefault(signature);
                counts[signature] = (c.Records + 1, c.Fields + changes.Count);
            }
        }

        Console.WriteLine($"edited {expected.Count} records of {counts.Count} types, {counts.Values.Sum(c => c.Fields)} fields; saving");
        var result = new PluginWriter(ws, catalog).Prepare(active, doc);
        Console.WriteLine($"prepare: success={result.Success} error={result.Error}; overridden {result.Overridden}, created {result.Created}");
        foreach (var w in result.Warnings.Take(20)) Console.WriteLine("  warn " + w);
        if (!result.Success)
        {
            // Which records break the writer: each one alone into a scratch plugin.
            var probe = Path.Combine(outDir, "probe.esp");
            foreach (var draft in doc.Drafts.Values.OfType<OverrideDraft>())
            {
                var mod = new Mutagen.Bethesda.Skyrim.SkyrimMod(ModKey.FromFileName("probe.esp"), game.Release);
                var probeRecord = (IMajorRecord)draft.Record.DeepCopy();
                if (!PluginWriter.AddTopLevel(mod, probeRecord)) continue;
                foreach (var master in probeRecord.EnumerateFormLinks().Select(l => l.FormKey.ModKey).Append(probeRecord.FormKey.ModKey)
                             .Where(m => !m.IsNull).Distinct())
                    mod.ModHeader.MasterReferences.Add(new Mutagen.Bethesda.Plugins.Records.MasterReference { Master = master });
                try
                {
                    mod.WriteToBinary(probe, new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters
                    {
                        ModKey = Mutagen.Bethesda.Plugins.Binary.Parameters.ModKeyOption.NoCheck,
                        MastersListContent = Mutagen.Bethesda.Plugins.Binary.Parameters.MastersListContentOption.NoCheck,
                        MastersListOrdering = Mutagen.Bethesda.Plugins.Binary.Parameters.MastersListOrderingOption.NoCheck,
                    });
                }
                catch (Exception ex)
                {
                    var inner = ex;
                    while (inner.InnerException is not null) inner = inner.InnerException;
                    var frame = inner.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("Mutagen"))?.Trim();
                    Console.WriteLine($"  write fails: {catalog.Get(draft.Key)?.Signature} {draft.EditorId}: {inner.GetType().Name} {inner.Message} @ {frame}");
                }
            }
            return;
        }
        File.Move(result.TempPath!, active.Path, overwrite: true);

        // Read back with the encoding the writer used (the plugin's text encoding), as the editor does.
        var encoding = new Mutagen.Bethesda.Strings.MutagenEncodingWrapper(StbEditor.Data.Raw.StringEncodingDetector.WriteEncoding(active.Encoding, game.Language));
        var check = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinary(new ModPath(ModKey.FromFileName(Path.GetFileName(active.Path)), active.Path), game.Release, new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryReadParameters
        {
            StringsParam = new Mutagen.Bethesda.Strings.StringsReadParameters { NonTranslatedEncodingOverride = encoding, NonLocalizedEncodingOverride = encoding },
        });
        var back = check.EnumerateMajorRecords().ToDictionary(r => r.FormKey);
        var mismatches = new Dictionary<string, List<string>>();
        var predicted = 0;
        var textEncoding = StbEditor.Data.Raw.StringEncodingDetector.WriteEncoding(active.Encoding, game.Language);
        foreach (var (key, changes) in expected)
        {
            var signature = catalog.Get(key)!.Signature;
            if (!back.TryGetValue(key, out var read)) { Note(mismatches, signature, $"{key}: missing after save"); continue; }
            // What the Inspector would warn about: the same record written and read back in memory.
            var trip = RecordRoundTrip.Read(copies[key], game.Release, textEncoding);
            if (trip is null) Console.WriteLine($"  round trip failed for {signature} {copies[key].EditorID}: {RecordRoundTrip.LastError}");
            foreach (var (path, value) in changes)
            {
                string actual;
                if (path[^1] is "#count")
                {
                    var count = RecordFields.Get(read, path.Take(path.Count - 1).ToList()) is System.Collections.IEnumerable items ? items.Cast<object>().Count() : 0;
                    if (count.ToString() == value) continue;
                    var tripCount = trip is not null && RecordFields.Get(trip, path.Take(path.Count - 1).ToList()) is System.Collections.IEnumerable tripItems ? tripItems.Cast<object>().Count() : -1;
                    if (tripCount == count) { predicted++; Note(mismatches, signature, $"(format) {path[0]}: {value} items after add, file keeps {count} (empty rows dropped)"); }
                    else Note(mismatches, signature, $"UNEXPECTED {path[0]}: {value} items after add, read {count}");
                    continue;
                }
                try { actual = RecordFields.Repr(RecordFields.Get(read, path)); }
                catch (Exception ex) { actual = "error " + (ex.InnerException?.Message ?? ex.Message); }
                if (actual == value) continue;
                if (trip is not null && RecordFields.Repr(RecordFields.Get(trip, path)) == actual) { predicted++; Note(mismatches, signature, $"(format) {string.Join('/', path)}: wrote {value}, file keeps {actual}"); continue; }
                Note(mismatches, signature, $"UNEXPECTED {string.Join('/', path)}: wrote {value}, read {actual}");
            }
        }

        foreach (var (signature, (records, fields)) in counts.OrderBy(kv => kv.Key))
        {
            var bad = mismatches.GetValueOrDefault(signature) ?? [];
            var errors = setErrors.GetValueOrDefault(signature) ?? [];
            Console.WriteLine($"{signature}: {records} record(s), {fields} field(s) edited, {bad.Count} mismatch(es), {errors.Count} set error(s)");
            foreach (var line in bad.Where(l => l.StartsWith("UNEXPECTED")).Concat(bad.Where(l => !l.StartsWith("UNEXPECTED")).Take(4)).Distinct())
                Console.WriteLine("    mismatch " + line);
            foreach (var line in errors.Distinct().Take(5)) Console.WriteLine("    error " + line);
        }
        foreach (var (signature, errors) in setErrors.Where(kv => !counts.ContainsKey(kv.Key)))
            foreach (var line in errors.Distinct().Take(5)) Console.WriteLine($"{signature}: error {line}");
        foreach (var (signature, lines) in refused)
            foreach (var line in lines.Distinct().Take(3)) Console.WriteLine($"{signature}: refused {line}");
        var all = mismatches.Values.Sum(l => l.Count);
        Console.WriteLine($"total: {counts.Values.Sum(c => c.Fields)} fields, {all} differ after save: {predicted} format limits the Inspector shows, {all - predicted} unexpected; {setErrors.Values.Sum(l => l.Count)} set errors, {refused.Values.Sum(l => l.Count)} adds refused (would break the record)");
    }

    private static void Note(Dictionary<string, List<string>> map, string signature, string line)
    {
        if (!map.TryGetValue(signature, out var list)) map[signature] = list = [];
        list.Add(line);
    }

    /// <summary>A different value of the same kind; null skips the field (links are tested by proptest).</summary>
    private static Box? NewValue(FieldLeaf leaf)
    {
        var v = leaf.Value;
        var type = Nullable.GetUnderlyingType(leaf.DeclaredType ?? v?.GetType() ?? typeof(object)) ?? leaf.DeclaredType ?? v?.GetType();
        switch (leaf.Kind)
        {
            case FieldKind.String:
                // EditorIDs stay ASCII (the engine wants them so); other text gets Cyrillic to test encodings.
                return new((v as string ?? "") + (leaf.Path[^1] is "EditorID" ? "_T" : "_Тест"));
            case FieldKind.Translated:
                return new(((v as ITranslatedStringGetter)?.String ?? "") + " Тест");
            case FieldKind.Bool:
                return new(!(v as bool? ?? false));
            case FieldKind.Integer:
            {
                var current = v is null ? 0 : Convert.ToDecimal(v, CultureInfo.InvariantCulture);
                var max = Convert.ToDecimal(type!.GetField("MaxValue")!.GetValue(null), CultureInfo.InvariantCulture);
                return new(RecordFields.Coerce(current < max ? current + 1 : current - 1, type));
            }
            case FieldKind.Float:
            {
                var f = v as float? ?? 0f;
                if (float.IsNaN(f) || float.IsInfinity(f)) return null;
                // Fine digits on purpose: the Creation Kit keeps only two decimals in some fields.
                // Some floats are stored as 0..1 fractions: step down from 1.
                return new(f == 1f ? 0.999877f : f + 0.000123f + (MathF.Abs(f) > 1000 ? 1f : 0f));
            }
            case FieldKind.Double:
                return new((v as double? ?? 0d) + 0.000123d);
            case FieldKind.Enum:
            {
                var values = Enum.GetValues(type!).Cast<object>().ToList();
                var index = v is null ? -1 : values.IndexOf(v);
                return values.Count < 2 ? null : new(values[(index + 1) % values.Count]);
            }
            case FieldKind.Flags:
            {
                var raw = v is null ? 0UL : unchecked((ulong)Convert.ToInt64(v, CultureInfo.InvariantCulture));
                var bit = Enum.GetValues(type!).Cast<object>().Select(x => unchecked((ulong)Convert.ToInt64(x, CultureInfo.InvariantCulture))).FirstOrDefault(x => x != 0 && (x & (x - 1)) == 0);
                return bit == 0 ? null : new(Enum.ToObject(type!, unchecked((long)(raw ^ bit))));
            }
            case FieldKind.P3Float:
            {
                var p = v as P3Float? ?? default;
                return new(new P3Float(p.X + 0.5f, p.Y + 0.25f, p.Z + 0.125f));
            }
            case FieldKind.P2Float:
            {
                var p = v as P2Float? ?? default;
                return new(new P2Float(p.X + 0.5f, p.Y + 0.25f));
            }
            case FieldKind.P3Int16:
            {
                var p = v as P3Int16? ?? default;
                return new(new P3Int16((short)(p.X + 1), (short)(p.Y + 1), (short)(p.Z + 1)));
            }
            case FieldKind.P2Int:
            {
                var p = v as P2Int? ?? default;
                return new(new P2Int(p.X + 1, p.Y + 1));
            }
            case FieldKind.Color:
            {
                var c = v as System.Drawing.Color? ?? System.Drawing.Color.Black;
                return new(System.Drawing.Color.FromArgb(c.A, (c.R + 1) % 256, c.G, c.B));
            }
            case FieldKind.Percent:
            {
                var p = v as Percent? ?? default;
                return new(new Percent(p.Value < 0.5 ? 0.75 : 0.25));
            }
            default:
                return null;
        }
    }

    private sealed record Box(object? Value);

}
