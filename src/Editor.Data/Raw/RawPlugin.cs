using System.Buffers.Binary;
using System.IO.Compression;

namespace StbEditor.Data.Raw;

/// <summary>Flags of the TES4 header record.</summary>
[Flags]
public enum PluginFlags : uint
{
    None = 0,
    Master = 0x1,
    Localized = 0x80,
    Light = 0x200,
}

/// <summary>Plugin header read straight from the file, without Mutagen.</summary>
public sealed record PluginHeader(
    PluginFlags Flags,
    float Version,
    int RecordCount,
    uint NextObjectId,
    string? Author,
    string? Description,
    IReadOnlyList<string> Masters,
    int FormVersion)
{
    public bool IsMasterFlagged => Flags.HasFlag(PluginFlags.Master);
    public bool IsLightFlagged => Flags.HasFlag(PluginFlags.Light);
    public bool IsLocalized => Flags.HasFlag(PluginFlags.Localized);

    /// <summary>
    /// Reads the TES4 record. Only the header is touched, so this works for files Mutagen cannot
    /// parse and catches ESL-flagged .esp files.
    /// </summary>
    public static PluginHeader Read(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[RawFormat.RecordHeaderSize];
        stream.ReadExactly(head);

        if (!head[..4].SequenceEqual("TES4"u8))
            throw new InvalidDataException($"{Path.GetFileName(path)}: not a TES4 plugin");

        var dataSize = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
        var flags = (PluginFlags)BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);
        var formVersion = BinaryPrimitives.ReadUInt16LittleEndian(head[20..]);

        var data = new byte[dataSize];
        stream.ReadExactly(data);

        float version = 0;
        int count = 0;
        uint nextId = 0;
        string? author = null, description = null;
        var masters = new List<string>();

        foreach (var sub in RawFormat.Subrecords(data))
        {
            switch (sub.Type)
            {
                case "HEDR" when sub.Data.Length >= 12:
                    version = BinaryPrimitives.ReadSingleLittleEndian(sub.Data.Span);
                    count = BinaryPrimitives.ReadInt32LittleEndian(sub.Data.Span[4..]);
                    nextId = BinaryPrimitives.ReadUInt32LittleEndian(sub.Data.Span[8..]);
                    break;
                case "CNAM":
                    author = RawFormat.ZString(sub.Data.Span);
                    break;
                case "SNAM":
                    description = RawFormat.ZString(sub.Data.Span);
                    break;
                case "MAST":
                    masters.Add(RawFormat.ZString(sub.Data.Span));
                    break;
            }
        }

        return new PluginHeader(flags, version, count, nextId, author, description, masters, formVersion);
    }
}

public readonly record struct RawSubrecord(string Type, ReadOnlyMemory<byte> Data);

/// <summary>A record found while walking a plugin. <see cref="Data"/> is decompressed.</summary>
public readonly record struct RawRecord(string Type, uint Flags, uint FormId, long Offset, ReadOnlyMemory<byte> Data)
{
    public bool IsCompressed => (Flags & RawFormat.CompressedFlag) != 0;
}

/// <summary>Binary layout of Skyrim SE plugins (UESP "Mod File Format").</summary>
public static class RawFormat
{
    public const int RecordHeaderSize = 24;
    public const int GroupHeaderSize = 24;
    public const uint CompressedFlag = 0x00040000;

    /// <summary>Subrecords of a record body. Handles XXXX (size of the next subrecord above 64 KB).</summary>
    public static IEnumerable<RawSubrecord> Subrecords(ReadOnlyMemory<byte> data)
    {
        var pos = 0;
        uint? bigSize = null;

        while (pos + 6 <= data.Length)
        {
            var span = data.Span;
            var type = FourCC(span[pos..]);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(span[(pos + 4)..]);
            pos += 6;

            if (bigSize is { } big)
            {
                size = (int)big;
                bigSize = null;
            }

            if (pos + size > data.Length) yield break;

            if (type == "XXXX" && size == 4)
            {
                bigSize = BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]);
                pos += 4;
                continue;
            }

