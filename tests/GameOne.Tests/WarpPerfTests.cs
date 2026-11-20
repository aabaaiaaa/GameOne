using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>What a run of warp frames cost (see <see cref="WarpPerfTests.Measure"/>): medians over its frames.</summary>
/// <param name="FrameMs">A frame's steps, whole.</param>
/// <param name="StepMs">One step, whole.</param>
/// <param name="Steps">Steps a frame.</param>
/// <param name="PhysicsSteps">Of them, those that stepped a physics world.</param>
/// <param name="Reached">The rate the frames ran at (game seconds in a real frame's seconds).</param>
/// <param name="Parts">Each step part's time in a frame, ms.</param>
/// <param name="Details">Each piece's time in a frame, ms (within its part's).</param>
/// <param name="Again">How many times the scene was begun again, having run out (see <see cref="WarpPerfTests.Measure"/>).</param>
internal sealed record WarpMeasure(double FrameMs, double StepMs, double Steps, double PhysicsSteps, double Reached,
    IReadOnlyDictionary<StepPart, double> Parts, IReadOnlyDictionary<StepDetail, double> Details, int Again);

/// <summary>
/// Where a frame in warp goes, headless: the flight's steps at the rate asked, within
/// <see cref="Flight.WarpBudgetSeconds"/> as the game gives them, each part and piece of them timed
/// (<see cref="Timings"/>). They report and never fail on times: run only when working on the warp
/// (<c>GAMEONE_PERF=1</c>), in Release.
/// </summary>
public sealed class WarpPerfTests(ITestOutputHelper output)
{
    /// <summary>Frames run before the timed ones, so the JIT and the first tiles are done.</summary>
    private const int WarmUpFrames = 60;

    /// <summary>Frames timed.</summary>
    private const int TimedFrames = 300;

    [PerfFact] public void Where_a_frame_at_32x_goes_on_a_moon_transfer() => Report("Moon transfer", WarpScenes.MoonTransfer(), 32f);
    [PerfFact] public void Where_a_frame_at_the_physics_top_rate_goes_on_a_moon_transfer() => Report("Moon transfer", WarpScenes.MoonTransfer(), Scene.PhysicsTopRate);
    [PerfFact] public void Where_a_frame_at_32x_goes_in_a_low_orbit() => Report("Low orbit", WarpScenes.LowOrbit(), 32f);
    [PerfFact] public void Where_a_frame_at_32x_goes_among_a_hundred_satellites() => Report("Hundred satellites", WarpScenes.HundredSatellites(), 32f);
    [PerfFact] public void Where_a_frame_at_the_top_rate_goes_among_a_hundred_satellites() => Report("Hundred satellites", WarpScenes.HundredSatellites(), Scene.TopRate);

    [PerfFact]
    public void Where_a_frame_at_the_top_rate_goes_on_a_moon_transfer()
    {
        // The transfer lasts some hundred frames at the top rate before its pass by the moon comes
        // down near its ground and drops the warp: begun again each time it has.
        var (world, id) = WarpScenes.MoonTransferWorld();
        var (sim, flown) = WarpScenes.Flying(world, id);
        Report("Moon transfer", (sim, flown), Scene.TopRate, () => WarpScenes.Again(sim, world, id));
    }

    [PerfFact] public void Where_a_frame_at_the_top_rate_goes_in_a_low_orbit() => Report("Low orbit", WarpScenes.LowOrbit(), Scene.TopRate);
    [PerfFact] public void Where_a_frame_at_the_top_rate_goes_in_a_full_world() => Report("A full world", WarpScenes.FullWorld(), Scene.TopRate);

    private void Report(string name, (Simulation Sim, Vessel Flown) scene, float rate, Func<Vessel>? again = null)
    {
        var (sim, _) = scene;
        using var _s = sim;
        var measure = Measure(sim, rate, TimedFrames, again);
        var flown = sim.Flight.Controlled;
        output.WriteLine($"{name} at {rate:0}x ({TimedFrames} frames after {WarmUpFrames}{(measure.Again > 0 ? $", begun again {measure.Again} times" : "")}):");
        output.WriteLine($"  frame (its steps, whole) median {measure.FrameMs:0.000} ms of the {Flight.WarpBudgetSeconds * 1000:0} ms budget; one step median {measure.StepMs:0.000} ms");
        output.WriteLine($"  {measure.Steps:0.#} steps a frame ({measure.PhysicsSteps:0.#} of them stepping a physics), reached {measure.Reached:0.0}x");
        output.WriteLine($"  {sim.Flight.WorldVessels.Count} vessels in the world, {sim.Flight.WorldVessels.Count(v => v.State == FlightState.Crashed)} of them wrecked, {sim.Flight.Log.Count} entries in its log");
        output.WriteLine("  part / piece           ms a frame   share of the frame");
        foreach (var (part, ms) in measure.Parts.OrderByDescending(p => p.Value))
            output.WriteLine($"  {part,-22} {ms,9:0.000}   {ms / measure.FrameMs,6:0%}");
        foreach (var (detail, ms) in measure.Details.OrderByDescending(p => p.Value).Where(p => p.Value > 0))
            output.WriteLine($"    {Timings.PartOf(detail)}: {detail,-18} {ms,9:0.000}   {ms / measure.FrameMs,6:0%}");
        Assert.Equal(FlightState.Flying, flown.State);
    }

