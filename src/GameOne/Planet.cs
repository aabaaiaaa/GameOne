using System.Numerics;

namespace GameOne;

/// <summary>
/// The planet: a small sphere (see <see cref="Scene.PlanetRadius"/>) with the launch pad on top of
/// it at the origin, so near the pad "up" is +Y as before. Its gravity is 1 g at the surface,
/// far stronger than a real body this size would have, so it flies like a full-size world but an
/// orbit takes minutes rather than hours. Gravity falls off with the square of the distance from
/// its centre, as real gravity does, so orbits behave properly.
/// </summary>
/// <remarks>
/// These are the planet's own values, fixed (<see cref="GroundBody.Planet"/>'s), for what is the
/// planet's alone: its sites, rafts and sea, the launch, the solar system and its drawing. Code that
/// can run at any body reads the body it is about instead (<see cref="GroundBody"/>: a vessel's
/// <see cref="Vessel.Body"/>, a physics' <c>Body</c>), never these. It turns about its axis
/// (<see cref="Spin"/>), under orbits that don't: the physics world stays fixed to the ground. Its
/// ground is the terrain (<see cref="Terrain"/>): land and seas baked into a map, with fine detail
/// on top; and the seas, water over their beds, with waves (<see cref="Sea"/>). Every height is
/// measured from the sea (at 0, <see cref="SeaRadius"/> from the centre). The atmosphere thins
/// exponentially with height and ends at <see cref="Scene.AtmosphereHeight"/>, above which there
/// is no drag at all, so an orbit above it lasts for ever.
/// </remarks>
internal static class Planet
{
    private static readonly GroundBody Its = GroundBody.Planet;

    public static readonly Vector3 Centre = GroundBody.Planet.Centre;
    public const float Radius = GroundBody.PlanetRadius;

    /// <summary>The gravitational parameter (G times mass), set so its surface gravity is <see cref="Scene.SurfaceGravity"/>.</summary>
    public const float Mu = GroundBody.PlanetMu;

    /// <summary>The pull at the ground (<see cref="Scene.SurfaceGravity"/>), as the touchdowns' leeway takes it.</summary>
    public const float SurfaceGravity = Scene.SurfaceGravity;

    /// <summary>
    /// The spin axis, from the centre towards the north pole: it turns about this (see
    /// <see cref="Spin"/>), and the local frame is built from it ("north" is the way this points
    /// across the ground).
    /// </summary>
    public static readonly Vector3 NorthPole = GroundBody.Planet.NorthPole;

    /// <summary>How long one turn against the stars takes, in seconds (a day, noon to noon, is longer).</summary>
    public const float TurnSeconds = GroundBody.PlanetTurnSeconds;

    /// <summary>How fast it turns, in radians per second, about <see cref="NorthPole"/> (east is the way the ground moves).</summary>
    public const float SpinRate = GroundBody.PlanetSpinRate;

    /// <summary>Its angular velocity among the stars.</summary>
    public static readonly Vector3 Spin = GroundBody.Planet.Spin;

    /// <summary>The sea's radius: how far the calm sea is from the planet's centre, the zero of every height.</summary>
    public const float SeaRadius = GroundBody.PlanetSeaRadius;

    /// <inheritdoc cref="GroundBody.GroundSpin"/>
    public static Vector3 GroundSpin(Vector3 p) => Its.GroundSpin(p);

    /// <inheritdoc cref="GroundBody.AmongStars"/>
    public static Vector3 AmongStars(Vector3 p, Vector3 groundVelocity) => Its.AmongStars(p, groundVelocity);

    /// <inheritdoc cref="GroundBody.OverGround"/>
    public static Vector3 OverGround(Vector3 p, Vector3 starsVelocity) => Its.OverGround(p, starsVelocity);

    /// <inheritdoc cref="GroundBody.TurningPull"/>
    public static Vector3 TurningPull(Vector3 p, Vector3 groundVelocity) => Its.TurningPull(p, groundVelocity);

    /// <inheritdoc cref="GroundBody.OutwardPull"/>
    public static Vector3 OutwardPull(Vector3 p) => Its.OutwardPull(p);

    /// <inheritdoc cref="GroundBody.FallStep"/>
    public static Vector3 FallStep(Vector3 velocity, Vector3 pull, float drag, float seconds, bool noHalfKick = false) =>
        Its.FallStep(velocity, pull, drag, seconds, noHalfKick);

