using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

namespace GameOne.Tests;

/// <summary>
/// A very low round orbit of a body with no air, found from its height map: the circle whose
/// track over a given number of laps crosses the lowest highest ground, flown
/// <see cref="Clearance"/> over that. The body turns under the orbit (the moon some 56° a lap at
/// this height), so the ground under the orbit's point <c>t</c> seconds on is that point's
/// direction among the stars turned back by <c>t</c> of the body's turn
/// (<see cref="GroundBody.TurnedDirection"/>). Each circle starts at its ascending node (where it
/// crosses the body's equator going north; for one round the equator, out along +X), so a normal
/// alone names one track.
/// </summary>
/// <remarks>
/// The search: every orbit normal on a Fibonacci sphere of <see cref="Normals"/> (so prograde and
/// retrograde alike), each track read every <see cref="ReadDegrees"/>° of the orbit, then twice
/// refined round the best on a grid at a quarter of the spacing before. How fast the circle goes
/// round, and so where its track falls on the turning ground, follows from its radius, which
/// follows from the highest ground it crosses: so the search is run again at the found radius
/// (<see cref="SearchRounds"/> times at most), then that radius raised until the found track,
/// read every <see cref="FineDegrees"/>°, clears its highest ground by the clearance.
/// </remarks>
internal static class LowPass
{
    /// <summary>How far over the highest ground its track crosses the circle is flown, in metres (its centre of mass).</summary>
    public const float Clearance = 30f;

    /// <summary>The orbit normals tried first, on a Fibonacci sphere.</summary>
    public const int Normals = 2000;

    /// <summary>How often each track is read in the search, in degrees of the orbit.</summary>
    public const double ReadDegrees = 1.0;

    /// <summary>How often the found track is read for its highest ground, in degrees of the orbit.</summary>
    public const double FineDegrees = 0.05;

    /// <summary>The searches round the best normal, each at a quarter of the spacing before.</summary>
    private const int Refinements = 2;

    /// <summary>The grid's reach round the best normal in each refinement, in its own steps either way: so it covers the spacing before.</summary>
    private const int GridReach = 4;

    /// <summary>The most times the search is run again at the radius it found.</summary>
    private const int SearchRounds = 3;

    private sealed record Found(Vector3 Normal, float Highest, float Radius, double LapSeconds, TimeSpan Took);

    // A search's result, by body and laps: the search takes a second or so, and every test of the
    // low pass (and the performance test) asks for the same few.
    private static readonly ConcurrentDictionary<(string Body, int Laps), Lazy<Found>> Searched = new();

    /// <summary>
    /// The round orbit of <paramref name="body"/> whose track over <paramref name="laps"/> laps
    /// crosses the lowest highest ground: its normal, that highest ground (above the body's datum,
    /// read every <see cref="FineDegrees"/>°), its radius (<c>SeaRadius + Highest + Clearance</c>)
    /// and how long a lap takes. Started at world time <paramref name="worldTime"/>: a body turns
    /// evenly in its own frame, so the track is the same whenever it starts.
    /// </summary>
    public static (Vector3 Normal, float Highest, float Radius, double LapSeconds) Find(GroundBody body, double worldTime, int laps)
    {
        var found = Search(body, laps);
        return (found.Normal, found.Highest, found.Radius, found.LapSeconds);
    }

    /// <summary>How long <see cref="Find"/>'s search over <paramref name="laps"/> laps of <paramref name="body"/> took (once: it is kept).</summary>
    public static TimeSpan SearchTime(GroundBody body, int laps) => Search(body, laps).Took;

    /// <summary>
    /// The lander alone (<see cref="LanderTests.Alone"/>), numbered <paramref name="id"/>, full,
    /// legs in, engine off, on <see cref="Find"/>'s circle over <paramref name="laps"/> laps, its
    /// centre of mass on it and its nose along the way it goes among the stars: at its start, or
    /// with <paramref name="secondsOn"/>, where it would be that many seconds on (so on the same
    /// track over the ground, that far ahead along it).
    /// </summary>
    public static VesselState Start(int id, GroundBody body, double worldTime, int laps, double secondsOn = 0)
    {
        var (normal, _, radius, _) = Find(body, worldTime, laps);
        var direction = Along(body, normal, radius, secondsOn);
        var stars = Vector3.Cross(normal, direction) * MathF.Sqrt(body.Mu / radius);
        // Among the stars matching the ground at the start, then turned to the ground it is over then.
        var centre = body.Centre + body.TurnedDirection(direction, -secondsOn) * radius;
        var way = body.TurnedDirection(stars, -secondsOn);
        return CrossingTests.Craft(id, body, centre, body.OverGround(centre, way), way, first: 0, last: 0, fuel: 1f, vehicle: LanderTests.Alone);
    }

