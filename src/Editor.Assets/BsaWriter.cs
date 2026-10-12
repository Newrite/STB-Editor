using System.Buffers.Binary;
using System.Text;

namespace StbEditor.Assets;

/// <summary>
/// Writes Skyrim SE archives (BSA version 105), as the Creation Kit's Create Archive: folders and
/// files sorted by the game's name hash, names included, data stored uncompressed (the game reads
/// those as well; packing textures into their own "&lt;plugin&gt; - Textures.bsa" is the caller's choice).
/// </summary>
public static class BsaWriter
{
    public const int Version = 105;

    private const uint IncludeDirectoryNames = 0x1, IncludeFileNames = 0x2, RetainDirectoryNames = 0x8,
        RetainFileNames = 0x10, RetainFileNameOffsets = 0x20, XboxArchive = 0x40, EmbedFileNames = 0x100;

    /// <summary>Content kinds in the header (meshes 1, textures 2, menus 4, sounds 8, voices 16, shaders 32, trees 64, fonts 128, misc 256).</summary>
    public static ushort FileFlags(IEnumerable<string> paths)
    {
        ushort flags = 0;
        foreach (var path in paths)
        {
            var top = path.Split('\\')[0];
            flags |= top switch
            {
                "meshes" => 1,
                "textures" => 2,
                "interface" => 4,
                "sound" when path.Contains(@"\voice\", StringComparison.Ordinal) => 16,
                "sound" => 8,
                "shaders" => 32,
                "trees" => 64,
                "fonts" => 128,
                _ => 256,
            };
        }
        return flags;
    }

    /// <summary>The game's 64-bit name hash (lower-case, backslashes; for a folder the whole path, for a file name and extension).</summary>
    public static ulong Hash(string name, bool folder)
    {
        name = name.ToLowerInvariant().Replace('/', '\\');
        string root = name, ext = "";
        if (!folder)
        {
            var dot = name.LastIndexOf('.');
            if (dot >= 0) { root = name[..dot]; ext = name[dot..]; }
        }
        if (root.Length == 0) return 0;
        uint hash1 = (uint)(root[^1] | (root.Length > 2 ? root[^2] << 8 : 0) | root.Length << 16 | root[0] << 24);
        hash1 |= ext switch { ".kf" => 0x80u, ".nif" => 0x8000u, ".dds" => 0x8080u, ".wav" => 0x80000000u, _ => 0u };
        uint hash2 = 0;
        for (var i = 1; i < root.Length - 2; i++) hash2 = unchecked(hash2 * 0x1003F + root[i]);
        uint hash3 = 0;
        foreach (var c in ext) hash3 = unchecked(hash3 * 0x1003F + c);
        return ((ulong)unchecked(hash2 + hash3) << 32) | hash1;
    }

    /// <summary>
    /// Writes <paramref name="files"/> (archive path → source file on disk) into <paramref name="output"/>.
    /// Paths are relative to Data (meshes\…, textures\…). Returns the number of files written.
    /// </summary>
    public static int Write(string output, IReadOnlyDictionary<string, string> files, IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        var encoding = Encoding.GetEncoding(1252);
        var folders = files
            .Select(kv => (Path: kv.Key.ToLowerInvariant().Replace('/', '\\'), Source: kv.Value))
            .GroupBy(f => Path.GetDirectoryName(f.Path) ?? "")
            .Select(g => (Name: g.Key, Hash: Hash(g.Key, true),
                Files: g.Select(f => (Name: Path.GetFileName(f.Path), Hash: Hash(Path.GetFileName(f.Path), false), f.Source)).OrderBy(f => f.Hash).ToList()))
            .OrderBy(f => f.Hash)
            .ToList();
        var fileCount = folders.Sum(f => f.Files.Count);
        var folderNamesLength = folders.Sum(f => f.Name.Length + 1);
        var fileNamesLength = folders.Sum(f => f.Files.Sum(x => x.Name.Length + 1));

        const int headerSize = 36, folderRecord = 24, fileRecord = 16;
        var foldersStart = headerSize;
        var fileRecordsStart = foldersStart + folders.Count * folderRecord;
        // Each folder's block: its name (length byte, name, NUL) then its file records.
        var blocksSize = folders.Sum(f => 1 + f.Name.Length + 1 + f.Files.Count * fileRecord);
        var namesStart = fileRecordsStart + blocksSize;
        var dataStart = namesStart + fileNamesLength;

        using var stream = new FileStream(output, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream, encoding);
        writer.Write(Encoding.ASCII.GetBytes("BSA\0"));
        writer.Write((uint)Version);
        writer.Write((uint)headerSize);
        writer.Write(IncludeDirectoryNames | IncludeFileNames);
        writer.Write((uint)folders.Count);
        writer.Write((uint)fileCount);
        writer.Write((uint)folderNamesLength);
        writer.Write((uint)fileNamesLength);
        writer.Write(FileFlags(files.Keys.Select(k => k.ToLowerInvariant())));
        writer.Write((ushort)0);

        // Folder records: the offset points at the folder's name in the file-record area, plus the names' total length (the format's quirk).
        var blockOffset = (long)fileRecordsStart;
        foreach (var folder in folders)
        {
            writer.Write(folder.Hash);
            writer.Write((uint)folder.Files.Count);
            writer.Write(0u);
            writer.Write((ulong)(blockOffset + fileNamesLength));
            blockOffset += 1 + folder.Name.Length + 1 + folder.Files.Count * fileRecord;
        }

        // File records with their data offsets (data stored after the names, in the same order).
        var dataOffset = (long)dataStart;
        var order = new List<string>();
        foreach (var folder in folders)
        {
            writer.Write((byte)(folder.Name.Length + 1));
            writer.Write(encoding.GetBytes(folder.Name));
            writer.Write((byte)0);
            foreach (var file in folder.Files)
            {
                var size = new FileInfo(file.Source).Length;
                if (size > 0x3FFFFFFF) throw new InvalidOperationException($"{file.Source}: too big for an archive");
                writer.Write(file.Hash);
                writer.Write((uint)size);
                if (dataOffset > uint.MaxValue) throw new InvalidOperationException("archive over 4 GB: split it");
                writer.Write((uint)dataOffset);
                dataOffset += size;
                order.Add(file.Source);
            }
        }
        foreach (var folder in folders)
            foreach (var file in folder.Files)
            {
                writer.Write(encoding.GetBytes(file.Name));
                writer.Write((byte)0);
            }

        var done = 0;
        foreach (var source in order)
        {
            cancel.ThrowIfCancellationRequested();
            using var input = File.OpenRead(source);
            input.CopyTo(stream);
            if (++done % 200 == 0) progress?.Report($"{done}/{order.Count}");
        }
        return order.Count;
    }
}
