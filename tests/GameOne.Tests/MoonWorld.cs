using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Writes a saved world for looking from the moon in the running game (the GameDriver's
/// <c>tools/GameDriver/examples/gameone-moon.txt</c>): flying there is for later, so the world
/// starts there, with two landers landed on the moon (<see cref="OnTheMoon.Lander"/>: the satellite
/// launcher's upper stage, standing on its legs, at the moon), one at <see cref="Scene.MoonLandingSpot"/>, 20° from
/// its north pole, the other at the near side's middle (latitude 0, longitude 0), under the planet.
/// The world's time is when the landing spot sees the planet just over its horizon, rising, with
/// the sun up there too (found by searching the moon's lap, <see cref="SolarSystem.PlanetHeightFromMoon"/>).
/// It is saved through a <see cref="WorldStore"/>, so it is a world like any other, and nothing is
/// added to the game for it. Only with <c>GAMEONE_WRITE_MOON_WORLD</c> set to the folder to write
/// it into (the driver's <c>GAMEONE_WORLDS</c>); otherwise skipped.
/// </summary>
public sealed class MoonWorld(ITestOutputHelper output)
{
    /// <summary>How high the planet's centre is over the landing spot's horizon as the world starts, in degrees: just over it, its disc half risen.</summary>
    private const float RisingDegrees = 2f;

    /// <summary>The sun at least this high over the landing spot's horizon then, in degrees (it is never high there).</summary>
    private const float SunDegrees = 3f;

    private static Vector3 NearSideMiddle => CubeSphere.FromLatLon(0f, 0f);

    [MoonWorldFact]
    public void Writes_the_moon_world()
    {
        var time = Rising();
        var spot = OnTheMoon.LandingSpot;
        output.WriteLine($"world time {time:0.0} s: the planet {Degrees(SolarSystem.PlanetHeightFromMoon(spot, time)):0.0}° up, the sun {SunHeight(spot, time):0.0}°, the sun {Elongation(spot, time):0}° from the planet as the spot sees them");
        var (highest, highestAt, risen) = (float.NegativeInfinity, 0.0, double.NaN);
        for (var t = time; t < time + SolarSystem.MoonLapSeconds; t += 1.0)
        {
            var height = Degrees(SolarSystem.PlanetHeightFromMoon(spot, t));
            if (double.IsNaN(risen) && height >= 16f)
                risen = t - time;
            if (height > highest)
                (highest, highestAt) = (height, t - time);
        }
        output.WriteLine($"then: 16° up {risen:0} s on, highest {highest:0.0}° {highestAt:0} s on (a lap is {SolarSystem.MoonLapSeconds:0} s)");
        output.WriteLine($"from the near side's middle: the planet {Degrees(SolarSystem.PlanetHeightFromMoon(NearSideMiddle, time)):0.0}° up, the sun {SunHeight(NearSideMiddle, time):0.0}°");
        WorldState World() => new()
        {
            Time = time, Flights = 1, NextVesselId = 2,
            Vessels = [OnTheMoon.Lander(0, spot), OnTheMoon.Lander(1, NearSideMiddle)],
        };

        // Both stay where they are put, in a flight: landed on the moon, in its frame.
        using (var sim = new Simulation(world: World()))
        {
            var landers = sim.Flight.WorldVessels.Where(v => v.Body == GroundBody.Moon).ToList();
            Assert.Equal(2, landers.Count);
            var placed = landers.Select(v => v.Position).ToList();
            sim.Step(new FlightInput(SwitchTo: landers[0]));
            sim.Run((_, _) => default, _ => false, maxSeconds: 5);
            for (var i = 0; i < landers.Count; i++)
            {
                Assert.Equal(FlightState.Landed, landers[i].State);
                Assert.True(Vector3.Distance(landers[i].Position, placed[i]) < 0.01f, $"lander {i} moved {Vector3.Distance(landers[i].Position, placed[i]):0.000} m");
            }
            var reading = Readout.FlightReadings(sim.Flight)["Planet height"];
            output.WriteLine($"after 5 s, flying the one at the landing spot: Planet height {reading}");
        }

        var saved = DriverWorlds.Write(World(), "moon", "The moon: two landers on it", output);
        Assert.Equal(2, saved.Vessels.Count(v => v.Body == GroundBody.Moon.Name));
    }

    /// <summary>
    /// The first world time in the planet's year when the landing spot sees the planet's centre
    /// rise through <see cref="RisingDegrees"/> with the sun at least <see cref="SunDegrees"/> up
    /// there: the one whose sun is furthest round from the planet in the sky, short of opposite it
    /// (so the planet shows some of its night side, with its lights, and some of its day).
    /// </summary>
    private double Rising()
    {
        var spot = OnTheMoon.LandingSpot;
        var year = (double)Scene.DaySeconds * Scene.YearDays;
        var (best, bestScore) = (double.NaN, double.NegativeInfinity);
        var was = Degrees(SolarSystem.PlanetHeightFromMoon(spot, 0));
        for (var t = 1.0; t < year; t += 1.0)
        {
            var height = Degrees(SolarSystem.PlanetHeightFromMoon(spot, t));
            if (was < RisingDegrees && height >= RisingDegrees && SunHeight(spot, t) >= SunDegrees)
            {
                // Best with the sun some 100° from the planet: the planet's night side and day side both shown.
                var score = -MathF.Abs(Elongation(spot, t) - 100f);
                output.WriteLine($"  rising at {t:0} s: the sun {SunHeight(spot, t):0.0}° up, {Elongation(spot, t):0}° from the planet");
                if (score > bestScore)
                    (best, bestScore) = (t, score);
            }
            was = height;
        }
        Assert.False(double.IsNaN(best), "the planet never rises over the landing spot with the sun up there");
        return best;
    }

    private static float Degrees(float sine) => float.RadiansToDegrees(MathF.Asin(sine));

    /// <summary>The ground's frame's up at moon-fixed <paramref name="direction"/> on the moon at world time <paramref name="t"/>, and the place there.</summary>
    private static (Vector3 Up, Vector3 At) Place(Vector3 direction, double t)
    {
        var up = Vector3.Transform(direction, SolarSystem.MoonOrientation(t));
        return (up, SolarSystem.MoonCentre(t) + up * Scene.MoonRadius);
    }

    /// <summary>The sun's height over the horizon at moon-fixed <paramref name="direction"/> at <paramref name="t"/>, in degrees.</summary>
    private static float SunHeight(Vector3 direction, double t) => Degrees(Vector3.Dot(Place(direction, t).Up, SolarSystem.SunDirectionFromMoon(t)));

    /// <summary>How far apart the sun and the planet's centre are in the sky at moon-fixed <paramref name="direction"/> at <paramref name="t"/>, in degrees.</summary>
    private static float Elongation(Vector3 direction, double t)
    {
        var toPlanet = Vector3.Normalize(Planet.Centre - Place(direction, t).At);
        return float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(toPlanet, SolarSystem.SunDirectionFromMoon(t)), -1f, 1f)));
    }
}

