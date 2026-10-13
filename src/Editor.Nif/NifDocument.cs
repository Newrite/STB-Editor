using System.Text;

namespace StbEditor.Nif;

/// <summary>
/// A Skyrim NIF (20.2.0.7) as its header and raw blocks, for building files out of other files' blocks (FaceGen heads).
/// Blocks are kept as bytes; whoever edits one knows its layout.
/// </summary>
public sealed class NifDocument
{
    public uint UserVersion { get; set; } = 12;
    public uint BsVersion { get; set; } = 100;
    public string Author { get; set; } = "";
    public string ProcessScript { get; set; } = "";
    public string ExportScript { get; set; } = "";
    public List<string> Strings { get; } = [];
    public List<(string Type, byte[] Data)> Blocks { get; } = [];
    public List<int> Roots { get; } = [];

    private const string Signature = "Gamebryo File Format, Version 20.2.0.7";
    private const uint Version = 0x14020007;

    public static NifDocument Read(byte[] data)
    {
        var newline = Array.IndexOf(data, (byte)'\n', 0, Math.Min(data.Length, 128));
        if (newline < 0 || !Encoding.ASCII.GetString(data, 0, newline).StartsWith("Gamebryo File Format", StringComparison.Ordinal))
            throw new InvalidDataException("not a Skyrim NIF");
        var r = new BinaryReader(new MemoryStream(data, newline + 1, data.Length - newline - 1));
        if (r.ReadUInt32() != Version) throw new NotSupportedException("only 20.2.0.7 NIFs");
        if (r.ReadByte() != 1) throw new NotSupportedException("big-endian NIF");
        var doc = new NifDocument { UserVersion = r.ReadUInt32() };
        var blockCount = r.ReadInt32();
        doc.BsVersion = r.ReadUInt32();
        doc.Author = ShortString(r);
        if (doc.BsVersion > 130) r.ReadUInt32();
        doc.ProcessScript = ShortString(r);
        doc.ExportScript = ShortString(r);
        if (doc.BsVersion >= 103) ShortString(r);
        var types = new string[r.ReadUInt16()];
        for (var i = 0; i < types.Length; i++) types[i] = Encoding.ASCII.GetString(r.ReadBytes(r.ReadInt32()));
        var typeIndex = new ushort[blockCount];
        for (var i = 0; i < blockCount; i++) typeIndex[i] = r.ReadUInt16();
        var sizes = new uint[blockCount];
        for (var i = 0; i < blockCount; i++) sizes[i] = r.ReadUInt32();
        var stringCount = r.ReadInt32();
        r.ReadUInt32();
        for (var i = 0; i < stringCount; i++) doc.Strings.Add(Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32())));
        var groups = r.ReadInt32();
        r.ReadBytes(groups * 4);
        for (var i = 0; i < blockCount; i++) doc.Blocks.Add((types[typeIndex[i] & 0x7FFF], r.ReadBytes((int)sizes[i])));
        if (r.BaseStream.Position + 4 <= r.BaseStream.Length)
        {
            var roots = r.ReadInt32();
            for (var i = 0; i < roots && r.BaseStream.Position + 4 <= r.BaseStream.Length; i++) doc.Roots.Add(r.ReadInt32());
        }
        return doc;
    }

    public byte[] Write()
    {
        using var output = new MemoryStream();
        var w = new BinaryWriter(output);
        w.Write(Encoding.ASCII.GetBytes(Signature + "\n"));
        w.Write(Version);
        w.Write((byte)1);
        w.Write(UserVersion);
        w.Write(Blocks.Count);
        w.Write(BsVersion);
        WriteShortString(w, Author);
        WriteShortString(w, ProcessScript);
        WriteShortString(w, ExportScript);
        var types = Blocks.Select(b => b.Type).Distinct().ToList();
        w.Write((ushort)types.Count);
        foreach (var type in types) WriteSized(w, type);
        foreach (var block in Blocks) w.Write((ushort)types.IndexOf(block.Type));
        foreach (var block in Blocks) w.Write((uint)block.Data.Length);
        w.Write(Strings.Count);
        w.Write((uint)(Strings.Count == 0 ? 0 : Strings.Max(s => Encoding.UTF8.GetByteCount(s))));
        foreach (var s in Strings) WriteSized(w, s);
        w.Write(0); // groups
        foreach (var block in Blocks) w.Write(block.Data);
        w.Write(Roots.Count);
        foreach (var root in Roots) w.Write(root);
        return output.ToArray();
    }

    /// <summary>Index of a header string, added if missing; null or empty gives -1 (no string).</summary>
    public int String(string? text)
    {
        if (string.IsNullOrEmpty(text)) return -1;
        var index = Strings.IndexOf(text);
        if (index >= 0) return index;
        Strings.Add(text);
        return Strings.Count - 1;
    }

    public string? StringAt(int index) => index >= 0 && index < Strings.Count ? Strings[index] : null;

    public int Add(string type, byte[] data)
    {
        Blocks.Add((type, data));
        return Blocks.Count - 1;
    }

    private static string ShortString(BinaryReader r)
    {
        var length = r.ReadByte();
        return Encoding.ASCII.GetString(r.ReadBytes(length)).TrimEnd('\0');
    }

    private static void WriteShortString(BinaryWriter w, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        w.Write((byte)(bytes.Length + 1));
        w.Write(bytes);
        w.Write((byte)0);
    }

    private static void WriteSized(BinaryWriter w, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        w.Write(bytes.Length);
        w.Write(bytes);
    }
}
