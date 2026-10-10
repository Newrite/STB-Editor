using System.Buffers.Binary;
using Mutagen.Bethesda.Plugins;
using StbEditor.Data.Raw;

namespace StbEditor.Data;

/// <summary>
/// Which cell each teleport door (a REFR with XTEL) stands in, for "go to the linked door". The
/// catalog does not index references and resolving a placed object through Mutagen indexes all of
/// them (a million records), so the plugin files are walked raw instead: only the CELL / WRLD top
/// groups, only REFR bodies, ~1–2 s for a full load order. Later plugins win (a mod may move a door).
/// </summary>
public sealed class DoorIndex
{
    private readonly Dictionary<FormKey, FormKey> _cellOf = new();

    public int Count => _cellOf.Count;

    public FormKey? CellOf(FormKey door) => _cellOf.TryGetValue(door, out var cell) ? cell : null;

    public static DoorIndex Build(Workspace workspace, CancellationToken cancel = default)
    {
        var index = new DoorIndex();
        foreach (var plugin in workspace.Active)
        {
            cancel.ThrowIfCancellationRequested();
            if (plugin.Header is not { } header || !plugin.Location.Exists) continue;
            try
            {
                index.Scan(plugin, header);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentOutOfRangeException)
            {
                // A broken plugin loses its doors only.
            }
        }
        return index;
    }

    private void Scan(PluginInfo plugin, PluginHeader header)
    {
        var masters = header.Masters.Select(m => ModKey.FromFileName(m)).ToList();
        FormKey Key(uint local)
        {
            var index = (int)(local >> 24);
            var mod = index < masters.Count ? masters[index] : plugin.ModKey;
            return new FormKey(mod, local & 0xFFFFFF);
        }

        var file = File.ReadAllBytes(plugin.Location.Path);
        // Groups open at a position and close at their end; for cell children (types 6, 8, 9, 10)
        // the label is the cell's FormID.
        var groups = new Stack<(long End, uint Cell)>();
        long pos = 0;
        var tes4Size = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        pos = RawFormat.RecordHeaderSize + tes4Size;

        while (pos + RawFormat.RecordHeaderSize <= file.Length)
        {
            while (groups.Count > 0 && pos >= groups.Peek().End) groups.Pop();
            var span = file.AsSpan((int)pos);
            var type = RawFormat.FourCC(span);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);

            if (type == "GRUP")
            {
                var label = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                var groupType = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
                if (groupType == 0 && label != Sig("CELL") && label != Sig("WRLD"))
                {
                    pos += size; // a top group of something else: skip it whole
                    continue;
                }
                var cell = groupType is 6 or 8 or 9 or 10 ? label : groups.Count > 0 ? groups.Peek().Cell : 0;
                groups.Push((pos + size, cell));
                pos += RawFormat.GroupHeaderSize;
                continue;
            }

            var bodyStart = pos + RawFormat.RecordHeaderSize;
            if (bodyStart + size > file.Length) break;
            if (type == "REFR" && groups.Count > 0 && groups.Peek().Cell != 0)
            {
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                var formId = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
                ReadOnlyMemory<byte> body = file.AsMemory((int)bodyStart, (int)size);
                if ((flags & RawFormat.CompressedFlag) != 0) body = RawFormat.Inflate(body);
                if (RawFormat.Subrecords(body).Any(s => s.Type == "XTEL"))
                    _cellOf[Key(formId)] = Key(groups.Peek().Cell);
            }
            pos = bodyStart + size;
        }
    }

    private static uint Sig(string s) => (uint)(s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24);
}