/// <summary>
/// Writing a world for the GameDriver (see <see cref="MoonWorld"/>, <see cref="MoonTripWorlds"/>):
/// saved through a <see cref="WorldStore"/> into the folder <c>GAMEONE_WRITE_MOON_WORLD</c> names,
/// so it is a world like any other.
/// </summary>
internal static class DriverWorlds
{
    /// <summary><paramref name="world"/> saved as <paramref name="id"/>, named <paramref name="name"/>; and loaded back, as the game would.</summary>
    public static WorldState Write(WorldState world, string id, string name, ITestOutputHelper output)
    {
        var folder = Environment.GetEnvironmentVariable(MoonWorldFactAttribute.Variable)!;
        var store = new WorldStore(folder);
        var now = DateTime.Now;
        store.Save(world with { Id = id, Name = name, Created = now, LastPlayed = now });
        output.WriteLine($"written to {Path.Combine(folder, id)}");
        return store.Load(id);
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that only runs with <c>GAMEONE_WRITE_MOON_WORLD</c> set (to the
/// folder to write the moon world into, or the moon trip's): it is for the GameDriver, not a test of the game.
/// </summary>
public sealed class MoonWorldFactAttribute : FactAttribute
{
    public const string Variable = "GAMEONE_WRITE_MOON_WORLD";

    public MoonWorldFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) is null or "")
            Skip = $"Writes a world for the GameDriver: set {Variable} to the folder to write it into.";
    }
}