    /// <summary>
    /// The ground under the circle of <paramref name="normal"/> and <paramref name="radius"/>
    /// round <paramref name="body"/>, <paramref name="degrees"/> of the orbit on from its start: a
    /// direction from the body's centre in its frame (fixed to its ground) as it was at the start.
    /// </summary>
    public static Vector3 Under(GroundBody body, Vector3 normal, float radius, double degrees)
    {
        var seconds = double.DegreesToRadians(degrees) / Rate(body, radius);
        return body.TurnedDirection(Along(body, normal, radius, seconds), -seconds);
    }

    /// <summary>The highest ground under the circle over <paramref name="laps"/> laps, read every <paramref name="stepDegrees"/>° of the orbit.</summary>
    public static float Highest(GroundBody body, Vector3 normal, float radius, int laps, double stepDegrees) =>
        HighestAt(body, normal, radius, laps, stepDegrees).Height;

    /// <summary>
    /// <see cref="Highest"/>, and how far on from the circle's start it is, in degrees of the
    /// orbit (the first, if more than one read is as high).
    /// </summary>
    public static (float Height, double Degrees) HighestAt(GroundBody body, Vector3 normal, float radius, int laps, double stepDegrees)
    {
        var reads = (int)Math.Round(laps * 360.0 / stepDegrees);
        var (highest, at) = (float.NegativeInfinity, 0.0);
        for (var k = 0; k <= reads; k++)
        {
            var height = body.Height(Under(body, normal, radius, k * stepDegrees));
            if (height > highest)
                (highest, at) = (height, k * stepDegrees);
        }
        return (highest, at);
    }

    /// <summary>How long the circle of <paramref name="radius"/> takes to go <paramref name="degrees"/> round <paramref name="body"/>, in seconds.</summary>
    public static double SecondsFor(GroundBody body, float radius, double degrees) => double.DegreesToRadians(degrees) / Rate(body, radius);

    /// <summary>
    /// The search's first look alone (no refinement): the normal on the Fibonacci sphere whose
    /// circle of <paramref name="radius"/> crosses the lowest highest ground over
    /// <paramref name="laps"/> laps, read every <see cref="ReadDegrees"/>°, and that ground.
    /// </summary>
    public static (Vector3 Normal, float Highest) Coarse(GroundBody body, float radius, int laps)
    {
        var best = Lowest(body, radius, laps, Sphere());
        return (best, Highest(body, best, radius, laps, ReadDegrees));
    }

    /// <summary>How fast the circle of <paramref name="radius"/> goes round <paramref name="body"/>, in radians a second.</summary>
    private static double Rate(GroundBody body, float radius) => Math.Sqrt(body.Mu / Math.Pow(radius, 3));

    /// <summary>Where on the circle, among the stars matching the ground at its start, it is <paramref name="seconds"/> on: a unit direction.</summary>
    private static Vector3 Along(GroundBody body, Vector3 normal, float radius, double seconds)
    {
        var angle = Rate(body, radius) * seconds;
        angle -= Math.Floor(angle / Math.Tau) * Math.Tau; // kept small, so the float turn keeps its precision
        return Vector3.Transform(Node(body, normal), Quaternion.CreateFromAxisAngle(normal, (float)angle));
    }

    /// <summary>The circle's start: its ascending node (square to the body's north and the normal), or for one round the equator, out along +X.</summary>
    private static Vector3 Node(GroundBody body, Vector3 normal)
    {
        var node = Vector3.Cross(body.NorthPole, normal);
        if (node.LengthSquared() < 1e-8f)
            node = Vector3.UnitX - normal * Vector3.Dot(Vector3.UnitX, normal);
        return Vector3.Normalize(node);
    }

