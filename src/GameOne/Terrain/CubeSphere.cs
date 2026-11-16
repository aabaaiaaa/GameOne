using System.Numerics;

namespace GameOne;

/// <summary>
/// The planet's directions mapped onto the six faces of a cube, with the equal-angle spacing
/// <c>Shaders.PlanetVertex</c> draws: a face coordinate in [-1, 1] is an angle (a * π/4) from the
/// face's centre, so texels cover similar areas whether near a face's middle or its corner.
/// Also latitude and longitude, with the pad at (0, 0).
/// </summary>
internal static class CubeSphere
{
    // Each face's outward direction and its across and up directions (across x up = out): the
    // same arrays, in the same order, as Shaders.PlanetVertex's.
    private static readonly Vector3[] Normals =
    [
        new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1),
    ];

    private static readonly Vector3[] Acrosses =
    [
        new(0, 0, -1), new(0, 0, 1), new(1, 0, 0), new(1, 0, 0), new(1, 0, 0), new(-1, 0, 0),
    ];

    private static readonly Vector3[] Ups =
    [
        new(0, 1, 0), new(0, 1, 0), new(0, 0, -1), new(0, 0, 1), new(0, 1, 0), new(0, 1, 0),
    ];

    /// <summary>The face a direction falls on, and where on it (equal-angle, each in [-1, 1]).</summary>
    public static (int Face, float U, float V) ToFace(Vector3 direction)
    {
        var ax = MathF.Abs(direction.X);
        var ay = MathF.Abs(direction.Y);
        var az = MathF.Abs(direction.Z);
        int face;
        if (ax >= ay && ax >= az) face = direction.X >= 0f ? 0 : 1;
        else if (ay >= az) face = direction.Y >= 0f ? 2 : 3;
        else face = direction.Z >= 0f ? 4 : 5;

        var along = Vector3.Dot(direction, Normals[face]);
        var x = Vector3.Dot(direction, Acrosses[face]) / along;
        var y = Vector3.Dot(direction, Ups[face]) / along;
        return (face, MathF.Atan(x) * 4f / MathF.PI, MathF.Atan(y) * 4f / MathF.PI);
    }

    /// <summary>The unit direction at (<paramref name="u"/>, <paramref name="v"/>) on <paramref name="face"/>; the inverse of <see cref="ToFace"/>.</summary>
    public static Vector3 FromFace(int face, float u, float v)
    {
        var x = MathF.Tan(u * MathF.PI / 4f);
        var y = MathF.Tan(v * MathF.PI / 4f);
        return Vector3.Normalize(Normals[face] + Acrosses[face] * x + Ups[face] * y);
    }

    /// <summary>Latitude and longitude of a direction, in radians: the pad is (0, 0), and east is +longitude.</summary>
    public static (float Latitude, float Longitude) LatLon(Vector3 direction) =>
        (MathF.Asin(Math.Clamp(-direction.Z, -1f, 1f)), MathF.Atan2(direction.X, direction.Y));

    /// <summary>The unit direction at a latitude and longitude (radians); the inverse of <see cref="LatLon"/>.</summary>
    public static Vector3 FromLatLon(float latitude, float longitude)
    {
        var c = MathF.Cos(latitude);
        return new Vector3(c * MathF.Sin(longitude), c * MathF.Cos(longitude), -MathF.Sin(latitude));
    }
}