    /// <summary>
    /// <paramref name="frames"/> frames of <paramref name="sim"/> at <paramref name="rate"/> (the rate keys
    /// pressed up to it, <see cref="WarmUpFrames"/> frames run untimed first), each
    /// <c>Flight.RunFrame</c> with the game's budget: the medians of the steps' parts and pieces a
    /// frame, the steps a frame, the rate reached and the frame's steps' whole time. With
    /// <paramref name="again"/>, a scene that runs out (its warp dropped under the rate) is begun
    /// again by it and the keys pressed up to the rate again, and only the frames begun at the rate
    /// are timed.
    /// </summary>
    internal static WarpMeasure Measure(Simulation sim, float rate, int frames, Func<Vessel>? again = null)
    {
        using var fast = FastCores.Hold(); // the best case (High priority on the fastest cores, which the game doesn't take), so a run is like the next
        var flight = sim.Flight;
        void Climb()
        {
            for (var i = 0; i < Flight.Rates.Length && flight.PlayerRate < rate; i++)
                sim.Step(new FlightInput(Faster: true));
            Assert.Equal(rate, flight.TimeScale);
        }
        var begun = 0;
        bool RunOut()
        {
            if (again is null || flight.TimeScale >= rate)
                return false;
            again();
            Climb();
            begun++;
            return true;
        }
        Climb();

        for (var i = 0; i < WarmUpFrames; i++)
            if (!RunOut())
                flight.RunFrame(Simulation.FrameSeconds, default, Flight.WarpBudgetSeconds);

        var parts = Enum.GetValues<StepPart>();
        var details = Enum.GetValues<StepDetail>();
        var frameMs = new List<double>();
        var stepMs = new List<double>();
        var steps = new List<double>();
        var physics = new List<double>();
        var reached = new List<double>();
        var byPart = parts.ToDictionary(p => p, _ => new List<double>());
        var byDetail = details.ToDictionary(d => d, _ => new List<double>());

        Timings.Enabled = true;
        try
        {
            for (var i = 0; i < frames; i++)
            {
                if (RunOut())
                {
                    i--;
                    continue;
                }
                var before = Timings.Steps.Count;
                var game = flight.RunFrame(Simulation.FrameSeconds, default, Flight.WarpBudgetSeconds);
                Assert.True(Timings.Steps.Count < Timings.StepCapacity, "the timings' step ring filled: fewer frames, or a bigger ring");
                var taken = Timings.Steps.Count - before;
                var whole = 0.0;
                var inFrame = new Dictionary<StepPart, double>();
                var pieces = new Dictionary<StepDetail, double>();
                var physicsSteps = 0;
                for (var s = before; s < before + taken; s++)
                {
                    var step = Timings.Steps[s];
                    whole += step.TotalMs;
                    physicsSteps += step.PhysicsSteps;
                    stepMs.Add(step.TotalMs);
                    foreach (var part in parts)
                        inFrame[part] = inFrame.GetValueOrDefault(part) + step[part];
                    foreach (var detail in details)
                        pieces[detail] = pieces.GetValueOrDefault(detail) + step[detail];
                }
                frameMs.Add(whole);
                steps.Add(taken);
                physics.Add(physicsSteps);
                reached.Add(game / Simulation.FrameSeconds);
                foreach (var part in parts)
                    byPart[part].Add(inFrame.GetValueOrDefault(part));
                foreach (var detail in details)
                    byDetail[detail].Add(pieces.GetValueOrDefault(detail));
            }
        }
        finally
        {
            Timings.Enabled = false;
        }

        return new WarpMeasure(Median(frameMs), Median(stepMs), Median(steps), Median(physics), Median(reached),
            byPart.ToDictionary(p => p.Key, p => Median(p.Value)), byDetail.ToDictionary(p => p.Key, p => Median(p.Value)), begun);
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }
}
