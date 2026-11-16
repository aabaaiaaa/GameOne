using System.Drawing;
using System.Numerics;
using System.Diagnostics;
using GameOne.Rendering;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The timings recorder (<see cref="Timings"/>): a step's laps add up to its whole, a step or
/// frame open across a switch is dropped, nothing is recorded or allocated when it shouldn't be,
/// a frame takes in its steps and counts the world, a summary is the mean of its frames, and the
/// GPU's timestamps give the right time across their counter's wrap. The recorder is static, so
/// each test leaves it off; the tests run one at a time.
/// </summary>
public sealed class TimingsTests : IDisposable
{
    public void Dispose() => Timings.Enabled = false;

    private static void Spin(double ms)
    {
        long end = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000.0);
        while (Stopwatch.GetTimestamp() < end)
        {
        }
    }

    private static void WholeStep(int tiles = 0)
    {
        Timings.BeginStep();
        Timings.Lap(StepPart.Jolt);
        Timings.EndStep(tiles);
    }

    [Fact]
    public void A_step_s_laps_go_to_their_parts_and_add_up_to_the_step()
    {
        Timings.Enabled = true;
        Timings.BeginStep();
        Spin(0.2);
        Timings.Lap(StepPart.Jolt);
        Spin(0.2);
        Timings.Lap(StepPart.Water);
        Timings.CountPhysicsStep();
        Timings.CountPhysicsStep();
        Timings.EndStep(tiles: 7);

        Assert.Single(Timings.Steps);
        var s = Timings.Steps[0];
        Assert.True(s[StepPart.Jolt] > 0.1 && s[StepPart.Water] > 0.1);
        Assert.Equal(s.TotalMs, Enum.GetValues<StepPart>().Sum(p => s[p]), 6);
        Assert.All(Enum.GetValues<StepPart>(), p => Assert.True(s[p] >= 0));
        Assert.Equal(2, s.PhysicsSteps);
        Assert.Equal(7, s.Tiles);
    }

    [Fact]
    public void A_piece_is_timed_within_its_part_and_not_beside_it()
    {
        Timings.Enabled = true;
        Timings.BeginStep();
        Spin(0.1);
        var started = Timings.Start();
        Spin(0.2);
        Timings.Stop(StepDetail.Samples, started);
        Timings.Lap(StepPart.Tiles);
        Timings.EndStep(0);
        Timings.BeginFrame();
        Timings.EndFrame(1f, 1f);

        var s = Timings.Steps[0];
        Assert.True(s[StepDetail.Samples] > 0.15);
        Assert.True(s[StepPart.Tiles] >= s[StepDetail.Samples]); // the part takes in its piece ...
        Assert.Equal(s.TotalMs, Enum.GetValues<StepPart>().Sum(p => s[p]), 6); // ... and the step is its parts alone
        Assert.Equal(s[StepDetail.Samples], Timings.Frames[^1][StepDetail.Samples]); // the frame takes it in too
        Assert.Equal(StepPart.Tiles, Timings.PartOf(StepDetail.Removals));
        Assert.Equal(StepPart.Contact, Timings.PartOf(StepDetail.Movers));
        Assert.Equal(StepPart.Contact, Timings.PartOf(StepDetail.Vessels));
        Assert.Equal(StepPart.Rules, Timings.PartOf(StepDetail.Collisions));
        Assert.Equal(StepPart.Rules, Timings.PartOf(StepDetail.Formula));
        Assert.Equal(StepPart.Rules, Timings.PartOf(StepDetail.FormulaCollisions));

        // Off, or started before the step opened, it times nothing (and takes no clock off).
        Timings.Enabled = false;
        Assert.Equal(0, Timings.Start());
        Timings.Enabled = true;
        var before = Timings.Start();
        Timings.BeginStep();
        Timings.Stop(StepDetail.Bodies, before);
        Timings.EndStep(0);
        Assert.Equal(0, Timings.Steps[^1][StepDetail.Bodies]);
    }

    [Fact]
    public void Turning_timings_on_or_off_mid_step_records_no_step()
    {
        Timings.Enabled = false;
        Timings.BeginStep();
        Timings.Enabled = true;
        Timings.Lap(StepPart.Jolt);
        Timings.EndStep(0);
        Assert.Empty(Timings.Steps);

        Timings.BeginStep();
        Timings.Enabled = false;
        Timings.Enabled = true;
        Timings.Lap(StepPart.Jolt);
        Timings.EndStep(0);
        Assert.Empty(Timings.Steps);

        Timings.BeginStep();
        Spin(0.1);
        Timings.Lap(StepPart.Jolt);
        Timings.EndStep(0);
        Assert.Single(Timings.Steps);
        var s = Timings.Steps[0];
        Assert.All(Enum.GetValues<StepPart>(), p => Assert.True(s[p] >= 0));
        Assert.True(s.TotalMs < 1000);
    }

    [Fact]
    public void A_frame_open_across_turning_timings_off_and_on_is_not_recorded()
    {
        // Its start was before the switch, so its total would take in time from before it.
        Timings.Enabled = true;
        Timings.BeginFrame();
        Timings.Enabled = false;
        Timings.Enabled = true;
        Timings.FrameLap(FramePart.Hud);
        Timings.EndFrame(1f, 1f);
        Assert.Empty(Timings.Frames);

        Timings.BeginFrame();
        Spin(0.1);
        Timings.FrameLap(FramePart.Hud);
        Timings.EndFrame(1f, 1f);
        Assert.Single(Timings.Frames);
        var f = Timings.Frames[0];
        Assert.All(Enum.GetValues<FramePart>(), p => Assert.True(f[p] >= 0));
        Assert.True(f.TotalMs < 1000);
    }

    [Fact]
    public void With_timings_off_nothing_is_recorded()
    {
        // Turning it on empties what an earlier test left, and off keeps nothing new.
        Timings.Enabled = true;
        Timings.Enabled = false;
        for (int i = 0; i < 100; i++)
        {
            WholeStep();
        }
        Timings.BeginFrame();
        Timings.EndFrame(1f, 1f);

        Assert.Empty(Timings.Steps);
        Assert.Empty(Timings.Frames);
        Assert.Null(Timings.Summary);
    }

    [Fact]
    public void Turning_timings_on_starts_afresh()
    {
        Timings.Enabled = true;
        for (int i = 0; i < 3; i++)
        {
            WholeStep();
        }
        Assert.Equal(3, Timings.Steps.Count);
        Timings.Enabled = false;
        Timings.Enabled = true;
        Assert.Empty(Timings.Steps);
        Assert.Empty(Timings.Frames);
    }

    [Fact]
    public void Past_its_capacity_it_keeps_the_latest_steps_and_allocates_nothing()
    {
        Timings.Enabled = true;
        for (int i = 0; i < Timings.StepCapacity + 10; i++)
        {
            WholeStep(tiles: i);
        }
        Assert.Equal(Timings.StepCapacity, Timings.Steps.Count);
        Assert.Equal(Timings.StepCapacity + 9, Timings.Steps[^1].Tiles);
        Assert.Equal(10, Timings.Steps[0].Tiles); // the oldest kept: the first ten gone, in order

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            WholeStep();
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void A_frame_takes_its_steps_parts_and_its_own_and_they_add_up()
    {
        Timings.Enabled = true;
        Timings.BeginStep();
        Spin(0.1);
        Timings.Lap(StepPart.Jolt);
        Timings.EndStep(0);
        Timings.BeginFrame();
        Spin(0.2);
        Timings.FrameLap(FramePart.Hud);
        Timings.Gpu([1, 2, 0.5, 0.25, 0.25]);
        Timings.EndFrame(32f, 17f);

        var f = Timings.Frames[^1];
        Assert.Equal(1, f.Steps);
        Assert.Equal(Timings.Steps[^1][StepPart.Jolt], f[StepPart.Jolt]);
        Assert.Equal(Timings.Steps[^1].TotalMs, f[FramePart.Flight], 6);
        Assert.True(f[FramePart.Hud] > 0.1);
        Assert.Equal(f.TotalMs, Enum.GetValues<FramePart>().Sum(p => f[p]), 6);
        Assert.Equal(4.0, f.GpuMs, 6);
        Assert.Equal(2.0, f[GpuPass.Scene]);
        Assert.Equal(32f, f.WarpAsked);
        Assert.Equal(17f, f.WarpReached);

        Timings.BeginFrame();
        Timings.EndFrame(1f, 1f);
        var g = Timings.Frames[^1];
        Assert.True(double.IsNaN(g.GpuMs));
        Assert.True(double.IsNaN(g[GpuPass.Scene]));
        Assert.Equal(0, g.Steps);
    }

    [Fact]
    public void A_summary_averages_its_frames()
    {
        var a = new FrameSample { TotalMs = 10, PhysicsSteps = 2, WarpReached = 16f, GpuMs = 4 };
        a[StepPart.Jolt] = 1;
        a[StepDetail.Near] = 0.5;
        var b = new FrameSample { TotalMs = 20, PhysicsSteps = 4, WarpReached = 18f, GpuMs = double.NaN };
        b[StepPart.Jolt] = 3;
        b[StepDetail.Near] = 1.5;

        var s = TimingsSummary.Of([a, b]);
        Assert.Equal(1, s[StepDetail.Near], 6);

        Assert.Equal(2, s.Frames);
        Assert.Equal(15, s.FrameMs, 6);
        Assert.Equal(1000 / 15.0, s.Fps, 6);
        Assert.Equal(3, s.PhysicsSteps, 6);
        Assert.Equal(2, s[StepPart.Jolt], 6);
        Assert.Equal(17f, s.WarpReached);
        Assert.Equal(4, s.GpuMs, 6);
    }

    [Fact]
    public void A_second_with_no_steps_or_GPU_times_summarises_to_zeros_and_no_GPU()
    {
        var frame = new FrameSample { TotalMs = 16, Steps = 0, GpuMs = double.NaN };
        foreach (var pass in Enum.GetValues<GpuPass>())
        {
            frame[pass] = double.NaN;
        }

        var s = TimingsSummary.Of([frame, frame]);

        Assert.Equal(0, s.PhysicsSteps);
        Assert.All(Enum.GetValues<StepPart>(), p => Assert.Equal(0, s[p]));
        Assert.True(double.IsNaN(s.GpuMs));
        Assert.True(double.IsNaN(s[GpuPass.Scene]));
        Assert.False(double.IsNaN(s.FrameMs));

        var none = TimingsSummary.Of([]);
        Assert.Equal(0, none.Frames);
        Assert.Equal(0, none.FrameMs);
        Assert.Equal(0, none.Fps);
        Assert.False(double.IsNaN(none.PhysicsSteps));
        Assert.All(Enum.GetValues<FramePart>(), p => Assert.Equal(0, none[p]));
        Assert.True(double.IsNaN(none.GpuMs));
    }

    private static void Fly(Simulation sim, int frames)
    {
        var pilot = new OrbitAutopilot();
        for (var i = 0; i < frames; i++)
        {
            sim.Step(pilot.Fly(sim.Flight, Simulation.FrameSeconds));
        }
    }

    [Fact]
    public void With_timings_off_a_flight_records_nothing()
    {
        Timings.Enabled = true; // off keeps the last run's samples, so empty them first
        Timings.Enabled = false;
        using var sim = new Simulation(Mission.ReachOrbit);
        Fly(sim, 120);
        Assert.Empty(Timings.Steps);
    }

    [Fact]
    public void Every_part_of_a_step_is_timed_on_a_climb_from_the_pad()
    {
        // Off the pad, so the tiles are laid, the sea has its phases and contact is judged.
        using var sim = new Simulation(Mission.ReachOrbit);
        Timings.Enabled = true;
        Fly(sim, 120);
        Assert.Equal(120, Timings.Steps.Count);
        Assert.All(Timings.Steps, s => Assert.Equal(2, s.PhysicsSteps));
        Assert.All(Timings.Steps, s => Assert.Equal(s.TotalMs, Enum.GetValues<StepPart>().Sum(p => s[p]), 6));
        Assert.All(Enum.GetValues<StepPart>(), p => Assert.True(Timings.Steps.Sum(s => s[p]) > 0, $"{p} never timed"));
        Assert.Equal(sim.Physics.TileCount, Timings.Steps[^1].Tiles);
    }

    [Fact]
    public void The_tiles_and_contact_pieces_are_timed_on_a_climb_within_their_parts()
    {
        // Every piece that runs on a climb from the pad (no tile is old enough to go in two
        // seconds, and a rocket climbing from the pad has every tile it needs made in the step,
        // none from the worker or a kept shape; the flight looks ahead for the flown vessel's
        // collisions ahead every step), and each part's pieces come to no more than the part
        // (and the rules', where a craft woken between steps lays its tiles).
        using var sim = new Simulation(Mission.ReachOrbit);
        Timings.Enabled = true;
        Fly(sim, 120);
        foreach (var detail in Enum.GetValues<StepDetail>().Where(d => d is not (StepDetail.Removals or StepDetail.Adds)))
            Assert.True(Timings.Steps.Sum(s => s[detail]) > 0, $"{detail} never timed");
        foreach (var part in new[] { StepPart.Tiles, StepPart.Contact })
            Assert.All(Timings.Steps, s => Assert.True(
                Enum.GetValues<StepDetail>().Where(d => Timings.PartOf(d) == part).Sum(d => s[d]) <= s[part] + s[StepPart.Rules] + 1e-6,
                $"{part}'s pieces came to more than it"));
    }

    [Fact]
    public void Asking_for_a_vessels_collisions_ahead_is_timed_within_the_rules_part()
    {
        // Asked in the flight's step (as the warning asks, from the rules), it is timed on its own
        // there, within the rules' time.
        var (sim, flown, _) = FastCollisionTests.Fly(FastCollisionTests.Pair(2000f, 5000f, 5f));
        using var _ = sim;
        var collisions = new Collisions(sim.Flight);
        Timings.Enabled = true;
        Timings.BeginStep();
        Assert.NotNull(collisions.Soonest(flown, sim.Flight.WorldVessels, Scene.CollisionHorizonSeconds));
        Timings.Lap(StepPart.Rules);
        Timings.EndStep(0);
        var s = Timings.Steps[^1];
        Assert.True(s[StepDetail.Collisions] > 0, "it was never timed");
        Assert.True(s[StepDetail.Collisions] <= s[StepPart.Rules]);
    }

    [Fact]
    public void A_flight_held_for_a_menu_takes_no_step_and_calls_for_no_sound()
    {
        // Paused, nothing moves: a step of no time would still judge every vessel's contact and
        // nearness (a third of a millisecond a frame, the panel showed) and change nothing.
        using var sim = new Simulation(Mission.ReachOrbit);
        var pilot = new OrbitAutopilot();
        for (var i = 0; i < 6000 && sim.Flight.Sounds.Length == 0; i++) // up to a step that called for a sound
            sim.Step(pilot.Fly(sim.Flight, Simulation.FrameSeconds));
        Assert.NotEqual(0, sim.Flight.Sounds.Length);
        var (time, position) = (sim.Flight.WorldTime, sim.Flight.Controlled.Position);
        Timings.Enabled = true;
        sim.Flight.Hold();
        Assert.Empty(Timings.Steps);
        Assert.Equal(0, sim.Flight.Sounds.Length); // none played again while it is held
        Assert.Equal(time, sim.Flight.WorldTime);
        Assert.Equal(position, sim.Flight.Controlled.Position);
    }

    [Fact]
    public void In_time_warp_each_step_is_a_sample()
    {
        using var sim = new Simulation(Mission.ReachOrbit);
        Timings.Enabled = true;
        sim.Flight.Advance(10 * Flight.WarpStepSeconds, default);
        Assert.Equal(10, Timings.Steps.Count);
    }

    [Fact]
    public void A_timestamp_that_wraps_its_valid_bits_still_gives_the_right_time()
    {
        Assert.Equal(0.00015, VulkanRenderer.TimestampMs((1UL << 36) - 100, 50, 1f, 36), 9);
        Assert.Equal(1.0, VulkanRenderer.TimestampMs(1000, 1_001_000, 1f, 64), 9);
        Assert.Equal(2.0, VulkanRenderer.TimestampMs(0, 1_000_000, 2f, 64), 9);
    }

    // The HUD at 1280x720 as the driver's shots have it: the credits bottom right, the instruments
    // top left, the compass top middle, a small view and its buttons bottom left; a briefing under
    // the compass; the mission panel top right, its bottom wherever its lines take it. The timings
    // panel 240 px wide, its lines 16 px apart with 12 px of frame: whole 12 lines (204 px),
    // shortened 9 (156 px).
    private static readonly Vector2 Screen = new(1280, 720);
    private static readonly RectangleF Credits = new(1043, 551, 223, 155);
    private static readonly RectangleF Instruments = new(14, 14, 282, 440);
    private static readonly RectangleF Compass = new(420, 14, 440, 56);
    private static readonly RectangleF Briefing = new(175, 80, 930, 196);
    private static readonly RectangleF SmallView = new(14, 470, 270, 236);

    private static RectangleF MissionTo(float bottom) => new(952, 14, 314, bottom - 14);

    private static (RectangleF Place, int Lines)? Place(RectangleF mission, RectangleF credits, params RectangleF[] avoid) =>
        Hud.PlaceTimings(Screen, 240f, 12, 9, 16f, 12f, mission, credits, avoid);

    [Fact]
    public void The_timings_panel_goes_under_the_mission_panel_whole_then_shortened_then_beside_it()
    {
        RectangleF[] hud = [Instruments, Compass, SmallView];

        var whole = Place(MissionTo(270), Credits, hud);
        Assert.Equal((new RectangleF(1026, 278, 240, 204), 12), whole); // under it, whole

        var shortened = Place(MissionTo(346), Credits, hud);
        Assert.Equal((new RectangleF(1026, 354, 240, 156), 9), shortened); // under it, shortened (204 would reach the credits)

        // Launched and staged: no room under it, so beside it, under the compass, whole.
        var beside = Place(MissionTo(403), Credits, hud);
        Assert.Equal((new RectangleF(704, 78, 240, 204), 12), beside);
        // And under a briefing, while one shows.
        var underBriefing = Place(MissionTo(403), Credits, [.. hud, Briefing]);
        Assert.Equal((new RectangleF(704, 284, 240, 204), 12), underBriefing);

        // Where nothing is in the way beside it, at the top (the small views up under the
        // instruments, so no room bottom left).
        Assert.Equal((new RectangleF(704, 14, 240, 204), 12), Place(MissionTo(403), Credits, Instruments, SmallView));
        // And with nothing anywhere, bottom left.
        Assert.Equal((new RectangleF(14, 502, 240, 204), 12), Place(MissionTo(403), Credits));

        // Not before the credits are laid out (the first frame), and not where nothing fits.
        Assert.Null(Place(MissionTo(270), RectangleF.Empty, hud));
        Assert.Null(Hud.PlaceTimings(new Vector2(500, 400), 240f, 12, 9, 16f, 12f, new RectangleF(186, 14, 300, 200), new RectangleF(263, 231, 223, 155), [Instruments]));
    }

    // The controls folded to their button, bottom left, with no small views up; and opened.
    private static readonly RectangleF ControlsFolded = new(14, 682, 100, 24);
    private static readonly RectangleF ControlsOpen = new(14, 300, 330, 406);

    [Fact]
    public void The_timings_panel_goes_bottom_left_over_the_folded_controls_first()
    {
        // Where the controls list opens, which is folded most of the time:
        // just over its button, whole where there is room under the instruments, else shortened.
        Assert.Equal((new RectangleF(14, 470, 240, 204), 12), Place(MissionTo(270), Credits, Instruments, Compass, ControlsFolded));
        var taller = new RectangleF(14, 14, 282, 470); // the instruments with more lines
        Assert.Equal((new RectangleF(14, 518, 240, 156), 9), Place(MissionTo(270), Credits, taller, Compass, ControlsFolded));

        // The list open, or the small views up, there: where it went before, under the mission panel.
        Assert.Equal((new RectangleF(1026, 278, 240, 204), 12), Place(MissionTo(270), Credits, Instruments, Compass, ControlsOpen));
        Assert.Equal((new RectangleF(1026, 278, 240, 204), 12), Place(MissionTo(270), Credits, Instruments, Compass, SmallView));
    }

    [Fact]
    public void In_a_small_window_the_timings_panel_goes_beside_the_credits_when_nothing_else_has_room()
    {
        // 800x600: the instruments down most of the left, the mission panel to 414.
        var screen = new Vector2(800, 600);
        var credits = new RectangleF(563, 431, 223, 155);
        var mission = new RectangleF(486, 14, 300, 400);
        var placed = Hud.PlaceTimings(screen, 240f, 12, 9, 16f, 12f, mission, credits, [Instruments, new RectangleF(180, 14, 440, 56)]);
        Assert.Equal((new RectangleF(315, 430, 240, 156), 9), placed);
    }

    [Fact]
    public void Wherever_it_goes_the_timings_panel_never_reaches_the_credits_the_mission_panel_or_the_rest_of_the_HUD()
    {
        foreach (var briefing in (RectangleF[])[RectangleF.Empty, Briefing])
            for (var bottom = 30f; bottom <= 540f; bottom += 1f)
            {
                var mission = MissionTo(bottom);
                RectangleF[] avoid = [Instruments, Compass, SmallView, briefing];
                var placed = Place(mission, Credits, avoid);
                // At 1280x720 there is always somewhere for it.
                Assert.True(placed is not null, $"nowhere for it with the mission panel to {bottom}");
                var (place, lines) = placed.Value;
                Assert.True(lines is 12 or 9);
                Assert.True(place.Left >= 14 && place.Top >= 14 && place.Right <= Screen.X - 14 && place.Bottom <= Screen.Y - 14, $"{place} off the screen");
                var room = RectangleF.Inflate(place, 7f, 7f);
                Assert.False(room.IntersectsWith(Credits), $"{place} reaches the credits (mission to {bottom})");
                Assert.False(room.IntersectsWith(mission), $"{place} reaches the mission panel (mission to {bottom})");
                foreach (var other in avoid)
                    Assert.False(!other.IsEmpty && room.IntersectsWith(other), $"{place} reaches {other} (mission to {bottom})");
                // Under the mission panel first, whenever it fits there (clear of a briefing reaching under it, too).
                var under = new RectangleF(Screen.X - 14 - 240, bottom + 8f, 240, 156);
                if (under.Bottom <= Credits.Top - 8f && !(briefing.IsEmpty ? false : RectangleF.Inflate(under, 7f, 7f).IntersectsWith(briefing)))
                    Assert.Equal((Screen.X - 14, bottom + 8f), (place.Right, place.Top));
            }
    }

    [Fact]
    public void The_timings_panel_is_judged_by_the_lines_it_will_draw_from_its_first_summary()
    {
        // From the first frame with a summary, by that summary's lines, not by "measuring..."'s one.
        Assert.Equal((1, 1), Hud.TimingsLineCounts(null));
        var withGpu = new FrameSample { TotalMs = 16, GpuMs = 4 };
        var withoutGpu = new FrameSample { TotalMs = 16, GpuMs = double.NaN };
        Assert.Equal((13, 9), Hud.TimingsLineCounts(TimingsSummary.Of([withGpu])));
        Assert.Equal((11, 9), Hud.TimingsLineCounts(TimingsSummary.Of([withoutGpu])));
    }

    [Fact]
    public void The_readout_has_every_number_of_the_timings_panel_whole()
    {
        // All of them, the GPU's passes and the world's counts too, whatever of the panel fits on
        // the screen: a script reads them from here, never off a screenshot.
        var frame = new FrameSample { TotalMs = 20, PhysicsSteps = 2, WarpAsked = 4f, WarpReached = 3.5f, GpuMs = 6, Awake = 3, Frozen = 7, Afloat = 1, Tiles = 12, UnderFormula = 5 };
        frame[StepPart.Jolt] = 1.5;
        frame[StepPart.Contact] = 0.25;
        frame[StepDetail.Adds] = 0.75; // a piece within the tiles' part (none here): not in the physics' sum
        frame[StepPart.Rules] = 0.5;
        frame[FramePart.Wait] = 9;
        frame[GpuPass.Scene] = 4;
        var shown = new Dictionary<string, string>();

        Readout.ReadTimings(TimingsSummary.Of([frame]), shown);

        Assert.Equal("20.00 ms", shown["Timings frame"]);
        Assert.Equal("50", shown["Timings fps"]);
        Assert.Equal("4x", shown["Timings warp asked"]);
        Assert.Equal("3.5x", shown["Timings warp reached"]);
        Assert.Equal("1.75 ms", shown["Timings physics"]); // Jolt, tiles, water, contact and the rest
        Assert.Equal("2", shown["Timings steps a frame"]);
        Assert.Equal("1.50 ms", shown["Timings Jolt"]);
        Assert.Equal("0.25 ms", shown["Timings contact and nearness"]);
        Assert.Equal("0.50 ms", shown["Timings rules"]);
        Assert.Equal("9.00 ms", shown["Timings waiting for the GPU"]);
        Assert.Equal("6.00 ms", shown["Timings GPU"]);
        Assert.Equal("4.00 ms", shown["Timings GPU scene"]);
        Assert.Equal("3", shown["Timings awake"]);
        Assert.Equal("7", shown["Timings frozen"]);
        Assert.Equal("1", shown["Timings afloat"]);
        Assert.Equal("12", shown["Timings ground tiles laid"]);
        Assert.Equal("5", shown["Timings under formula"]);
        Assert.Equal("0.75 ms", shown["Timings ground tiles: added"]); // laid from a shape made already: the worker's or a kept one
        Assert.Equal("0.00 ms", shown["Timings rules: collisions ahead"]); // a piece of the rules' (none here)
        Assert.All(shown.Keys, name => Assert.StartsWith("Timings ", name)); // apart from the flight's ("Frame" is the frame of reference)
        Assert.Equal(14, Enum.GetValues<StepDetail>().Length);
        Assert.Equal(43, shown.Count); // the panel's 29, and the 14 pieces of the tiles', the contact's and the rules'

        var noGpu = new FrameSample { TotalMs = 16, GpuMs = double.NaN };
        foreach (var pass in Enum.GetValues<GpuPass>())
            noGpu[pass] = double.NaN;
        shown.Clear();
        Readout.ReadTimings(TimingsSummary.Of([noGpu]), shown);
        Assert.Equal("n/a", shown["Timings GPU"]);
        Assert.Equal("n/a", shown["Timings GPU scene"]);
    }

    [Fact]
    public void A_frame_counts_the_world_s_crafts_and_tiles()
    {
        using var sim = new Simulation(Mission.FreePlay, world: WorldPresets.Satellites());
        Timings.Enabled = true;
        Fly(sim, 10);
        Timings.BeginFrame();
        Timings.CountWorld(sim.Worlds, sim.Flight.Unwatched.UnderFormula);
        Timings.EndFrame(1, 1);

        var f = Timings.Frames[^1];
        Assert.Equal(sim.Physics.Crafts.Count(c => !c.Removed && !c.Frozen), f.Awake);
        Assert.Equal(sim.Physics.Crafts.Count(c => !c.Removed && c.Frozen), f.Frozen);
        Assert.Equal(sim.Physics.TileCount, f.Tiles);
        // The preset's satellites far from the one flown, out of the physics: moved by formula.
        Assert.Equal(sim.Flight.WorldVessels.Count(v => v.OnFormula), f.UnderFormula);
        Assert.True(f.UnderFormula > 0, "none was under formula: this shows nothing of the count");
    }
}
