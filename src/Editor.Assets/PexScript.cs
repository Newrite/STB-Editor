using System.Buffers.Binary;
using System.Text;

namespace StbEditor.Assets;

/// <summary>A Papyrus property as compiled: what the Creation Kit shows in the script properties dialog.</summary>
public sealed record PexProperty(string Name, string Type, string Documentation, bool IsAuto, bool IsConditional, bool IsHidden, bool IsWritable = true)
{
    public bool IsArray => Type.EndsWith("[]", StringComparison.Ordinal);
    public string ElementType => IsArray ? Type[..^2] : Type;

    /// <summary>Int, Float, Bool, String; everything else is an object type (a form or a script).</summary>
    public bool IsObject => ElementType.ToLowerInvariant() is not ("int" or "float" or "bool" or "string");
}

/// <summary>
/// Reads compiled Papyrus scripts (.pex, Skyrim: big-endian, version 3.x) far enough for the
/// properties list: the object's name, parent script and its properties. Functions are walked
/// only to step over them (property getters/setters sit between properties).
/// </summary>
public sealed class PexScript
{
    public required string Name { get; init; }
    public string? Parent { get; init; }
    public string Documentation { get; init; } = string.Empty;
    public required IReadOnlyList<PexProperty> Properties { get; init; }

    private const uint Magic = 0xFA57C0DE;

    /// <summary>Instruction argument counts by opcode; -n means n fixed arguments followed by a counted vararg list.</summary>
    private static readonly int[] OpcodeArgs =
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, // nop iadd fadd isub fsub imul fmul idiv fdiv imod
        2, 2, 2, 2, 2,                // not ineg fneg assign cast
        3, 3, 3, 3, 3,                // cmp_eq lt le gt ge
        1, 2, 2,                      // jmp jmpt jmpf
        -3, -2, -3,                   // callmethod callparent callstatic
        1, 3, 3, 3,                   // return strcat propget propset
        2, 2, 3, 3, 4, 4,             // array_create length getelement setelement findelement rfindelement
    ];

    public static PexScript Read(byte[] data)
    {
        var r = new Reader(data);
        if (r.U32() != Magic) throw new InvalidDataException("not a compiled Papyrus script");
        r.U8(); // major
        r.U8(); // minor
        r.U16(); // game id
        r.U64(); // compile time
        r.WString(); // source
        r.WString(); // user
        r.WString(); // machine

        var strings = new string[r.U16()];
        for (var i = 0; i < strings.Length; i++) strings[i] = r.WString();
        string S(ushort index) => index < strings.Length ? strings[index] : string.Empty;

        if (r.U8() != 0)
        {
            r.U64();
            var functions = r.U16();
            for (var i = 0; i < functions; i++)
            {
                r.U16(); r.U16(); r.U16(); r.U8();
                var lines = r.U16();
                r.Skip(lines * 2);
            }
        }

        var userFlags = r.U16();
        r.Skip(userFlags * 3);

        var objects = r.U16();
        if (objects == 0) throw new InvalidDataException("script has no object");

        var name = S(r.U16());
        r.U32(); // object size
        var parent = S(r.U16());
        var documentation = S(r.U16());
        r.U32(); // user flags
        r.U16(); // auto state

        var variables = r.U16();
        for (var i = 0; i < variables; i++)
        {
            r.U16(); r.U16(); r.U32();
            SkipValue(ref r);
        }

        var properties = new List<PexProperty>();
        var count = r.U16();
        for (var i = 0; i < count; i++)
        {
            var propertyName = S(r.U16());
            var type = S(r.U16());
            var doc = S(r.U16());
            var flags = r.U32(); // user flags: 1 hidden, 2 conditional
            var kind = r.U8();   // 1 read, 2 write, 4 auto
            if ((kind & 4) != 0)
            {
                r.U16(); // auto variable
            }
            else
            {
                if ((kind & 1) != 0) SkipFunction(ref r);
                if ((kind & 2) != 0) SkipFunction(ref r);
            }
            properties.Add(new PexProperty(propertyName, type, doc, (kind & 4) != 0, (flags & 2) != 0, (flags & 1) != 0, (kind & 6) != 0));
        }

        return new PexScript { Name = name, Parent = parent.Length > 0 ? parent : null, Documentation = documentation, Properties = properties };
    }

    private static void SkipValue(ref Reader r)
    {
        switch (r.U8())
        {
            case 0: break;
            case 1:
            case 2: r.U16(); break;
            case 3:
            case 4: r.U32(); break;
            case 5: r.U8(); break;
            default: throw new InvalidDataException("bad variable data");
        }
    }

    private static int IntValue(ref Reader r)
    {
        var type = r.U8();
        if (type != 3) throw new InvalidDataException("vararg count is not an int");
        return (int)r.U32();
    }

    private static void SkipFunction(ref Reader r)
    {
        r.U16(); // return type
        r.U16(); // doc
        r.U32(); // user flags
        r.U8();  // flags
        var parameters = r.U16();
        r.Skip(parameters * 4);
        var locals = r.U16();
        r.Skip(locals * 4);
        var instructions = r.U16();
        for (var i = 0; i < instructions; i++)
        {
            var opcode = r.U8();
            if (opcode >= OpcodeArgs.Length) throw new InvalidDataException($"unknown opcode {opcode}");
            var args = OpcodeArgs[opcode];
            var fixedArgs = Math.Abs(args);
            for (var a = 0; a < fixedArgs; a++) SkipValue(ref r);
            if (args < 0)
            {
                var extra = IntValue(ref r);
                for (var a = 0; a < extra; a++) SkipValue(ref r);
            }
        }
    }

    private ref struct Reader(byte[] data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;

        public byte U8() => _data[_pos++];

        public ushort U16()
        {
            var v = BinaryPrimitives.ReadUInt16BigEndian(_data[_pos..]);
            _pos += 2;
            return v;
        }

        public uint U32()
        {
            var v = BinaryPrimitives.ReadUInt32BigEndian(_data[_pos..]);
            _pos += 4;
            return v;
        }

        public ulong U64()
        {
            var v = BinaryPrimitives.ReadUInt64BigEndian(_data[_pos..]);
            _pos += 8;
            return v;
        }

        public string WString()
        {
            var length = U16();
            var text = Encoding.UTF8.GetString(_data.Slice(_pos, length));
            _pos += length;
            return text;
        }

        public void Skip(int count) => _pos += count;
    }
}

