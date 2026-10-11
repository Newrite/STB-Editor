using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;

namespace StbEditor.Core;

/// <summary>
/// What the plugin file will actually keep of a record: it is written alone into an in-memory
/// plugin and read back. Some fields are stored with less precision than the editor shows (armor
/// rating in hundredths, many fractions as a byte), some exist only under a flag or a type
/// (perk entry button labels) — the Inspector marks such fields after an edit instead of letting
/// the change silently vanish on save.
/// </summary>
public static class RecordRoundTrip
{
    /// <summary>Last failure of <see cref="Read"/> (for diagnostics).</summary>
    public static string? LastError { get; private set; }

    public static IMajorRecordGetter? Read(IMajorRecordGetter record, SkyrimRelease release, System.Text.Encoding encoding)
    {
        LastError = null;
        try
        {
            var copy = (IMajorRecord)record.DeepCopy();
            // A probe plugin of its own; the record's plugin and everything it links to are its masters
            // (vanilla records have FormIDs in the low range a new plugin may not own).
            var probe = ModKey.FromFileName("STBEditorRoundTrip.esp");
            var mod = new SkyrimMod(probe, release);
            if (!PluginWriter.AddTopLevel(mod, copy)) return null;
            foreach (var master in copy.EnumerateFormLinks().Select(l => l.FormKey.ModKey)
                         .Append(copy.FormKey.ModKey).Where(m => !m.IsNull).Distinct())
                mod.ModHeader.MasterReferences.Add(new MasterReference { Master = master });

            var wrapper = new MutagenEncodingWrapper(encoding);
            var path = Path.Combine(Path.GetTempPath(), $"stbeditor-roundtrip-{Environment.ProcessId}.esp");
            try
            {
                mod.WriteToBinary(path, new BinaryWriteParameters
                {
                    ModKey = ModKeyOption.NoCheck,
                    MastersListContent = MastersListContentOption.NoCheck,
                    MastersListOrdering = MastersListOrderingOption.NoCheck,
                    Encodings = new EncodingBundle(wrapper, wrapper),
                });
                // Read fully (not as an overlay): nothing may keep the file open.
                var back = SkyrimMod.CreateFromBinary(new ModPath(probe, path), release, new BinaryReadParameters
                {
                    StringsParam = new StringsReadParameters { NonTranslatedEncodingOverride = wrapper, NonLocalizedEncodingOverride = wrapper },
                });
                return back.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == copy.FormKey);
            }
            finally
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }
        catch (Exception ex)
        {
            LastError = (ex.InnerException ?? ex).Message;
            return null;
        }
    }
}
