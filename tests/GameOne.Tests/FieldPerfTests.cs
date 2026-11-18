using System.Diagnostics;
using System.Numerics;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Where a slowed frame's time goes as a fast booster dives into the field of 49 frozen boosters
/// (the paced check's case, <see cref="PerfScenes.FastCollisions"/>), measured as each part's
/// multiple of Jolt's own step in the same frames: Jolt's native step does the same work whatever
/// the game's code does round it, and runs faster or slower with the machine, so the ratios hold on
/// a busy or slow machine where the times don't. Paced at real time, as the check is (the tiles'
/// worker keeps up as in play), a few runs, each its own process-warm flight.
/// </summary>
public sealed class FieldPerfTests(ITestOutputHelper output)
{
    private const int Runs = 3;

    [PerfFact]
    public void The_field_of_boosters_slowed_where_the_time_goes()
    {
        var runs = new List<Dictionary<string, double>>();
        using (FastCores.Hold())
        {
            try
            {
                Fly(); // warm-up: the JIT and the first tiles
                for (var i = 0; i < Runs; i++)
                    runs.Add(Fly());
            }
            finally
            {
                Timings.Enabled = false;
            }
        }
        output.WriteLine($"The field of boosters, slowed ({PerfBaseline.Configuration}, {Runs} runs): each part's ms a slowed frame, and as a multiple of Jolt's in the same frames");
        output.WriteLine("  part                         ms (median of runs)   x Jolt   x Jolt each run");
        foreach (var key in runs[0].Keys)
        {
            var ms = runs.Select(r => r[key]).Order().ToList();
            var ratios = runs.Select(r => r[key] / r["Jolt"]).ToList();
            var sorted = ratios.Order().ToList();
            output.WriteLine($"  {key,-28} {ms[ms.Count / 2],10:0.000} {sorted[sorted.Count / 2],15:0.00}   {string.Join(" ", ratios.Select(r => r.ToString("0.00")))}");
        }
    }

    /// <summary>The field's dive flown to the end of its slow-down, paced: the slowed frames' mean ms by part and piece, their count, and the crafts awake.</summary>
    private static Dictionary<string, double> Fly()
    {
        var field = TerrainTileTests.FrozenField(within: 4, firstId: 1);
        var zone = Scene.LandingZoneCentre;
        var centre = field.MinBy(b => Vector3.Distance(b.Position.ToSingle(), zone))!;
        var middle = FastCollisionTests.Middle(centre.Position.ToSingle(), centre.Rotation);
        var dive = Vector3.Normalize(Planet.East(middle) - Planet.Up(middle));
        var world = new WorldState { Vessels = [FastCollisionTests.Flying(middle, dive, Scene.GroundTopSpeed, 3f, 0), .. field], NextVesselId = field.Count + 1 };
        var (sim, _, _) = FastCollisionTests.Fly(world);
        using var _ = sim;
        var frame = TimeSpan.FromSeconds(Simulation.FrameSeconds);
        var clock = Stopwatch.StartNew();
        var slowed = new List<StepSample>();
        var (seen, frames, awake) = (false, 0, 0.0);
        Timings.Enabled = true;
        while (frames < 1200 && !(seen && sim.Flight.Slowed is null))
        {
            var began = clock.Elapsed;
            var isSlowed = sim.Flight.Slowed is not null;
            sim.Step(default);
            frames++;
            if (isSlowed)
            {
                seen = true;
                slowed.Add(Timings.Steps[^1]);
                awake += sim.Flight.WorldVessels.Count(v => !v.OnFormula && v.State != FlightState.Crashed && !v.Craft.Frozen);
            }
            while (clock.Elapsed < began + frame)
                Thread.SpinWait(50);
        }
        Timings.Enabled = false;
        var n = Math.Max(1, slowed.Count);
        var result = new Dictionary<string, double> { ["Whole"] = slowed.Sum(s => s.TotalMs) / n };
        foreach (var part in Enum.GetValues<StepPart>())
            result[part.ToString()] = slowed.Sum(s => s[part]) / n;
        foreach (var detail in Enum.GetValues<StepDetail>())
            result["  " + detail] = slowed.Sum(s => s[detail]) / n;
        result["(steps a frame)"] = slowed.Sum(s => s.PhysicsSteps) / (double)n;
        result["(awake crafts)"] = awake / n;
        result["(slowed frames)"] = slowed.Count;
        return result;
    }
}
