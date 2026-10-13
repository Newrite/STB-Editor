using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace StbEditor.Nif;

/// <summary>Little-endian cursor over one NIF block.</summary>
internal ref struct BlockReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public BlockReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public int Position => _pos;
    public int Remaining => _data.Length - _pos;

    public byte Byte() => _data[_pos++];
    public bool Bool() => _data[_pos++] != 0;

    public ushort UShort()
    {
        var v = BinaryPrimitives.ReadUInt16LittleEndian(_data[_pos..]);
        _pos += 2;
        return v;
    }

    public short Short() => (short)UShort();

    public uint UInt()
    {
        var v = BinaryPrimitives.ReadUInt32LittleEndian(_data[_pos..]);
        _pos += 4;
        return v;
    }

    public int Int() => (int)UInt();

    public ulong ULong()
    {
        var v = BinaryPrimitives.ReadUInt64LittleEndian(_data[_pos..]);
        _pos += 8;
        return v;
    }

    public float Float()
    {
        var v = BinaryPrimitives.ReadSingleLittleEndian(_data[_pos..]);
        _pos += 4;
        return v;
    }

    public float Half()
    {
        var v = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(_data[_pos..]));
        _pos += 2;
        return v;
    }

    public Vector2 Vec2() => new(Float(), Float());
    public Vector3 Vec3() => new(Float(), Float(), Float());
    public Vector4 Vec4() => new(Float(), Float(), Float(), Float());

    /// <summary>NIF Matrix33: rows as stored, applied to column vectors (v' = R·v).</summary>
    public Matrix3 Matrix33() => new(Vec3(), Vec3(), Vec3());

    public void Skip(int count) => _pos += count;

    public string SizedString()
    {
        var length = Int();
        if (length < 0 || length > Remaining) throw new InvalidDataException("bad string length");
        var text = Encoding.Latin1.GetString(_data.Slice(_pos, length));
        _pos += length;
        return text.TrimEnd('\0');
    }

    public int[] Refs()
    {
        var count = Int();
        if (count < 0 || count * 4 > Remaining) throw new InvalidDataException("bad ref count");
        var refs = new int[count];
        for (var i = 0; i < count; i++) refs[i] = Int();
        return refs;
    }
}

/// <summary>3x3 rotation in NIF convention (rows, column-vector multiplication).</summary>
public readonly record struct Matrix3(Vector3 Row0, Vector3 Row1, Vector3 Row2)
{
    public static readonly Matrix3 Identity = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

    public Vector3 Transform(Vector3 v) => new(Vector3.Dot(Row0, v), Vector3.Dot(Row1, v), Vector3.Dot(Row2, v));

    public static Matrix3 operator *(Matrix3 a, Matrix3 b)
    {
        Vector3 Col(int i) => i switch
        {
            0 => new Vector3(b.Row0.X, b.Row1.X, b.Row2.X),
            1 => new Vector3(b.Row0.Y, b.Row1.Y, b.Row2.Y),
            _ => new Vector3(b.Row0.Z, b.Row1.Z, b.Row2.Z),
        };

        Vector3 Row(Vector3 r) => new(Vector3.Dot(r, Col(0)), Vector3.Dot(r, Col(1)), Vector3.Dot(r, Col(2)));
        return new Matrix3(Row(a.Row0), Row(a.Row1), Row(a.Row2));
    }
}

/// <summary>Rotation + translation + uniform scale, as NIF nodes and Skyrim references use.</summary>
public readonly record struct NifTransform(Matrix3 Rotation, Vector3 Translation, float Scale)
{
    public static readonly NifTransform Identity = new(Matrix3.Identity, Vector3.Zero, 1f);

    public Vector3 Apply(Vector3 v) => Rotation.Transform(v * Scale) + Translation;

    /// <summary>parent ∘ child: the child's local transform expressed in the parent's space.</summary>
    public NifTransform Then(NifTransform child) => new(
        Rotation * child.Rotation,
        Translation + Rotation.Transform(child.Translation * Scale),
        Scale * child.Scale);

    /// <summary>The transform that undoes this one (rotations are orthonormal).</summary>
    public NifTransform Inverse()
    {
        var r = new Matrix3(new(Rotation.Row0.X, Rotation.Row1.X, Rotation.Row2.X), new(Rotation.Row0.Y, Rotation.Row1.Y, Rotation.Row2.Y), new(Rotation.Row0.Z, Rotation.Row1.Z, Rotation.Row2.Z));
        var s = Scale == 0 ? 1 : 1 / Scale;
        return new NifTransform(r, -r.Transform(Translation) * s, s);
    }

    /// <summary>Row-vector matrix for System.Numerics / HLSL row_major (v' = v·M).</summary>
    public Matrix4x4 ToMatrix()
    {
        var r = Rotation;
        var s = Scale;
        return new Matrix4x4(
            r.Row0.X * s, r.Row1.X * s, r.Row2.X * s, 0,
            r.Row0.Y * s, r.Row1.Y * s, r.Row2.Y * s, 0,
            r.Row0.Z * s, r.Row1.Z * s, r.Row2.Z * s, 0,
            Translation.X, Translation.Y, Translation.Z, 1);
    }
}
