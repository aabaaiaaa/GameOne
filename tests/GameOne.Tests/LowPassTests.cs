using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// A very low orbit of the moon (<see cref="LowPass"/>): the round orbit found from its height map
/// whose track crosses the lowest highest ground over its laps, and the lander flown on it in the
/// physics, with its engine off, <see cref="LowPass.Clearance"/> over that ground.
/// </summary>
public sealed class LowPassTests(ITestOutputHelper output)
{
    private static readonly Pilot Nobody = (_, _) => default;
    private static GroundBody Moon => GroundBody.Moon;

    /// <summary>How far under <see cref="LowPass.Clearance"/> its lowest point may come over the ground: the lander's own size below its centre of mass, and the ground between the track's reads.</summary>
    private const float GroundLeeway = 5f;

    /// <summary>How far its periapsis and apoapsis may stray from the circle's height, in metres.</summary>
    private const float RoundWithin = 2f;

    [Fact]
    public void The_search_finds_a_circle_clearing_the_ground_it_crosses_over_its_laps()
    {
        foreach (var laps in new[] { 1, 5 })
        {
            var (normal, highest, radius, lap) = LowPass.Find(Moon, MoonTrip.Start, laps);
            output.WriteLine($"{laps} lap(s): normal ({normal.X:0.0000}, {normal.Y:0.0000}, {normal.Z:0.0000}), {Inclination(normal):0.0}° from the moon's equator; " +
                $"Highest {highest:0.00} m, Radius {radius:0.00} m, LapSeconds {lap:0.0}; searched in {LowPass.SearchTime(Moon, laps).TotalSeconds:0.00} s " +
                $"(the moon's highest ground {Moon.MaxHeight:0} m)");
            Assert.Equal(Moon.SeaRadius + highest + LowPass.Clearance, radius, 0.01f);
            Assert.Equal(Math.Tau * Math.Sqrt(Math.Pow(radius, 3) / Moon.Mu), lap, 1e-6);

            // Read every quarter degree, nothing on the track is over its highest (but a millimetre, the floats' rounding).
            var read = LowPass.Highest(Moon, normal, radius, laps, 0.25);
            Assert.True(read <= highest + 0.001f, $"{laps} lap(s): ground {read:0.000} m high under the track, over its Highest {highest:0.000} m");

            // The refinement found no worse than the sphere's best alone, at the same radius, read alike.
            var (coarse, coarseHighest) = LowPass.Coarse(Moon, radius, laps);
            var refined = LowPass.Highest(Moon, normal, radius, laps, LowPass.ReadDegrees);
            output.WriteLine($"  read every {LowPass.ReadDegrees}°: the found track {refined:0.00} m, the sphere's best ({coarse.X:0.000}, {coarse.Y:0.000}, {coarse.Z:0.000}) {coarseHighest:0.00} m");
            Assert.True(refined <= coarseHighest, $"{laps} lap(s): the refined track's {refined:0.00} m is over the sphere's best's {coarseHighest:0.00} m");

            // No higher than the equator's circle, either way round, or the polar circles'.
            foreach (var (name, other) in new[] { ("equatorial, prograde", Moon.NorthPole), ("equatorial, retrograde", -Moon.NorthPole), ("polar, its normal +X", Vector3.UnitX), ("polar, its normal +Y", Vector3.UnitY) })
            {
                var theirs = LowPass.Highest(Moon, other, radius, laps, 0.25);
                output.WriteLine($"  the {name} circle at that radius: {theirs:0.00} m");
                Assert.True(highest <= theirs, $"{laps} lap(s): the found track's highest {highest:0.00} m is over the {name} circle's {theirs:0.00} m");
            }
        }
    }

    /// <summary>How long before the track's highest ground the stretch flown in the normal suite starts, and how long after it it ends, in seconds.</summary>
    private const double AroundHighest = 15.0;

    [Fact]
    public void A_very_low_orbit_of_the_moon_clears_its_highest_ground()
    {
        // The stretch of the one-lap circle over its track's highest ground, from just before it
        // to just after (a whole lap is some 250 s of flight, its tiles laid all the way round:
        // long, below).
        var (normal, _, radius, lap) = LowPass.Find(Moon, MoonTrip.Start, 1);
        var (top, degrees) = LowPass.HighestAt(Moon, normal, radius, 1, LowPass.FineDegrees);
        var over = LowPass.SecondsFor(Moon, radius, degrees);
        var from = Math.Max(0.0, over - AroundHighest);
        output.WriteLine($"the track's highest ground, {top:0.00} m, {degrees:0.00}° round, {over:0.0} s on");
        Flown(1, from, Math.Min(lap, over + AroundHighest) - from);
    }