    private static Found Search(GroundBody body, int laps) =>
        Searched.GetOrAdd((body.Name, laps), _ => new Lazy<Found>(() => Searching(body, laps))).Value;

    private static Found Searching(GroundBody body, int laps)
    {
        if (body.HasAir)
            throw new ArgumentException($"{body.Name} has air: no orbit just over its ground", nameof(body));
        var clock = Stopwatch.StartNew();
        // Started at the radius that clears any ground, then at each radius found.
        var radius = body.SeaRadius + body.MaxHeight + Clearance;
        var normal = Vector3.UnitZ;
        for (var round = 0; round < SearchRounds; round++)
        {
            normal = BestNormal(body, radius, laps);
            var next = body.SeaRadius + Highest(body, normal, radius, laps, FineDegrees) + Clearance;
            var settled = MathF.Abs(next - radius) < 0.5f;
            radius = next;
            if (settled)
                break;
        }
        // The track moves a little with the radius: raised until the track at it clears its highest
        // by the clearance (it can only rise, so it ends by the body's highest ground at the latest).
        for (var tries = 0; ; tries++)
        {
            var highest = Highest(body, normal, radius, laps, FineDegrees);
            if (body.SeaRadius + highest + Clearance <= radius)
                break;
            if (tries == 100)
                throw new InvalidOperationException($"the low pass's radius never settled ({radius:0.00} m, its track's highest {highest:0.00} m)");
            radius = body.SeaRadius + highest + Clearance;
        }
        var top = radius - body.SeaRadius - Clearance;
        return new Found(normal, top, radius, Math.Tau / Rate(body, radius), clock.Elapsed);
    }

    /// <summary>The normal whose circle of <paramref name="radius"/> crosses the lowest highest ground over <paramref name="laps"/> laps, read every <see cref="ReadDegrees"/>°.</summary>
    private static Vector3 BestNormal(GroundBody body, float radius, int laps)
    {
        var best = Lowest(body, radius, laps, Sphere());

        // The spacing between the sphere's normals, as an angle: each refinement's grid, a quarter
        // of the spacing before apart, reaches the spacing before either way.
        var step = MathF.Sqrt(4f * MathF.PI / Normals) / 4f;
        for (var refinement = 0; refinement < Refinements; refinement++, step /= 4f)
        {
            var a = Vector3.Normalize(Vector3.Cross(MathF.Abs(best.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY, best));
            var b = Vector3.Cross(best, a);
            var grid = new List<Vector3>();
            for (var i = -GridReach; i <= GridReach; i++)
                for (var j = -GridReach; j <= GridReach; j++)
                    grid.Add(Vector3.Normalize(best + a * (MathF.Tan(i * step)) + b * MathF.Tan(j * step)));
            best = Lowest(body, radius, laps, [.. grid]);
        }
        return best;
    }

    /// <summary>The <see cref="Normals"/> normals of a Fibonacci sphere: evenly spread over every direction.</summary>
    private static Vector3[] Sphere()
    {
        var normals = new Vector3[Normals];
        var golden = Math.PI * (3 - Math.Sqrt(5));
        for (var i = 0; i < Normals; i++)
        {
            var z = 1 - 2 * (i + 0.5) / Normals;
            var across = Math.Sqrt(1 - z * z);
            normals[i] = new Vector3((float)(across * Math.Cos(golden * i)), (float)(across * Math.Sin(golden * i)), (float)z);
        }
        return normals;
    }

    /// <summary>Which of <paramref name="normals"/> has the lowest highest ground under its circle, read every <see cref="ReadDegrees"/>° (the first, on a tie).</summary>
    private static Vector3 Lowest(GroundBody body, float radius, int laps, Vector3[] normals)
    {
        var highest = new float[normals.Length];
        Parallel.For(0, normals.Length, i => highest[i] = Highest(body, normals[i], radius, laps, ReadDegrees));
        var best = 0;
        for (var i = 1; i < normals.Length; i++)
            if (highest[i] < highest[best])
                best = i;
        return normals[best];
    }
}
