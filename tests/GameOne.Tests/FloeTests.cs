using System.Numerics;
using GameOne.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The floes in the icy seas' floe bands (<see cref="Floes"/>): laid out once, the same every run,
/// only over the band and never overlapping; each a raft riding the waves (<see cref="Raft"/>),
/// which a booster lands on as on the ground, rides, and is saved and loaded aboard; open water
/// between them; found wherever a point is over one, at its cell's edge as anywhere; and only as
/// many of them bodies at once as <see cref="Scene.MaxFloeBodies"/>, and none once every craft has gone.
/// </summary>
public sealed class FloeTests(ITestOutputHelper output)
{
    private static Vessel Dropped(Simulation sim) => sim.Flight.WorldVessels.First(v => v.Id == 0);

    private static readonly Pilot Nobody = (_, _) => default;

    /// <summary>Where the calm sea is in <paramref name="direction"/>.</summary>
    private static Vector3 Waterline(Vector3 direction) => Planet.Centre + direction * Planet.SeaRadius;

    /// <summary>The middle of Boreal's floe band, on the calm sea.</summary>
    private static Vector3 Band => Waterline(IceTests.FloeBand("Boreal Sea"));

    /// <summary>The middle of <paramref name="floe"/>'s top, as it is now.</summary>
    internal static Vector3 Top(Raft floe) => floe.Current.Position + floe.Normal * floe.Freeboard;

    /// <summary><paramref name="p"/> in <paramref name="floe"/>'s own frame, as it is now.</summary>
    private static Vector3 Local(Raft floe, Vector3 p) => Vector3.Transform(p - floe.Current.Position, Quaternion.Inverse(floe.Current.Rotation));

    /// <summary>How far a foot stands out from a booster's axis.</summary>
    private static float FootReach
    {
        get
        {
            var foot = RocketGeometry.Foot(LegGeometry.Rocket, 0);
            return MathF.Sqrt(foot.X * foot.X + foot.Z * foot.Z);
        }
    }

    /// <summary>The biggest floe within 300 m of <paramref name="sea"/>'s band point, set as it is at time 0 (the simulation's world starts then).</summary>
    internal static Raft BigFloe(string sea = "Boreal Sea")
    {
        Rafts.Time = 0.0;
        var floe = Floes.Near(Waterline(IceTests.FloeBand(sea)), 300f).MaxBy(f => f.HalfLength)!;
        floe.Set(0.0);
        return floe;
    }