            yield return new RawSubrecord(type, data.Slice(pos, size));
            pos += size;
        }
    }

    /// <summary>
    /// Every record of the file in file order (groups are descended). Compressed bodies are
    /// inflated. <paramref name="filter"/> skips the body work for unwanted record types.
    /// </summary>
    public static IEnumerable<RawRecord> Records(ReadOnlyMemory<byte> file, Func<string, bool>? filter = null)
    {
        long pos = 0;

        while (pos + RecordHeaderSize <= file.Length)
        {
            var span = file.Span[(int)pos..];
            var type = FourCC(span);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);

            if (type == "GRUP")
            {
                // Descend: the group's children follow its header directly.
                pos += GroupHeaderSize;
                continue;
            }

            var flags = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
            var formId = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
            var bodyStart = pos + RecordHeaderSize;

            if (bodyStart + size > file.Length) yield break;

            if (filter is null || filter(type))
            {
                var body = file.Slice((int)bodyStart, (int)size);
                if ((flags & CompressedFlag) != 0) body = Inflate(body);
                yield return new RawRecord(type, flags, formId, pos, body);
            }

            pos = bodyStart + size;
        }
    }

    /// <summary>Record headers only (no body work): type, flags, FormID, offset and size.</summary>
    public static IEnumerable<(string Type, uint Flags, uint FormId, long Offset, uint Size)> RecordHeaders(ReadOnlyMemory<byte> file)
    {
        long pos = 0;
        while (pos + RecordHeaderSize <= file.Length)
        {
            var span = file.Span[(int)pos..];
            var type = FourCC(span);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);

            if (type == "GRUP")
            {
                pos += GroupHeaderSize;
                continue;
            }

            yield return (type, BinaryPrimitives.ReadUInt32LittleEndian(span[8..]), BinaryPrimitives.ReadUInt32LittleEndian(span[12..]), pos, size);
            pos += RecordHeaderSize + size;
        }
    }

    /// <summary>
    /// Highest object ID among the plugin's own records (master index == master count), so new
    /// FormIDs never collide even when the header's NextObjectID is stale.
    /// </summary>
    public static uint MaxOwnObjectId(string path, int masterCount)
    {
        var file = File.ReadAllBytes(path);
        uint max = 0;
        foreach (var (type, _, formId, _, _) in RecordHeaders(file))
        {
            if (type == "TES4") continue;
            if ((formId >> 24) != masterCount) continue;
            max = Math.Max(max, formId & 0x00FFFFFF);
        }
        return max;
    }

    /// <summary>Every object ID the plugin's own records use (for light plugins: allocate into the gaps).</summary>
    public static HashSet<uint> OwnObjectIds(string path, int masterCount)
    {
        var file = File.ReadAllBytes(path);
        var ids = new HashSet<uint>();
        foreach (var (type, _, formId, _, _) in RecordHeaders(file))
        {
            if (type == "TES4" || (formId >> 24) != masterCount) continue;
            ids.Add(formId & 0x00FFFFFF);
        }
        return ids;
    }

    public static byte[] Inflate(ReadOnlyMemory<byte> body)
    {
        var expected = BinaryPrimitives.ReadInt32LittleEndian(body.Span);
        var output = new byte[expected];

        using var input = new MemoryStream(body[4..].ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        zlib.ReadExactly(output);
        return output;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, string> FourCCs = new();

    /// <summary>Record/subrecord signature. Interned: walking a plugin sees the same few hundred.</summary>
    public static string FourCC(ReadOnlySpan<byte> span)
    {
        var key = BinaryPrimitives.ReadUInt32LittleEndian(span);
        return FourCCs.GetOrAdd(key, static k => string.Create(4, k, static (chars, v) =>
        {
            for (var i = 0; i < 4; i++) chars[i] = (char)((v >> (8 * i)) & 0xFF);
        }));
    }

    /// <summary>Null-terminated string as it is stored in headers (ASCII/ANSI file names).</summary>
    public static string ZString(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        if (end >= 0) data = data[..end];
        return TextFiles.Decode(data);
    }
}
