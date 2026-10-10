using System.Numerics;

namespace StbEditor.Render;

public static class TransformMath
{
    /// <summary>
    /// World matrix (row-vector, uniform scale) → reference placement in the engine's Euler
    /// convention, the inverse of <see cref="SceneBuilder.ReferenceTransform"/>.
    /// </summary>
    public static (Vector3 Position, Vector3 Rotation, float Scale) Decompose(Matrix4x4 m)
    {
        var scale = new Vector3(m.M11, m.M12, m.M13).Length();
        if (scale < 1e-6f) scale = 1f;

        // Engine rows R[i][j] = M[j][i] / s.
        float R(int i, int j) => (j, i) switch
        {
            (0, 0) => m.M11, (0, 1) => m.M12, (0, 2) => m.M13,
            (1, 0) => m.M21, (1, 1) => m.M22, (1, 2) => m.M23,
            (2, 0) => m.M31, (2, 1) => m.M32, _ => m.M33,
        } / scale;

        var sy = Math.Clamp(-R(0, 2), -1f, 1f);
        var y = MathF.Asin(sy);
        float x, z;
        if (MathF.Abs(MathF.Cos(y)) > 1e-4f)
        {
            x = MathF.Atan2(R(1, 2), R(2, 2));
            z = MathF.Atan2(R(0, 1), R(0, 0));
        }
        else
        {
            // Gimbal lock: fold the rotation into X.
            z = 0;
            x = MathF.Atan2(-R(2, 1), R(1, 1));
        }

        return (new Vector3(m.M41, m.M42, m.M43), new Vector3(Wrap(x), Wrap(y), Wrap(z)), scale);
    }

    /// <summary>Angles into [0, 2π), the way the Creation Kit writes them.</summary>
    public static float Wrap(float radians)
    {
        var r = radians % MathF.Tau;
        if (r < 0) r += MathF.Tau;
        if (r > MathF.Tau - 1e-6f) r = 0;
        return r;
    }

    public static float Degrees(float radians) => radians * 180f / MathF.PI;
    public static float Radians(float degrees) => degrees * MathF.PI / 180f;
}
