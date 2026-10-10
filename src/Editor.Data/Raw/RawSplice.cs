using System.Buffers.Binary;

namespace StbEditor.Data.Raw;

/// <summary>
/// Surgical save: takes the plugin a full writer produced and puts back the original bytes of
/// every record that was not edited, so untouched data survive exactly as they were (no
/// normalisation, no re-compression, unknown subrecords kept). Group sizes are recomputed.
///
/// Valid because existing masters keep their indices (new masters are appended), so FormIDs
/// inside untouched records still mean the same records.
/// </summary>
public static class RawSplice
{
    /// <param name="original">The plugin as it was on disk.</param>
    /// <param name="written">The full rewrite with the edits.</param>
    /// <param name="edited">File FormIDs (as stored in the original) whose new bytes must be kept.</param>
    public static byte[] Splice(byte[] original, byte[] written, ISet<uint> edited, out int restored)
    {
        var originals = new Dictionary<(uint Type, uint FormId), (int Offset, int Length)>();
        foreach (var (type, _, formId, offset, size) in RawFormat.RecordHeaders(original))
        {
            if (type == "TES4" || edited.Contains(formId)) continue;
            originals[(TypeKey(type), formId)] = ((int)offset, RawFormat.RecordHeaderSize + (int)size);
        }

        using var output = new MemoryStream(written.Length + written.Length / 8);
        restored = 0;
        CopyRegion(written, 0, written.Length, output, original, originals, ref restored);
        return output.ToArray();
    }

    private static void CopyRegion(byte[] source, int start, int end, MemoryStream output, byte[] original,
        Dictionary<(uint, uint), (int Offset, int Length)> originals, ref int restored)
    {
        var pos = start;
        while (pos + RawFormat.RecordHeaderSize <= end)
        {
            var span = source.AsSpan(pos);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(span);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);

            if (type == GrupKey)
            {
                // Header now, size patched once the children are written.
                var headerAt = (int)output.Position;
                output.Write(source, pos, RawFormat.GroupHeaderSize);
                CopyRegion(source, pos + RawFormat.GroupHeaderSize, pos + size, output, original, originals, ref restored);
                var groupSize = (int)output.Position - headerAt;
                var buffer = output.GetBuffer();
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(headerAt + 4), (uint)groupSize);
                pos += size;
                continue;
            }

            var formId = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
            var length = RawFormat.RecordHeaderSize + size;

            if (originals.TryGetValue((type, formId), out var orig))
            {
                output.Write(original, orig.Offset, orig.Length);
                restored++;
            }
            else
            {
                output.Write(source, pos, length);
            }

            pos += length;
        }
    }

    private static readonly uint GrupKey = TypeKey("GRUP");

    private static uint TypeKey(string type) =>
        (uint)type[0] | ((uint)type[1] << 8) | ((uint)type[2] << 16) | ((uint)type[3] << 24);
}
