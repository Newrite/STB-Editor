using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda.Plugins;

namespace StbEditor.Data.Catalog;

/// <summary>
/// Per-plugin catalog on disk, keyed by file path, size and write time. Re-reading 300 plugins
/// through Mutagen takes tens of seconds; a warm cache loads in about one.
/// </summary>
internal static class CatalogCache
{
    private const uint Magic = 0x43425453; // "STBC"
    private const int Version = 3; // 3: names of localized masters loaded from mod folders (cleaned DLCs)

    public static string PathFor(string folder, PluginInfo plugin)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(plugin.Location.Path.ToLowerInvariant())))[..12];
        return Path.Combine(folder, $"{plugin.Name}.{hash}.cat");
    }

    public static List<CatalogEntry>? TryRead(string path, PluginInfo plugin)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var info = new FileInfo(plugin.Location.Path);
            using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);

            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version) return null;
            if (reader.ReadInt64() != info.Length || reader.ReadInt64() != info.LastWriteTimeUtc.Ticks) return null;
            if (reader.ReadInt32() != (int)plugin.StringEncoding) return null;

            var mods = new ModKey[reader.ReadInt32()];
            for (var i = 0; i < mods.Length; i++) mods[i] = ModKey.FromFileName(reader.ReadString());

            var count = reader.ReadInt32();
            var entries = new List<CatalogEntry>(count);
            var signatures = new Dictionary<uint, string>();

            for (var i = 0; i < count; i++)
            {
                var key = new FormKey(mods[reader.ReadUInt16()], reader.ReadUInt32());
                var sigRaw = reader.ReadUInt32();
                if (!signatures.TryGetValue(sigRaw, out var sig))
                {
                    sig = Encoding.ASCII.GetString(BitConverter.GetBytes(sigRaw));
                    signatures[sigRaw] = sig;
                }

                var flags = reader.ReadUInt32();
                var editorId = ReadNullable(reader);
                var name = ReadNullable(reader);

                CellPlacement? cell = null;
                var cellKind = reader.ReadByte();
                if (cellKind != 0)
                {
                    FormKey? world = null;
                    var worldMod = reader.ReadUInt16();
                    var worldId = reader.ReadUInt32();
                    if (worldMod != ushort.MaxValue) world = new FormKey(mods[worldMod], worldId);
                    cell = new CellPlacement(world, reader.ReadInt16(), reader.ReadInt16(), cellKind == 1, cellKind == 3);
                }

                entries.Add(new CatalogEntry(key, sig, flags, editorId, name, cell));
            }

            return entries;
        }
        catch
        {
            return null;
        }
    }

    public static void Write(string path, PluginInfo plugin, List<CatalogEntry> entries)
    {
        var info = new FileInfo(plugin.Location.Path);
        var mods = new Dictionary<ModKey, ushort>();

        ushort ModIndex(ModKey key)
        {
            if (!mods.TryGetValue(key, out var index))
            {
                index = (ushort)mods.Count;
                mods[key] = index;
            }
            return index;
        }

        foreach (var entry in entries)
        {
            ModIndex(entry.FormKey.ModKey);
            if (entry.Cell?.Worldspace is { } world) ModIndex(world.ModKey);
        }

        var temp = path + ".tmp";
        using (var writer = new BinaryWriter(File.Create(temp), Encoding.UTF8))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(info.Length);
            writer.Write(info.LastWriteTimeUtc.Ticks);
            writer.Write((int)plugin.StringEncoding);

            writer.Write(mods.Count);
            foreach (var (key, _) in mods.OrderBy(m => m.Value)) writer.Write(key.FileName.String);

            writer.Write(entries.Count);
            foreach (var entry in entries)
            {
                writer.Write(mods[entry.FormKey.ModKey]);
                writer.Write(entry.FormKey.ID);
                writer.Write(BitConverter.ToUInt32(Encoding.ASCII.GetBytes(entry.Signature)));
                writer.Write(entry.Flags);
                WriteNullable(writer, entry.EditorId);
                WriteNullable(writer, entry.Name);

                if (entry.Cell is not { } cell)
                {
                    writer.Write((byte)0);
                    continue;
                }

                writer.Write((byte)(cell.IsInterior ? 1 : cell.IsPersistentCell ? 3 : 2));
                writer.Write(cell.Worldspace is { } w ? mods[w.ModKey] : ushort.MaxValue);
                writer.Write(cell.Worldspace?.ID ?? 0);
                writer.Write(cell.GridX);
                writer.Write(cell.GridY);
            }
        }

        File.Move(temp, path, overwrite: true);
    }

    private static string? ReadNullable(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;

    private static void WriteNullable(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null) writer.Write(value);
    }
}
