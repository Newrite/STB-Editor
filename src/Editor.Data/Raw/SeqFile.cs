using System.Buffers.Binary;

namespace StbEditor.Data.Raw;

/// <summary>
/// The SEQ file of a plugin (Data\SEQ\&lt;plugin&gt;.seq): the FormIDs of the plugin's own quests flagged
/// Start Game Enabled, as stored in the plugin. Without it the game does not start those quests (and
/// their dialogue) on a new game until a save is loaded. The CK and xEdit ("Create SEQ File") write it.
/// </summary>
public static class SeqFile
{
    private const ushort StartGameEnabled = 0x0001;

    /// <summary>Own quests with Start Game Enabled, read from the plugin file (DNAM flags).</summary>
    public static List<uint> StartGameQuests(string pluginPath)
    {
        var header = PluginHeader.Read(pluginPath);
        var own = (uint)header.Masters.Count;
        var result = new List<uint>();
        foreach (var record in RawFormat.Records(File.ReadAllBytes(pluginPath), type => type == "QUST"))
        {
            if (record.FormId >> 24 != own || (record.Flags & 0x20) != 0) continue; // own, not deleted
            var dnam = RawFormat.Subrecords(record.Data).FirstOrDefault(s => s.Type == "DNAM");
            if (dnam.Data.Length >= 2 && (BinaryPrimitives.ReadUInt16LittleEndian(dnam.Data.Span) & StartGameEnabled) != 0)
                result.Add(record.FormId);
        }
        return result;
    }

    public static string PathFor(string pluginPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pluginPath))!, "SEQ", Path.GetFileNameWithoutExtension(pluginPath) + ".seq");

    /// <summary>
    /// Writes the SEQ file next to the plugin when it has start-game quests; returns its path and the
    /// quest count, or null when there is nothing to write (an existing file is left alone).
    /// </summary>
    public static (string Path, int Count)? Write(string pluginPath)
    {
        var quests = StartGameQuests(pluginPath);
        if (quests.Count == 0) return null;
        var path = PathFor(pluginPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[quests.Count * 4];
        for (var i = 0; i < quests.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), quests[i]);
        File.WriteAllBytes(path, bytes);
        return (path, quests.Count);
    }
}
