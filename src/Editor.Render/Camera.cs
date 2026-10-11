using System.Numerics;

namespace StbEditor.Render;

/// <summary>
/// Editor camera in Skyrim world space: X east, Y north, Z up. Yaw is a compass heading
/// (0 = north, clockwise), like a reference's Z angle.
/// </summary>
public sealed class Camera
{
    public Vector3 Position { get; set; } = new(0, 0, 500);
    public float Yaw { get; set; }
    public float Pitch { get; set; } = -0.3f;
    public float FieldOfView { get; set; } = 75f * MathF.PI / 180f;
    public float Near { get; set; } = 4f;

    /// <summary>Base movement speed, units per second.</summary>
    public float Speed { get; set; } = 800f;

    public Vector3 Forward => new(
        MathF.Sin(Yaw) * MathF.Cos(Pitch),
        MathF.Cos(Yaw) * MathF.Cos(Pitch),
        MathF.Sin(Pitch));

    public Vector3 Right => new(MathF.Cos(Yaw), -MathF.Sin(Yaw), 0);
    public Vector3 Up => Vector3.Cross(Right, Forward);

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitZ);

    /// <summary>Right-handed, reversed-Z, infinite far plane: depth 1 at the near plane, 0 at infinity.</summary>
    public Matrix4x4 Projection(float aspect)
    {
        var f = 1f / MathF.Tan(FieldOfView / 2);
        return new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, 0, -1,
            0, 0, Near, 0);
    }

    public void Rotate(float deltaYaw, float deltaPitch)
    {
        Yaw = (Yaw + deltaYaw) % (MathF.PI * 2);
        Pitch = Math.Clamp(Pitch + deltaPitch, -1.55f, 1.55f);
    }

    public void LookAt(Vector3 target)
    {
        var d = target - Position;
        if (d.LengthSquared() < 1e-6f) return;
        d = Vector3.Normalize(d);
        Yaw = MathF.Atan2(d.X, d.Y);
        Pitch = MathF.Asin(Math.Clamp(d.Z, -1f, 1f));
    }

    /// <summary>Moves back from a sphere so it fills the view, keeping the current direction.</summary>
    public void Frame(Vector3 center, float radius)
    {
        radius = Math.Max(radius, 32f);
        var distance = radius / MathF.Sin(FieldOfView / 2) * 1.1f;
        Position = center - Forward * distance;
    }

    /// <summary>Orbits around a pivot by yaw/pitch deltas (Alt + drag).</summary>
    /// <remarks>
    /// The camera turns about the pivot from where it is: its offset from the pivot is rotated by the
    /// same angles as the view. (Placing it at "pivot − forward × distance" made it jump on the first
    /// frame whenever it was not already looking at the pivot — e.g. right after going through a door.)
    /// </remarks>
    public void Orbit(Vector3 pivot, float deltaYaw, float deltaPitch)
    {
        var oldPitch = Pitch;
        Rotate(deltaYaw, deltaPitch);
        var appliedPitch = Pitch - oldPitch;

        var offset = Position - pivot;
        var radius = offset.Length();
        if (radius < 1e-3f) return;
        var horizontal = MathF.Sqrt(offset.X * offset.X + offset.Y * offset.Y);
        // Heading as the camera's yaw (clockwise from +Y); elevation drops as the view tilts up.
        var heading = MathF.Atan2(offset.X, offset.Y) + deltaYaw;
        var elevation = Math.Clamp(MathF.Atan2(offset.Z, horizontal) - appliedPitch, -1.55f, 1.55f);
        var flat = radius * MathF.Cos(elevation);
        Position = pivot + new Vector3(flat * MathF.Sin(heading), flat * MathF.Cos(heading), radius * MathF.Sin(elevation));
    }

    /// <summary>World-space ray through a point of the viewport (pixels from top-left).</summary>
    public (Vector3 Origin, Vector3 Direction) Ray(Vector2 pixel, Vector2 viewport)
    {
        var ndc = new Vector2(pixel.X / viewport.X * 2 - 1, 1 - pixel.Y / viewport.Y * 2);
        var tan = MathF.Tan(FieldOfView / 2);
        var aspect = viewport.X / viewport.Y;
        var direction = Forward + Right * (ndc.X * tan * aspect) + Up * (ndc.Y * tan);
        return (Position, Vector3.Normalize(direction));
    }
}

/// <summary>Side and near planes of a view-projection; infinite far plane has none.</summary>
public readonly struct Frustum
{
    private readonly Plane[] _planes;

    public Frustum(Matrix4x4 m)
    {
        // Gribb/Hartmann on row-vector matrices: planes are column combinations.
        static Plane P(float a, float b, float c, float d) => Plane.Normalize(new Plane(a, b, c, d));
        _planes =
        [
            P(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41), // left
            P(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41), // right
            P(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42), // bottom
            P(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42), // top
            P(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43), // near (reversed Z: z <= w)
        ];
    }

    public bool Intersects(Vector3 center, float radius)
    {
        foreach (var plane in _planes)
            if (Plane.DotCoordinate(plane, center) < -radius) return false;
        return true;
    }
}
