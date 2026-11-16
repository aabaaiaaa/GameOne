using System.Numerics;

namespace GameOne;

/// <summary>
/// A vector in double: for places and motions worked out across the bodies' frames (tens of
/// kilometres apart, where a float rounds to millimetres), made a float only where it is used
/// near its own frame's origin (the physics, the renderer).
/// </summary>
internal readonly record struct Vector3Double(double X, double Y, double Z)
{
    public static Vector3Double Zero => default;

    public double LengthSquared() => X * X + Y * Y + Z * Z;
    public double Length() => Math.Sqrt(LengthSquared());
    public static double Dot(Vector3Double a, Vector3Double b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vector3Double Cross(Vector3Double a, Vector3Double b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static Vector3Double Normalize(Vector3Double a) => a / a.Length();

    public static Vector3Double operator +(Vector3Double a, Vector3Double b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3Double operator -(Vector3Double a, Vector3Double b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3Double operator -(Vector3Double a) => new(-a.X, -a.Y, -a.Z);
    public static Vector3Double operator *(Vector3Double a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vector3Double operator /(Vector3Double a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    /// <summary>A float vector, exactly.</summary>
    public static implicit operator Vector3Double(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>As a float vector, rounded.</summary>
    public Vector3 ToSingle() => new((float)X, (float)Y, (float)Z);
}

/// <summary>
/// A rotation in double, as <see cref="Quaternion"/> has it (a Hamilton product, <c>a * b</c>
/// turning by <c>b</c> then <c>a</c>; <see cref="Concatenate"/> the other way round, as
/// <see cref="Quaternion.Concatenate"/>), so a rotation worked out of angles grown over a long
/// time, and composed and undone, keeps well within a float's step at the bodies' distances.
/// </summary>
internal readonly record struct QuaternionDouble(double X, double Y, double Z, double W)
{
    public static QuaternionDouble Identity => new(0, 0, 0, 1);

    /// <summary>The rotation by <paramref name="angle"/> radians about the unit <paramref name="axis"/>.</summary>
    public static QuaternionDouble FromAxisAngle(Vector3Double axis, double angle)
    {
        var (s, c) = Math.SinCos(angle / 2);
        return new(axis.X * s, axis.Y * s, axis.Z * s, c);
    }

    /// <summary>A float rotation, made exactly unit in double.</summary>
    public static QuaternionDouble From(Quaternion q) => Normalize(new(q.X, q.Y, q.Z, q.W));

    public static QuaternionDouble Normalize(QuaternionDouble q)
    {
        var n = Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        return new(q.X / n, q.Y / n, q.Z / n, q.W / n);
    }

    /// <summary>The rotation back (its inverse, as it is unit).</summary>
    public QuaternionDouble Conjugate => new(-X, -Y, -Z, W);

    public static QuaternionDouble operator *(QuaternionDouble a, QuaternionDouble b) => new(
        a.X * b.W + b.X * a.W + (a.Y * b.Z - a.Z * b.Y),
        a.Y * b.W + b.Y * a.W + (a.Z * b.X - a.X * b.Z),
        a.Z * b.W + b.Z * a.W + (a.X * b.Y - a.Y * b.X),
        a.W * b.W - (a.X * b.X + a.Y * b.Y + a.Z * b.Z));

    /// <summary>Turning by <paramref name="first"/>, then by <paramref name="then"/>.</summary>
    public static QuaternionDouble Concatenate(QuaternionDouble first, QuaternionDouble then) => then * first;

    /// <summary><paramref name="v"/> turned by this rotation.</summary>
    public Vector3Double Rotate(Vector3Double v)
    {
        // v + w t + q × t, with t = 2 q × v.
        var (tx, ty, tz) = (2 * (Y * v.Z - Z * v.Y), 2 * (Z * v.X - X * v.Z), 2 * (X * v.Y - Y * v.X));
        return new(v.X + W * tx + (Y * tz - Z * ty), v.Y + W * ty + (Z * tx - X * tz), v.Z + W * tz + (X * ty - Y * tx));
    }

    /// <summary>As a float rotation, made unit.</summary>
    public Quaternion ToSingle() => Quaternion.Normalize(new((float)X, (float)Y, (float)Z, (float)W));
}