    /// <inheritdoc cref="GroundBody.Turned"/>
    public static Vector3 Turned(Vector3 p, double seconds) => Its.Turned(p, seconds);

    /// <inheritdoc cref="GroundBody.TurnedDirection"/>
    public static Vector3 TurnedDirection(Vector3 d, double seconds) => Its.TurnedDirection(d, seconds);

    /// <inheritdoc cref="GroundBody.TurnAngle"/>
    public static float TurnAngle(double seconds) => Its.TurnAngle(seconds);

    /// <summary>Straight up at <paramref name="p"/>: away from the planet's centre.</summary>
    public static Vector3 Up(Vector3 p) => Its.Up(p);

    /// <summary>East at <paramref name="p"/>, across the ground. At the pad it is +X.</summary>
    public static Vector3 East(Vector3 p) => Its.East(p);

    /// <summary>North at <paramref name="p"/>, across the ground. At the pad it is -Z.</summary>
    public static Vector3 North(Vector3 p) => Its.North(p);

    /// <inheritdoc cref="GroundBody.Bearing"/>
    public static float Bearing(Vector3 p, Vector3 direction) => Its.Bearing(p, direction);

    /// <inheritdoc cref="GroundBody.Towards"/>
    public static Vector3 Towards(Vector3 p, float bearing) => Its.Towards(p, bearing);

    /// <inheritdoc cref="GroundBody.Altitude"/>
    public static float Altitude(Vector3 p) => Its.Altitude(p);

    /// <inheritdoc cref="GroundBody.AboveAir"/>
    public static bool AboveAir(Vector3 p) => Its.AboveAir(p);

    /// <inheritdoc cref="GroundBody.Gravity"/>
    public static Vector3 Gravity(Vector3 p) => Its.Gravity(p);

    /// <inheritdoc cref="GroundBody.AirDensity"/>
    public static float AirDensity(float aboveSea) => Its.AirDensity(aboveSea);

    /// <summary>The point on the surface below the ground position (x, z), measured across the pad's plane.</summary>
    public static Vector3 SurfacePoint(float x, float z) =>
        new(x, MathF.Sqrt(Radius * Radius - x * x - z * z) - Radius, z);

    /// <inheritdoc cref="GroundBody.UprightAt"/>
    public static Quaternion UprightAt(Vector3 p) => Its.UprightAt(p);

    /// <inheritdoc cref="GroundBody.SurfaceAltitude"/>
    public static float SurfaceAltitude(Vector3 p) => Its.SurfaceAltitude(p);

    /// <inheritdoc cref="GroundBody.GroundAltitude"/>
    public static float GroundAltitude(Vector3 p) => Its.GroundAltitude(p);

    /// <inheritdoc cref="GroundBody.OnlyGroundNear"/>
    public static bool OnlyGroundNear(Vector3 p, float reach) => Its.OnlyGroundNear(p, reach);

    /// <inheritdoc cref="GroundBody.BesideASide"/>
    public static bool BesideASide(Vector3 p, float reach) => Its.BesideASide(p, reach);

    /// <inheritdoc cref="GroundBody.TerrainAltitude"/>
    public static float TerrainAltitude(Vector3 p) => Its.TerrainAltitude(p);

    /// <inheritdoc cref="GroundBody.Surface"/>
    public static (float Height, Vector3 Normal) Surface(Vector3 p) => Its.Surface(p);

    /// <inheritdoc cref="GroundBody.Ground"/>
    public static (float Height, Vector3 Normal) Ground(Vector3 p) => Its.Ground(p);

    /// <inheritdoc cref="GroundBody.GroundVelocity"/>
    public static Vector3 GroundVelocity(Vector3 p) => Its.GroundVelocity(p);

    /// <inheritdoc cref="GroundBody.Orbit"/>
    public static (float Apoapsis, float Periapsis) Orbit(Vector3 position, Vector3 velocity) => Its.Orbit(position, velocity);

    /// <inheritdoc cref="GroundBody.AngleBetween"/>
    public static float AngleBetween(Vector3 a, Vector3 b) => Its.AngleBetween(a, b);

    /// <inheritdoc cref="GroundBody.CircularSpeed"/>
    public static float CircularSpeed(float aboveSea) => Its.CircularSpeed(aboveSea);
}