    [Fact]
    public void Floes_lie_only_in_the_floe_band_and_never_overlap()
    {
        foreach (var sea in new[] { "Boreal Sea", "Austral Sea" })
        {
            var (centre, pack, band) = IceTests.Layout(sea);
            output.WriteLine($"{sea}: {Floes.Near(Waterline(centre), pack + band * 2f).Count()} floes");
        }
        output.WriteLine($"{Floes.Count} in the world");

        var near = Floes.Near(Band, 2000f).ToList();
        Assert.True(near.Count > 100, $"only {near.Count} floes near the band");
        foreach (var floe in near)
        {
            Assert.Equal(SurfaceKind.FloeField, Terrain.Kind(Planet.Up(floe.Centre)));
            Assert.Equal(RaftShape.Disc, floe.Shape);
            Assert.InRange(floe.HalfLength, Scene.FloeMinRadius, Scene.FloeMaxRadius);
            Assert.Equal(Scene.FloeFreeboard, floe.Freeboard);
            Assert.Equal(Scene.FloeThickness, floe.Freeboard + floe.Draft, 0.001f);
        }
        for (var i = 0; i < near.Count; i++)
            for (var j = i + 1; j < near.Count; j++)
            {
                var (a, b) = (near[i], near[j]);
                Assert.True(Vector3.Distance(a.Centre, b.Centre) > a.HalfLength + b.HalfLength, $"floes {a.Id} and {b.Id} overlap");
            }

        // Worked out again from nothing, it is the same layout: the same floes, numbered alike.
        var again = Floes.Lay();
        Assert.Equal(Floes.Count, again.Count);
        for (var i = 0; i < again.Count; i++)
        {
            var (a, b) = (Floes.All[i], again[i]);
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.Centre, b.Centre);
            Assert.Equal(a.HalfLength, b.HalfLength);
            Assert.Equal(a.Heading, b.Heading);
        }
    }

    [Fact]
    public void Boreals_waves_tilt_its_floes()
    {
        // Every floe within 500 m of the band point, every 2 s for two minutes: the waves under
        // them (Boreal's 2.4 m, halved over the band) heave and tilt them, a little.
        var (tilt, heave) = (0f, 0f);
        foreach (var floe in Floes.Near(Band, 500f))
        {
            var up = Planet.Up(floe.Centre);
            for (var t = 0.0; t < 120.0; t += 2.0)
            {
                var pose = floe.Pose(t);
                tilt = MathF.Max(tilt, float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Transform(Vector3.UnitY, pose.Rotation), up), -1f, 1f))));
                heave = MathF.Max(heave, MathF.Abs(Vector3.Dot(pose.Position - floe.Centre, up)));
            }
        }
        output.WriteLine($"the floes tilt up to {tilt:0.00}° and heave up to {heave:0.00} m");
        Assert.InRange(tilt, 0.1f, 10f);
        Assert.InRange(heave, 0.05f, 1.2f);
    }

    [Fact]
    public void A_booster_lands_on_a_floe_and_rides_it()
    {
        var floe = BigFloe();
        var at = Top(floe);
        using var sim = Drops.At(at, Planet.East(at), 1.5f);
        var booster = Dropped(sim);
        var tilt = 0f;
        float Tilt() => tilt = MathF.Max(tilt, float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(floe.Normal, Planet.Up(floe.Centre)), -1f, 1f))));
        Assert.True(sim.Run(Nobody, _ => { Tilt(); return booster.State != FlightState.Flying; }, maxSeconds: 60));
        var settled = sim.Time;
        output.WriteLine($"{booster.State} {booster.CrashReason} after {settled:0.0} s on a floe {floe.HalfLength:0.0} m round, touching down at {booster.TouchdownSpeed:0.00} m/s");
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.False(booster.LandedOnTarget);
        Assert.True(booster.TouchdownSpeed < Scene.SafeTouchdownSpeed);

        var onFloe = Local(floe, booster.Position);
        sim.Run(Nobody, _ => { Tilt(); return false; }, maxSeconds: 120);
        output.WriteLine($"{booster.State}, moved {Vector3.Distance(Local(floe, booster.Position), onFloe):0.000} m on the floe, "
            + $"{floe.HeightAbove(booster.Position):0.000} m above it, frozen {booster.Craft.Frozen}, aboard {booster.Craft.Aboard?.Raft.Id}; the floe tilted up to {tilt:0.00}°");
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(booster.Craft.Frozen);
        Assert.Same(floe, booster.Craft.Aboard?.Raft);
        Assert.True(Vector3.Distance(Local(floe, booster.Position), onFloe) < 0.5f);
        Assert.InRange(floe.HeightAbove(booster.Position)!.Value, -0.5f, 3f);
        Assert.True(tilt > 0.05f, $"the floe hardly tilted ({tilt}°): no waves under it?");
    }

    [Theory]
    [InlineData("Boreal Sea")]
    [InlineData("Austral Sea")]
    public void Landing_on_a_floe_is_not_touching_the_water(string sea)
    {
        // The floe's top is only its freeboard over the calm sea, and the waves under its rim are
        // not the plane it sits at, so its feet and body there are often under the water's
        // height: but over the floe they are on it, not in the sea. No splash, never in the water
        // (which would cut its steering and keep it settling), and its touchdown speed the touchdown's.
        var floe = BigFloe(sea);
        var at = Top(floe);
        using var sim = Drops.At(at, Planet.East(at), 1.5f);
        var booster = Dropped(sim);
        var (splashes, wet, touchdown) = (0, 0, (float?)null);
        var changed = 0f;
        Assert.True(sim.Run(Nobody, f =>
        {
            foreach (var sound in f.Sounds)
                if (sound.Cue == SoundCue.Splash)
                    splashes++;
            if (booster.Craft.Submerged > 0f || booster.Craft.Settling)
                wet++;
            if (touchdown is null && booster.TouchdownSpeed > 0f)
                touchdown = booster.TouchdownSpeed;
            else if (touchdown is { } speed)
                changed = MathF.Max(changed, MathF.Abs(booster.TouchdownSpeed - speed));
            return booster.Craft.Frozen || booster.State == FlightState.Crashed;
        }, maxSeconds: 120));
        output.WriteLine($"{booster.State} {booster.CrashReason} after {sim.Time:0.0} s: {splashes} splashes, {wet} steps in the water, "
            + $"touching down at {touchdown:0.00} m/s, and that changed by up to {changed:0.00} m/s after");
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.Equal(0, splashes);
        Assert.Equal(0, wet);
        Assert.NotNull(touchdown);
        Assert.Equal(0f, changed);
    }

    [Fact]
    public void Landed_on_a_floe_it_comes_back_aboard()
    {
        var floe = BigFloe();
        var at = Top(floe);
        using var sim = Drops.At(at, Planet.East(at), 1.5f);
        var booster = Dropped(sim);
        Assert.True(sim.Run(Nobody, _ => booster.Craft.Frozen, maxSeconds: 120));
        var onFloe = Local(floe, booster.Position);
        using var loaded = sim.Reloaded();
        var again = Dropped(loaded);
        Assert.Equal(FlightState.Landed, again.State);
        Assert.True(again.Craft.Frozen);
        Assert.Same(floe, again.Craft.Aboard?.Raft);
        float worst = 0f;
        loaded.Run(Nobody, _ => { worst = MathF.Max(worst, Vector3.Distance(Local(floe, loaded.Physics.Position(again.Craft)), onFloe)); return false; }, maxSeconds: 60);
        output.WriteLine($"{again.State} {again.CrashReason}, worst {worst:0.0000} m on the floe, frozen {again.Craft.Frozen}");
        Assert.Equal(FlightState.Landed, again.State);
        Assert.True(again.Craft.Frozen);
        Assert.True(worst < 0.05f, $"moved {worst} m on the floe");
    }

    [Fact]
    public void Beside_a_floe_it_splashes_down()
    {
        // Open water in the band: no floe's rim within 12 m (the feet reach 5 m out).
        Rafts.Time = 0.0;
        var band = Band;
        var (east, north) = (Planet.East(band), Planet.North(band));
        Vector3? open = null;
        for (var r = 0f; r < 200f && open is null; r += 5f)
            for (var k = 0; k < 16 && open is null; k++)
            {
                var (s, c) = MathF.SinCos(MathF.Tau * k / 16);
                var p = Waterline(Planet.Up(band + (east * c + north * s) * r));
                if (Terrain.Kind(Planet.Up(p)) == SurfaceKind.FloeField && Floes.Near(p, 12f).All(f => Vector3.Distance(f.Centre, p) - f.HalfLength > 12f))
                    open = p;
            }
        Assert.NotNull(open);
        using var sim = Drops.At(open.Value, Planet.East(open.Value), 1.5f);
        var booster = Dropped(sim);
        Assert.True(sim.Run(Nobody, _ => booster.State != FlightState.Flying, maxSeconds: 120));
        output.WriteLine($"{booster.State} {booster.CrashReason} after {sim.Time:0.0} s");
        Assert.Equal(FlightState.Floating, booster.State);
    }

    [Fact]
    public void Falling_on_its_side_across_a_small_floe_its_body_is_judged()
    {
        // A small floe (a little over FloeMinRadius round) with open water all round it, and a
        // booster (legs stowed) lying level over it, the floe under the point a quarter of its
        // length up from its base: its two ends and its middle are out over the sea. Falling
        // side-on at 9 m/s (over the 6 its body takes, under the 15 that stops anything dead), it
        // comes down on the floe on its body: that is a touchdown, judged as any other.
        Rafts.Time = 0.0;
        var length = RocketGeometry.StackHeight(Vehicle.Booster, 0, 0) + Vehicle.Booster.Stages[0].BellHeight;
        var bell = Vehicle.Booster.Stages[0].BellHeight;
        var radius = Vehicle.Booster.Stages[0].Radius;
        var floes = Floes.Near(Band, 1500f).ToList();
        var floe = floes.Where(f => f.HalfLength < Scene.FloeMinRadius + 0.6f)
            .Where(f => floes.All(o => o == f || Vector3.Distance(o.Centre, f.Centre) - o.HalfLength > length + 4f))
            .OrderBy(f => f.HalfLength).First();
        floe.Set(0.0);
        var along = floe.Heading;
        var over = Top(floe) + floe.Normal * (radius + 0.3f); // its axis there, its underside 0.3 m over the floe
        var basePoint = over - along * (length / 4f - bell); // the bottom of its body: its bell's bottom is a quarter of its length back from the floe's middle
        var lying = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(floe.Normal, along)), MathF.PI / 2) * Planet.UprightAt(over));
        Assert.True(Vector3.Dot(Vector3.Transform(Vector3.UnitY, lying), along) > 0.99f);
        var state = Drops.Placed(over, along, 0f, legsOut: false) with
        {
            Position = basePoint, Rotation = lying, Velocity = floe.VelocityAt(over) - floe.Normal * 9f, Aim = Vector3.Transform(Vector3.UnitY, lying),
        };
        using var sim = new Simulation(world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var booster = Dropped(sim);
        output.WriteLine($"floe {floe.Id}, {floe.HalfLength:0.0} m round");
        foreach (var t in (ReadOnlySpan<float>)[0f, 0.5f, 1f])
            Assert.Null(Rafts.Under(Vector3.Transform(new Vector3(0, -bell + length * t, 0), booster.Model))); // its ends and middle are over the sea
        // Watched, so it comes down in the physics, whose judging of its body this is.
        sim.Flight.BringIn(booster); // (far from the pad, it loads under formula)
        var watched = new[] { booster };
        sim.Run((_, _) => new FlightInput(Watched: watched), _ =>
        {
            Assert.False(booster.OnFormula);
            return booster.State == FlightState.Crashed;
        }, maxSeconds: 1);
        output.WriteLine($"{booster.State} {booster.CrashReason}");
        Assert.StartsWith("Hit the ground at", booster.CrashReason);
    }

    [Fact]
    public void A_floe_is_found_at_its_cells_edge()
    {
        // Among the floes near the band, the rim point nearest the edge of its floe's cell.
        Rafts.Time = 41.0; // the waves tilting them
        var (best, outward, gap) = ((Raft?)null, Vector3.Zero, float.MaxValue);
        foreach (var floe in Floes.Near(Band, 400f))
        {
            var up = Planet.Up(floe.Centre);
            var across = Vector3.Cross(floe.Heading, up);
            var cell = Floes.CellOf(up);
            for (var k = 0; k < 64; k++)
            {
                var (s, c) = MathF.SinCos(MathF.Tau * k / 64);
                var way = floe.Heading * c + across * s;
                for (var t = 0f; t < gap && t < 8f; t += 0.05f)
                    if (Floes.CellOf(Planet.Up(floe.Centre + way * (floe.HalfLength + t))) != cell)
                    {
                        (best, outward, gap) = (floe, way, t);
                        break;
                    }
            }
        }
        Assert.NotNull(best);
        output.WriteLine($"floe {best.Id}, {best.HalfLength:0.0} m round: its rim is {gap:0.00} m from its cell's edge");

        // Every floe near the band is found all round its rim, just inside it, from its top to
        // 45 m up along its tilted normal (which leans a point that high out over the next cell),
        // as the floe is now; and just outside its rim it isn't.
        var (points, beyond) = (0, 0);
        foreach (var floe in Floes.Near(Band, 400f))
        {
            var pose = Rafts.Now(floe).Current;
            var (x, z) = (Vector3.Transform(Vector3.UnitX, pose.Rotation), Vector3.Transform(Vector3.UnitZ, pose.Rotation));
            var cell = Floes.CellOf(Planet.Up(floe.Centre));
            for (var k = 0; k < 32; k++)
            {
                var (s, c) = MathF.SinCos(MathF.Tau * k / 32);
                var way = x * c + z * s;
                foreach (var h in (ReadOnlySpan<float>)[0.05f, 2f, 10f, 25f, 45f])
                {
                    var p = Top(floe) + way * (floe.HalfLength - 0.1f) + floe.Normal * h;
                    Assert.Same(floe, Rafts.Under(p)?.Raft);
                    points++;
                    if (Floes.CellOf(Planet.Up(p)) != cell)
                        beyond++;
                }
                Assert.NotSame(floe, Rafts.Under(Top(floe) + way * (floe.HalfLength + 0.3f) + floe.Normal)?.Raft);
            }
        }
        output.WriteLine($"{points} points over floes' rims found, {beyond} of them over the next cell");

        // High enough over a floe tilted far enough, a point over its rim is over the next cell,
        // which is why the cells round its own are searched too. Among every floe in both bands
        // near its cell's edge (a large one is kept just Gap, a metre, from it), each way to its
        // nearest edges, the floe and moment (in ten minutes of waves) a point 49.4 m up over its
        // rim (under the 50 m a raft reaches) leans furthest out over that edge: it is over the
        // next cell, and found, all the way up there.
        // (Each floe's edges are found first, and its poses worked out once a moment for all of
        // them: the floes are thousands, so the search is the test's cost, over 7 s beside the
        // other shards when each way asked its own poses and walked to every edge in steps.)
        var (leaner, leanWay, when, reach) = ((Raft?)null, Vector3.Zero, 0.0, float.MinValue);
        Span<Vector3> ways = stackalloc Vector3[4];
        Span<float> edges = stackalloc float[4];
        foreach (var floe in Floes.All)
        {
            var up = Planet.Up(floe.Centre);
            var cell = Floes.CellOf(up);
            var across = Vector3.Cross(floe.Heading, up);
            var near = 0;
            foreach (var way in (ReadOnlySpan<Vector3>)[floe.Heading, -floe.Heading, across, -across])
            {
                // A cell is convex, so a way still in it at the last step looked at (1.48 m out) never left it.
                if (Floes.CellOf(Planet.Up(floe.Centre + way * (floe.HalfLength + 1.48f))) == cell)
                    continue; // not near this edge
                var t = 0f;
                while (t < 1.5f && Floes.CellOf(Planet.Up(floe.Centre + way * (floe.HalfLength + t))) == cell)
                    t += 0.02f;
                (ways[near], edges[near]) = (way, t);
                near++;
            }
            if (near == 0)
                continue;
            for (var time = 0.0; time < 600.0; time += 1.0)
            {
                var normal = Vector3.Transform(Vector3.UnitY, floe.Pose(time).Rotation);
                for (var w = 0; w < near; w++)
                {
                    var past = Vector3.Dot(normal, ways[w]) * 49.4f - edges[w]; // how far past the edge the point leans, about
                    if (past > reach)
                        (leaner, leanWay, when, reach) = (floe, ways[w], time, past);
                }
            }
        }
        Assert.NotNull(leaner);
        Rafts.Time = when;
        Rafts.Now(leaner);
        var over = 0;
        var leaning = Vector3.Normalize(leanWay - leaner.Normal * Vector3.Dot(leanWay, leaner.Normal));
        for (var h = 0.05f; h < 49.5f; h += 0.5f)
        {
            var p = Top(leaner) + leaning * (leaner.HalfLength - 0.1f) + leaner.Normal * h;
            Assert.Same(leaner, Rafts.Under(p)?.Raft);
            if (Floes.CellOf(Planet.Up(p)) != Floes.CellOf(Planet.Up(leaner.Centre)))
            {
                Assert.Same(leaner, Floes.Under(p)?.Raft);
                over++;
            }
        }
        output.WriteLine($"floe {leaner.Id}, {leaner.HalfLength:0.0} m round, leaning out {float.RadiansToDegrees(MathF.Asin(Vector3.Dot(leaner.Normal, leanWay))):0.00}° "
            + $"over its cell's edge at {when:0} s (about {reach:0.00} m past it at the top): {over} points over its rim are over the next cell, and found");
        Assert.True(over > 0, "no point over a floe's rim was over the next cell: the search of the cells round it went untried");

        // And a booster set down with a foot just inside the rim there lands on it.
        Rafts.Time = 0.0;
        best.Set(0.0);
        var along = Vector3.Normalize(outward - best.Normal * Vector3.Dot(outward, best.Normal));
        var at = Top(best) + along * (best.HalfLength - FootReach - 0.5f);
        using var sim = Drops.At(at, outward, 1.5f);
        var booster = Dropped(sim);
        Assert.True(sim.Run(Nobody, _ => booster.Craft.Frozen || booster.State == FlightState.Crashed, maxSeconds: 120));
        output.WriteLine($"{booster.State} {booster.CrashReason} after {sim.Time:0.0} s, aboard {booster.Craft.Aboard?.Raft.Id}");
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.Same(best, booster.Craft.Aboard?.Raft);
    }

    [Fact]
    public void Flying_over_the_floe_band_the_floe_bodies_stay_capped()
    {
        // The flown booster stands on the sheet, far from the floes. Twenty-four more, all round
        // Boreal's band, 50 m up and flying along it at 40 m/s, between them want more floes than
        // the cap: the nearest are bodies, never more than the cap; and once they have gone, none.
        var (centre, _, _) = IceTests.Layout("Boreal Sea");
        var sheet = IceTests.SheetMiddle("Boreal Sea");
        var ground = Planet.Centre + sheet * (Planet.SeaRadius + Terrain.Height(sheet));
        using var sim = Drops.At(ground, Planet.East(ground), 1f);
        var outFrom = Planet.Radius * Planet.AngleBetween(Waterline(centre), Band);
        var loose = new List<Physics.Craft>();
        var wanted = new HashSet<Raft>();
        for (var k = 0; k < 24; k++)
        {
            var p = Waterline(IceTests.Travel(centre, k * 15f, outFrom));
            var up = Planet.Up(p);
            var along = Vector3.Normalize(Vector3.Cross(up, Vector3.Normalize(p - Waterline(centre))));
            loose.Add(sim.Physics.AddLoose(Vehicle.Booster, p + up * 50f, Planet.UprightAt(p), along * 40f, Vector3.Zero));
            foreach (var floe in Floes.Near(p + up * 50f, Scene.FloeReach))
                wanted.Add(floe);
        }
        output.WriteLine($"{wanted.Count} floes within reach of the boosters at first");
        Assert.True(wanted.Count > Scene.MaxFloeBodies);

        var most = 0;
        sim.Run(Nobody, _ => { most = Math.Max(most, sim.Physics.FloeBodyCount); return false; }, maxSeconds: 2);
        output.WriteLine($"at most {most} floe bodies at once");
        Assert.Equal(Scene.MaxFloeBodies, most);

        foreach (var craft in loose)
            sim.Physics.Remove(craft);
        var gone = sim.Time;
        Assert.True(sim.Run(Nobody, _ => sim.Physics.FloeBodyCount == 0, maxSeconds: 10));
        output.WriteLine($"none {sim.Time - gone:0.00} s after the boosters went");
    }

    [Fact]
    public void The_drawn_floe_is_the_collided_floe()
    {
        // The floe's mesh, posed as it is drawn, lies within the cylinder its hull is (its radius
        // round, from its draft under the waterline to its freeboard over it), and reaches its rim,
        // its top and its bottom: what is drawn is what is collided with.
        var floe = BigFloe();
        var model = Meshes.FloeModel(floe, floe.Current);
        var (top, bottom, rim) = (float.MinValue, float.MaxValue, 0f);
        foreach (var vertex in Meshes.BuildFloe())
        {
            var local = Local(floe, Vector3.Transform(new Vector3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z), model));
            var across = MathF.Sqrt(local.X * local.X + local.Z * local.Z);
            Assert.True(across <= floe.HalfLength + 0.01f, $"a vertex {across:0.000} m out, past the floe's {floe.HalfLength:0.000} m rim");
            (top, bottom, rim) = (MathF.Max(top, local.Y), MathF.Min(bottom, local.Y), MathF.Max(rim, across));
        }
        output.WriteLine($"a floe {floe.HalfLength:0.00} m round: drawn {rim:0.000} m round, from {bottom:0.000} to {top:0.000} m");
        Assert.Equal(Scene.FloeFreeboard, top, 0.01f);
        Assert.Equal(Scene.FloeFreeboard - Scene.FloeThickness, bottom, 0.01f);
        Assert.Equal(floe.HalfLength, rim, 0.01f);

        // Its drawn top's middle is where a craft over it stands.
        var middle = Vector3.Transform(Vector3.Zero, model);
        Assert.Equal(0f, floe.HeightAbove(middle)!.Value, 0.01f);
    }

    [Fact]
    public void The_far_tint_is_as_much_ice_as_the_floes_are()
    {
        // Beyond Scene.FloeDrawDistance the band's water is tinted towards the ice by Floes.Cover,
        // in place of the floes: so it must be how much of the band the floes cover, or the band
        // would brighten or darken as they come into range. Measured over a circle in the band.
        const float within = 150f;
        var floes = Floes.Near(Band, within).Where(f => Vector3.Distance(f.Centre, Band) <= within).ToList();
        var cover = floes.Sum(f => MathF.PI * f.HalfLength * f.HalfLength) / (MathF.PI * within * within);
        output.WriteLine($"{floes.Count} floes cover {cover:P1} of {within} m round the band's middle; the tint takes {Floes.Cover:P1}");
        Assert.InRange(cover, Floes.Cover * 0.7f, Floes.Cover * 1.3f);
        output.WriteLine($"{Floes.Near(Band + Planet.Up(Band) * 30f, Scene.FloeDrawDistance).Count()} floes are drawn from 30 m over it ({Floes.Count} in the world)");
    }

    [Fact]
    public void A_floe_reads_the_sea_as_the_sea_is()
    {
        // A raft keeps the waves under its points taken apart (Sea.Terms), so each pose is a sum:
        // the floes in sight are posed every frame to be drawn. It is the sea all the same.
        var floe = BigFloe();
        Span<float> phases = stackalloc float[Sea.WaveCount], sines = stackalloc float[Sea.WaveCount], cosines = stackalloc float[Sea.WaveCount];
        var worst = 0f;
        foreach (var direction in new[] { Planet.Up(floe.Centre), Planet.Up(floe.Centre + floe.Heading * floe.HalfLength), Planet.Up(Band) })
        {
            Sea.Terms(direction, sines, cosines);
            for (var t = 0.0; t < 60.0; t += 1.7)
            {
                Sea.Phases(t, phases);
                var sum = 0f;
                for (var i = 0; i < Sea.WaveCount; i++)
                    sum += sines[i] * MathF.Cos(phases[i]) - cosines[i] * MathF.Sin(phases[i]);
                worst = MathF.Max(worst, MathF.Abs(sum - Sea.Height(direction, phases)));
            }
        }
        output.WriteLine($"at most {worst * 1000f:0.000} mm apart");
        Assert.True(worst < 1e-3f, $"{worst} m apart");

        // Drawn as the physics has it: a floe not kept up to date is drawn where Rafts.Now would set it.
        Rafts.Time = 12.3;
        var drawn = Rafts.PoseNow(floe);
        Assert.Equal(drawn, Rafts.Now(floe).Current);
        Rafts.Time = 0.0;
    }
}