/// <summary>Scripts of the load order (loose + BSA) by name, read on demand, with inherited properties.</summary>
public sealed class ScriptLibrary(AssetFileSystem assets)
{
    private readonly Dictionary<string, PexScript?> _scripts = new(StringComparer.OrdinalIgnoreCase);
    private List<string>? _names;

    /// <summary>Every script name available (scripts\*.pex).</summary>
    public IReadOnlyList<string> Names => _names ??= assets.Enumerate(@"scripts\", ".pex")
        .Where(p => p.Count(c => c == '\\') == 1)
        .Select(p => Path.GetFileNameWithoutExtension(p))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>A script was (re)compiled: read it again next time, and list it.</summary>
    public void Forget(string name)
    {
        _scripts.Remove(name);
        _names = null;
    }

    public PexScript? Get(string name)
    {
        if (_scripts.TryGetValue(name, out var cached)) return cached;
        PexScript? script = null;
        try
        {
            if (assets.ReadAllBytes($@"scripts\{name}.pex") is { } bytes) script = PexScript.Read(bytes);
        }
        catch
        {
            // Unreadable or missing: shown as "script not found" by the editor.
        }
        _scripts[name] = script;
        return script;
    }

    /// <summary>
    /// Properties that can be filled in, as the CK lists them: auto or writable, not hidden, from the
    /// script and its parents (read-only ones like ObjectReference.X are left out).
    /// </summary>
    public List<(PexProperty Property, string Owner)> AllProperties(string name)
    {
        var result = new List<(PexProperty, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var current = Get(name); current is not null && seen.Add(current.Name); current = current.Parent is { } p ? Get(p) : null)
            foreach (var property in current.Properties.Where(p => p.IsWritable && !p.IsHidden))
                if (!result.Any(x => string.Equals(((PexProperty)x.Item1).Name, property.Name, StringComparison.OrdinalIgnoreCase)))
                    result.Add((property, current.Name));
        return result;
    }
}