    /// <summary>The whole lap: <see cref="A_very_low_orbit_of_the_moon_clears_its_highest_ground"/> leaves out the rest of the lap round its highest ground.</summary>
    [LongFact]
    [Trait("Run", "Long")]
    public void A_very_low_orbit_of_the_moon_clears_its_ground_for_a_lap() => Flown(1, 0.0, LowPass.Find(Moon, MoonTrip.Start, 1).LapSeconds);

    /// <summary>The same over five laps: <see cref="A_very_low_orbit_of_the_moon_clears_its_ground_for_a_lap"/> leaves out only the four laps after the first (its own search's circle, for one lap).</summary>
    [LongFact]
    [Trait("Run", "Long")]
    public void A_very_low_orbit_of_the_moon_clears_its_ground_for_five_laps() => Flown(5, 0.0, 5 * LowPass.Find(Moon, MoonTrip.Start, 5).LapSeconds);

    /// <summary>
    /// The lander alone on <see cref="LowPass.Find"/>'s circle over <paramref name="laps"/> laps,
    /// started <paramref name="from"/> seconds on along it, flown <paramref name="seconds"/> with
    /// its engine off.
    /// </summary>
    private void Flown(int laps, double from, double seconds)
    {
        var time = MoonTrip.Start;
        var (_, highest, radius, lap) = LowPass.Find(Moon, time, laps);
        var (sim, lander) = MoonTripTests.Flying(MoonTrip.World(time, LowPass.Start(0, Moon, time, laps, from)));
        using var _ = sim;
        var height = radius - Moon.SeaRadius;
        var fuel = lander.Fuel;
        var (least, leastAt, wide, touched) = (float.PositiveInfinity, 0.0, 0f, false);
        var start = sim.Time;
        sim.Run(Nobody, _ =>
        {
            if (lander.Ground < least)
                (least, leastAt) = (lander.Ground, sim.Time - start);
            wide = MathF.Max(wide, MathF.Max(MathF.Abs(lander.Apoapsis - height), MathF.Abs(lander.Periapsis - height)));
            touched |= !lander.OnFormula && (sim.Physics.TouchingAnything(lander.Craft) || sim.Physics.TouchesBegun(lander.Craft).Count > 0);
            return lander.State != FlightState.Flying;
        }, maxSeconds: seconds);
        output.WriteLine($"{seconds:0.0} s from {from:0.0} s on, of {laps} lap(s) of {lap:0.0} s, {height:0.0} m over the datum (the track's highest ground {highest:0.0} m): " +
            $"{lander.State} {lander.CrashReason}, after {sim.Time - start:0.0} s; the least Ground {least:0.0} m, {from + leastAt:0} s on; " +
            $"periapsis and apoapsis within {wide:0.00} m of the circle's; fuel {lander.Fuel:0.000} (was {fuel:0.000})");
        Assert.Equal(FlightState.Flying, lander.State);
        Assert.True(sim.Time - start >= seconds - 0.1, $"flown only {sim.Time - start:0.0} s");
        Assert.False(touched, "it touched something");
        Assert.True(least >= LowPass.Clearance - GroundLeeway, $"it came within {least:0.0} m of the ground, {from + leastAt:0} s on");
        // And it was over its track's highest ground: by then, its lowest point no higher than the clearance (but a metre, the ground beside the track).
        Assert.True(least <= LowPass.Clearance + 1f, $"never nearer the ground than {least:0.0} m: not over its track's highest ground");
        Assert.True(wide <= RoundWithin, $"its periapsis or apoapsis strayed {wide:0.00} m from the circle's {height:0.0} m");
        Assert.Equal(fuel, lander.Fuel);
    }

    /// <summary>How far the orbit of <paramref name="normal"/> is tilted from the moon's equator, in degrees (over 90 going round against its turn).</summary>
    private static float Inclination(Vector3 normal) =>
        float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(normal, Moon.NorthPole), -1f, 1f)));
}
