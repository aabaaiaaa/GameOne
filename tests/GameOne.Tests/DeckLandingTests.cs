using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// Landing on the drone ship's moving deck (see <see cref="Rafts.Ship"/>): the deck is the ground
/// over it, a touchdown is judged against the deck's own movement, a booster standing on it rides
/// with it (at rest, it freezes there and is carried, and is saved and loaded aboard), and off its
/// target circle or beside the ship it is off target.
/// </summary>
public sealed class DeckLandingTests(ITestOutputHelper output)
{
    /// <summary>
    /// The Reach Orbit mission's booster (its own vessel, stage 0, of the flight the simulation
    /// starts, so its landing site is the drone ship), dropped onto the deck at
    /// <paramref name="offset"/> from its centre (in the ship's frame), falling at
    /// <paramref name="speed"/>. With <paramref name="matchDeck"/>, it moves as the deck does
    /// there instead, and starts with its lowest foot already touching: set down at the deck's own speed.
    /// </summary>
    private static Simulation DropOnDeck(Vector3 offset, float speed, bool matchDeck = false, int last = 0)
    {
        Rafts.Ship.Set(0.0); // the simulation's world starts at time 0, so the deck is lowered onto as it is then
        var at = Rafts.Ship.Current.Position + Rafts.Ship.Normal * Scene.DroneShipFreeboard + Vector3.Transform(offset, Rafts.Ship.Current.Rotation);
        var sideways = Vector3.Zero;
        if (matchDeck)
        {
            var deck = Rafts.Ship.VelocityAt(at);
            var up = Planet.Up(at);
            speed = -Vector3.Dot(deck, up);
            sideways = deck - up * Vector3.Dot(deck, up);
        }
        return Drops.At(at, Planet.East(at), speed, Vehicle.TwoStage, sideways, first: 0, last: last, mission: Mission.ReachOrbit, flight: 1,
            // Matched, a centimetre up: its touch is judged as its foot touches the deck, so a fall of
            // 5 cm (as this was) would itself touch it at about 1 m/s, √(2 g 0.05).
            clearance: matchDeck ? 0.01f : Drops.Clearance);
    }

    private static Vessel Dropped(Simulation sim) => sim.Flight.WorldVessels.First(v => v.Id == 0);

    private static readonly Pilot Nobody = (_, _) => default;

    /// <summary><paramref name="p"/> in the ship's own frame, as it is now.</summary>
    private static Vector3 DeckLocal(Vector3 p) => Vector3.Transform(p - Rafts.Ship.Current.Position, Quaternion.Inverse(Rafts.Ship.Current.Rotation));

    [Fact]
    public void A_booster_set_down_on_the_deck_lands_on_target()
    {
        using var sim = DropOnDeck(offset: Vector3.Zero, speed: 2f);
        var booster = Dropped(sim);
        Assert.True(sim.Run(Nobody, _ => booster.State != FlightState.Flying, maxSeconds: 60));
        output.WriteLine($"{booster.State} {booster.CrashReason}, touchdown {booster.TouchdownSpeed:0.00} m/s, {booster.DistanceToLandingSite:0.0} m off");
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(booster.LandedOnTarget);
        Assert.True(booster.TouchdownSpeed < Scene.SafeTouchdownSpeed);
    }

    [Fact]
    public void A_booster_lands_on_Sentinels_drone_ship()
    {
        var site = LaunchSites.All.Single(s => s.Name == "Sentinel");
        var ship = Rafts.Ships.Single(s => Vector3.Distance(s.Centre, site.DroneShips[0]) < 1f);
        ship.Set(0.0);
        var at = ship.Current.Position + ship.Normal * Scene.DroneShipFreeboard;
        var mission = new Mission { Name = "TEST", Briefing = "", Vehicle = Vehicle.TwoStage, Steps = [], LandingSites = [site.DroneShips[0]] };
        using var sim = Drops.At(at, Planet.East(at), 2f, Vehicle.TwoStage, first: 0, last: 0, mission: mission, flight: 1);
        var booster = Dropped(sim);
        Assert.True(sim.Run(Nobody, _ => booster.State != FlightState.Flying, maxSeconds: 60));
        output.WriteLine($"{booster.State} {booster.CrashReason}, touchdown {booster.TouchdownSpeed:0.00} m/s, {booster.DistanceToLandingSite:0.0} m off, {ship.HeightAbove(booster.Position):0.00} m over the deck");
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(booster.LandedOnTarget);
        Assert.InRange(ship.HeightAbove(booster.Position)!.Value, -0.5f, 3f); // on the deck, not under it or fallen off
    }

    [Fact]
    public void Touchdown_is_judged_against_the_moving_deck()
    {
        // Set down a little faster than the deck moves (0.6 m/s down against it, from a centimetre
        // up), where and when the deck falls fastest: on the ship in the roughest sea, 16 m
        // forward of its middle (its pitch adds to its heave there), in its first ten minutes.
        // Judged against the deck, it is a touch under 1 m/s, and slower by the deck's own fall
        // than its speed over the ground. (No deck moves fast enough, a quarter of a metre a second
        // at most, for that difference alone to cross the limit: it is checked as it is.)
        var ship = Rafts.Ships.MaxBy(s => Sea.WaveScale(Planet.Up(s.Centre)))!;
        var offset = new Vector3(16f, 0f, 0f);
        Vector3 DeckAt() => ship.Current.Position + ship.Normal * Scene.DroneShipFreeboard + Vector3.Transform(offset, ship.Current.Rotation);
        var (time, falling) = (0.0, 0f);
        for (var t = 0.0; t < 600.0; t += 0.05)
        {
            ship.Set(t);
            var down = -Vector3.Dot(ship.VelocityAt(DeckAt()), Planet.Up(DeckAt()));
            if (down > falling)
                (time, falling) = (t, down);
        }
        time -= 0.05; // it falls the centimetre for about that long
        ship.Set(time);
        var at = DeckAt();
        var (up, deck) = (Planet.Up(at), ship.VelocityAt(at));
        var state = Drops.Placed(at, Planet.East(at), -Vector3.Dot(deck, up) + 0.6f, Vehicle.TwoStage, deck - up * Vector3.Dot(deck, up),
            first: 0, last: 0, mission: Mission.ReachOrbit, flight: 1, clearance: 0.01f);
        using var sim = new Simulation(Mission.ReachOrbit, world: new WorldState { Vessels = [state], NextVesselId = 1, Time = time });
        var booster = Dropped(sim);
        sim.Step(new FlightInput(SwitchTo: booster));
        var (moving, againstGround) = (0f, 0f);
        Assert.True(sim.Run(Nobody, _ =>
        {
            if (booster.TouchdownSpeed == 0f)
                moving = booster.Velocity.Length(); // its own speed over the ground, as it comes down
            else if (againstGround == 0f)
                againstGround = moving;
            return booster.State != FlightState.Flying;
        }, maxSeconds: 60));
        output.WriteLine($"the deck falling at up to {falling:0.000} m/s at {time + 0.05:0.00} s; {booster.State} {booster.CrashReason}, "
            + $"touchdown {booster.TouchdownSpeed:0.000} m/s against the deck, {againstGround:0.000} m/s over the ground");
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(falling > 0.2f, $"the deck falls at only {falling} m/s: too slow to tell");
        Assert.True(againstGround - booster.TouchdownSpeed > 0.1f, $"touched down at {booster.TouchdownSpeed} m/s, coming down at {againstGround} m/s over the ground: not against the deck");
        Assert.True(booster.TouchdownSpeed < 1f, $"touched down at {booster.TouchdownSpeed} m/s");
    }

    [Fact]
    public void Standing_on_the_deck_it_rides_with_it()
    {
        // Landed and left: two minutes on, it is still on its feet on the deck, where it came down (to 0.5 m), and still Landed.
        using var sim = DropOnDeck(offset: Vector3.Zero, speed: 1f);
        var booster = Dropped(sim);
        sim.Run(Nobody, _ => booster.State != FlightState.Flying, maxSeconds: 60);
        var onDeck = DeckLocal(booster.Position);
        sim.Run(Nobody, _ => false, maxSeconds: 120);
        output.WriteLine($"{booster.State}, moved {Vector3.Distance(DeckLocal(booster.Position), onDeck):0.000} m on the deck, "
            + $"{Rafts.Ship.HeightAbove(booster.Position):0.000} m above it, frozen {booster.Craft.Frozen}");
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(Vector3.Distance(DeckLocal(booster.Position), onDeck) < 0.5f);
        Assert.InRange(Rafts.Ship.HeightAbove(booster.Position)!.Value, -0.5f, 3f); // its base on the deck, not under it or fallen off
    }

    [Fact]
    public void Staged_frozen_on_the_deck_both_parts_come_apart_moving_with_it()
    {
        // The whole rocket set down on the deck and frozen there (carried by the ship, so its body is
        // still): staged there, the booster is left standing on the deck, and neither part starts
        // further off the deck's own motion than the push apart (the deck moves at a few cm/s).
        using var sim = DropOnDeck(offset: Vector3.Zero, speed: 1f, last: 1);
        var rocket = Dropped(sim);
        sim.Run(Nobody, _ => rocket.Craft.Frozen, maxSeconds: 120);
        Assert.Equal(FlightState.Landed, rocket.State);
        Assert.True(rocket.Craft.Frozen);
        sim.Step(new FlightInput(CutThrottle: true, Stage: true));
        var parts = sim.Flight.WorldVessels.Where(v => v.Vehicle == Vehicle.TwoStage && v.State != FlightState.Ready).ToArray(); // not the flight's own rocket, on the pad
        Assert.Equal(2, parts.Length);
        var booster = parts.Single(v => v.Stage == 0);
        var deck = Rafts.Ship.VelocityAt(sim.Physics.CentreOfMass(booster.Craft));
        var off = (sim.Physics.Velocity(booster.Craft) - deck).Length();
        output.WriteLine($"deck {deck.Length():0.00} m/s; the booster {off:0.00} m/s off it as it comes apart");
        Assert.True(off < Scene.SeparationSpeed, $"the booster started {off:0.00} m/s off the deck's motion");
        // The upper stage, touching nothing (its bell lifted clear of the booster), is pushed off
        // along its axis, and across it moves as the deck does there, not still: measured a frame
        // on (the step that staged it), so along its axis it has lost a frame's fall, and across it
        // 0.003 m/s was measured (left still, it was 0.034 m/s off, the deck's own speed across it). Where on
        // it the deck's motion is taken, before that frame, is the next test's.
        var upper = parts.Single(v => v.Stage == 1);
        var axis = sim.Physics.Up(upper.Craft);
        var under = Rafts.Ship.VelocityAt(sim.Physics.CentreOfMass(upper.Craft));
        var relative = sim.Physics.Velocity(upper.Craft) - under;
        var across = (relative - axis * Vector3.Dot(relative, axis)).Length();
        var along = Vector3.Dot(relative, axis);
        output.WriteLine($"the deck under the upper stage {under.Length():0.0000} m/s ({(under - axis * Vector3.Dot(under, axis)).Length():0.0000} across its axis); "
            + $"the upper stage {across:0.0000} m/s across its axis off it and {along:+0.000;-0.000} m/s along it");
        Assert.True(across < 0.01f, $"the upper stage started {across:0.0000} m/s across its axis off the deck's motion");
        var fall = Scene.SurfaceGravity * Simulation.FrameSeconds;
        Assert.InRange(along, Scene.SeparationSpeed / 2 - fall - 0.01f, Scene.SeparationSpeed / 2 + 0.01f); // the push apart, against the deck
        sim.Run(Nobody, _ => false, maxSeconds: 20);
        output.WriteLine($"booster {booster.State} {booster.CrashReason}");
        Assert.Equal(FlightState.Landed, booster.State);
    }

    [Fact]
    public void Separated_frozen_on_the_deck_each_part_starts_as_the_deck_moves_at_its_centre_of_mass()
    {
        // The physics' own separation, read before any step: each part moves as the deck does at
        // its centre of mass (Jolt's velocity is the centre of mass's), plus the push apart along
        // its axis. At its base instead it would be off by the deck's turning across the gap
        // (measured 0.003 m/s here), which a frame of falling would hide.
        using var sim = DropOnDeck(offset: Vector3.Zero, speed: 1f, last: 1);
        var rocket = Dropped(sim);
        sim.Run(Nobody, _ => rocket.Craft.Frozen, maxSeconds: 120);
        Assert.True(rocket.Craft.Frozen);
        var (dropped, rest) = sim.Physics.Separate(rocket.Craft, atRest: true)!.Value;
        foreach (var (part, push) in new[] { (dropped, -0.5f), (rest, 0.5f) })
        {
            var centre = sim.Physics.CentreOfMass(part);
            var expected = Rafts.Ship.VelocityAt(centre) + sim.Physics.Up(part) * (Scene.SeparationSpeed * push);
            var (off, atBase) = ((sim.Physics.Velocity(part) - expected).Length(), (Rafts.Ship.VelocityAt(sim.Physics.Position(part)) - Rafts.Ship.VelocityAt(centre)).Length());
            output.WriteLine($"stages {part.First} to {part.Last}: {off:0.000000} m/s off the deck's motion at its centre of mass (the deck at its base is {atBase:0.0000} m/s off that)");
            Assert.True(atBase > 0.001f, $"the deck turns too slowly here ({atBase:0.0000} m/s across the gap) for the test to tell");
            Assert.True(off < 0.0005f, $"stages {part.First} to {part.Last} started {off:0.0000} m/s off the deck's motion at its centre of mass");
            Assert.Equal(Rafts.Ship.AngularVelocity, sim.Physics.AngularVelocity(part));
        }
    }

    [Fact]
    public void Frozen_on_the_deck_it_is_carried_with_it()
    {
        using var sim = DropOnDeck(offset: Vector3.Zero, speed: 1f);
        var booster = Dropped(sim);
        sim.Run(Nobody, _ => booster.Craft.Frozen, maxSeconds: 120);
        output.WriteLine($"frozen {booster.Craft.Frozen} after {sim.Time:0.0} s, {booster.State}");
        Assert.True(booster.Craft.Frozen);                         // at rest in the deck's frame, so it freezes
        var onDeck = DeckLocal(booster.Position);
        float worst = 0f;
        sim.Run(Nobody, _ => { worst = MathF.Max(worst, Vector3.Distance(DeckLocal(sim.Physics.Position(booster.Craft)), onDeck)); return false; }, maxSeconds: 60);
        output.WriteLine($"frozen {booster.Craft.Frozen}, worst {worst:0.0000} m on the deck, the vessel's reading {Vector3.Distance(DeckLocal(booster.Position), onDeck):0.0000} m");
        Assert.True(booster.Craft.Frozen);
        Assert.True(worst < 0.05f, $"moved {worst} m on the deck while frozen");
        Assert.True(Vector3.Distance(DeckLocal(booster.Position), onDeck) < 0.05f); // the vessel's own reading follows too
    }

    [Fact]
    public void Woken_on_the_deck_it_stands_where_it_was()
    {
        // Frozen, then flown (which wakes it): a minute on, still Landed, on its feet, where it was.
        using var sim = DropOnDeck(offset: Vector3.Zero, speed: 1f);
        var booster = Dropped(sim);
        sim.Run(Nobody, _ => booster.Craft.Frozen, maxSeconds: 120);
        var onDeck = DeckLocal(booster.Position);
        sim.Physics.Wake(booster.Craft);
        sim.Run(Nobody, _ => false, maxSeconds: 60);
        output.WriteLine($"{booster.State} {booster.CrashReason}, moved {Vector3.Distance(DeckLocal(booster.Position), onDeck):0.000} m on the deck, frozen {booster.Craft.Frozen}");
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(Vector3.Distance(DeckLocal(booster.Position), onDeck) < 0.5f);
    }

    [Fact]
    public void Saved_aboard_it_comes_back_aboard()
    {
        // Saved mid-heave and loaded: back on the deck, Landed, frozen and carried, and two minutes on still there.
        using var sim = DropOnDeck(offset: Vector3.Zero, speed: 1f);
        var booster = Dropped(sim);
        sim.Run(Nobody, _ => booster.Craft.Frozen, maxSeconds: 120);
        var onDeck = DeckLocal(booster.Position);
        using var loaded = sim.Reloaded(); // save the world and load it into a fresh simulation, as WorldStoreTests does
        var again = Dropped(loaded);
        Assert.Equal(FlightState.Landed, again.State);
        float worst = 0f; // the deck heaves about 0.2 m a minute here, so one loaded frozen but left where it was would show it
        loaded.Run(Nobody, _ => { worst = MathF.Max(worst, Vector3.Distance(DeckLocal(loaded.Physics.Position(again.Craft)), onDeck)); return false; }, maxSeconds: 120);
        output.WriteLine($"{again.State} {again.CrashReason}, worst {worst:0.0000} m on the deck, moved {Vector3.Distance(DeckLocal(again.Position), onDeck):0.0000} m, frozen {again.Craft.Frozen}");
        Assert.Equal(FlightState.Landed, again.State);
        Assert.True(again.Craft.Frozen); // carried, never woken
        Assert.True(worst < 0.05f, $"moved {worst} m on the deck");
        Assert.True(Vector3.Distance(DeckLocal(again.Position), onDeck) < 0.5f);
    }

    [Fact]
    public void Lying_across_the_decks_edge_it_freezes_aboard()
    {
        // A booster (on its own, legs stowed) lying across the deck, its base 1.5 m out past the
        // deck's side: its base isn't over the deck, but the rest of it is, so it freezes aboard
        // and is carried, not left hanging where it was.
        using var sim = new Simulation(Mission.ReachOrbit); // the deck as it is at time 0, and the rocket flown on the pad, far off
        var across = Vector3.Transform(Vector3.UnitZ, Rafts.Ship.Current.Rotation);
        var lying = Quaternion.Normalize(Rafts.Ship.Current.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2)); // its nose across the deck, to -z
        var at = Rafts.Ship.Current.Position + Rafts.Ship.Normal * (Scene.DroneShipFreeboard + Scene.RocketRadius + 0.2f) + across * (Scene.DroneShipHalfBeam + 1.5f);
        var craft = sim.Physics.AddLoose(Vehicle.Booster, at, lying, Vector3.Zero, Vector3.Zero);
        Assert.Null(Rafts.Ship.HeightAbove(at)); // its base is out past the edge
        sim.Run(Nobody, _ => craft.Frozen, maxSeconds: 60);
        output.WriteLine($"frozen {craft.Frozen} after {sim.Time:0.0} s, aboard {craft.Aboard is not null}");
        Assert.True(craft.Frozen);
        Assert.NotNull(craft.Aboard);
        var onDeck = DeckLocal(sim.Physics.Position(craft));
        float worst = 0f;
        sim.Run(Nobody, _ => { worst = MathF.Max(worst, Vector3.Distance(DeckLocal(sim.Physics.Position(craft)), onDeck)); return false; }, maxSeconds: 60);
        output.WriteLine($"frozen {craft.Frozen}, worst {worst:0.0000} m on the deck");
        Assert.True(craft.Frozen);
        Assert.True(worst < 0.05f, $"moved {worst} m on the deck while frozen");
    }

    [Fact]
    public void Lying_across_the_decks_edge_it_settles_while_awake_against_the_deck()
    {
        // A booster vessel lying across the deck, its base (and its bell, its lowest points) out
        // past the deck's side: it is standing still against the deck under its middle, so it
        // lands before it freezes, and what it is measured against is the deck's movement at its
        // centre of mass, not the still ground the sea would be under its base.
        Rafts.Ship.Set(0.0);
        var across = Vector3.Transform(Vector3.UnitZ, Rafts.Ship.Current.Rotation);
        var lying = Quaternion.Normalize(Rafts.Ship.Current.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2)); // its nose across the deck, to -z
        var at = Rafts.Ship.Current.Position + Rafts.Ship.Normal * (Scene.DroneShipFreeboard + Scene.RocketRadius + 0.2f) + across * (Scene.DroneShipHalfBeam + 1.5f);
        var state = Drops.Placed(at, Planet.East(at), 0f, Vehicle.TwoStage, first: 0, last: 0, mission: Mission.ReachOrbit, flight: 1, legsOut: false) with
        {
            Position = at, Rotation = lying, Velocity = Rafts.Ship.VelocityAt(at), Aim = Vector3.Transform(Vector3.UnitY, lying),
        };
        using var sim = new Simulation(Mission.ReachOrbit, world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var booster = Dropped(sim);
        Assert.Null(Rafts.Ship.HeightAbove(booster.Position)); // its base is out past the edge
        var (landedAwake, worst, deck) = (false, 0f, 0f);
        // Watched, so it settles in the physics, whose measuring against the deck this is.
        var watched = new[] { booster };
        sim.Run((_, _) => new FlightInput(Watched: watched), _ =>
        {
            Assert.False(booster.OnFormula);
            if (!booster.Craft.Frozen && sim.Time > 0) // measured from its first step
            {
                landedAwake |= booster.State == FlightState.Landed;
                // Each step while it is awake (frozen, it isn't measured), the deck at its centre of mass as the step left it.
                var under = Rafts.Ship.VelocityAt(booster.CentreOfMass);
                (worst, deck) = (MathF.Max(worst, Vector3.Distance(booster.GroundVelocity, under)), MathF.Max(deck, under.Length()));
            }
            return booster.Craft.Frozen || booster.State == FlightState.Crashed;
        }, maxSeconds: 60);
        output.WriteLine($"{booster.State} {booster.CrashReason}, landed while awake {landedAwake}, frozen {booster.Craft.Frozen}; "
            + $"the deck at its centre of mass up to {deck:0.000} m/s, what it was measured against up to {worst:0.0000} m/s off that");
        Assert.True(landedAwake, "it never landed while awake");
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.True(worst < 0.001f, "it was measured against something other than the deck under its centre of mass");
    }

    [Fact]
    public void Falling_on_its_side_across_the_decks_edge_with_its_middle_over_the_sea_its_body_is_judged()
    {
        // A booster (legs stowed) lying across the deck's side, falling side-on at 9 m/s (over the
        // 6 its body takes, under the 15 that stops anything dead): its nose end comes down on the
        // deck, but its base end and its centre of mass are out over the sea, far deeper under them
        // than it is long. Its body touches the deck, so that is a touchdown, judged as any other.
        Rafts.Ship.Set(0.0);
        var across = Vector3.Transform(Vector3.UnitZ, Rafts.Ship.Current.Rotation);
        var lying = Quaternion.Normalize(Rafts.Ship.Current.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2)); // its nose across the deck, to -z
        var centreOfMass = RocketGeometry.CentreOfMassHeight(Vehicle.TwoStage, 0, 0);
        var at = Rafts.Ship.Current.Position + Rafts.Ship.Normal * (Scene.DroneShipFreeboard + Scene.RocketRadius + 0.3f)
            + across * (Scene.DroneShipHalfBeam + centreOfMass + 1f); // its centre of mass 1 m out past the deck's side
        var state = Drops.Placed(at, Planet.East(at), 0f, Vehicle.TwoStage, first: 0, last: 0, mission: Mission.ReachOrbit, flight: 1, legsOut: false) with
        {
            Position = at, Rotation = lying, Velocity = Rafts.Ship.VelocityAt(at) - Rafts.Ship.Normal * 9f, Aim = Vector3.Transform(Vector3.UnitY, lying),
        };
        using var sim = new Simulation(Mission.ReachOrbit, world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var booster = Dropped(sim);
        var length = RocketGeometry.StackHeight(Vehicle.TwoStage, 0, 0) + Vehicle.TwoStage.Stages[0].BellHeight;
        Assert.Null(Rafts.Ship.HeightAbove(booster.CentreOfMass)); // its middle is out over the sea
        Assert.True(Planet.GroundAltitude(booster.CentreOfMass) > length, "the sea under it is shallower than it is long: this shows nothing");
        Assert.NotNull(Rafts.Ship.HeightAbove(Vector3.Transform(new Vector3(0, length - 3f, 0), booster.Model))); // its nose end is over the deck
        // Watched, so it comes down in the physics, whose judging of its body this is.
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
    public void Landed_on_the_deck_it_freezes_there_with_the_flown_vessel_on_the_move_beside_the_ship()
    {
        // Landed on the deck while the vessel being flown is thrown up beside the ship, on the move
        // but never on a way that could touch it: the deck's heave is part of being landed on it, so
        // the booster freezes there and is carried with the deck, Landed all the while.
        Rafts.Ship.Set(0.0);
        var top = Rafts.Ship.Current.Position + Rafts.Ship.Normal * Scene.DroneShipFreeboard;
        var across = Vector3.Transform(Vector3.UnitZ, Rafts.Ship.Current.Rotation);
        var booster = Drops.Placed(top, Planet.East(top), 1f, Vehicle.TwoStage, first: 0, last: 0, mission: Mission.ReachOrbit, flight: 1);
        var beside = top + across * (Scene.DroneShipHalfBeam + 40f);
        var thrown = Drops.Placed(beside, Planet.East(beside), -40f, Vehicle.Booster, clearance: 40f, id: 1); // 40 m/s up
        using var sim = new Simulation(Mission.ReachOrbit, world: new WorldState { Vessels = [booster, thrown], NextVesselId = 2 });
        var standing = Dropped(sim);
        var flown = sim.Flight.WorldVessels.First(v => v.Id == 1);
        sim.Step(new FlightInput(SwitchTo: flown));
        Assert.True(sim.Run(Nobody, _ => standing.State != FlightState.Flying, maxSeconds: 10));
        Assert.Equal(FlightState.Landed, standing.State);
        var landedAt = sim.Time;
        var (alwaysLanded, everFrozen) = (true, false);
        sim.Run(Nobody, _ =>
        {
            alwaysLanded &= standing.State == FlightState.Landed;
            everFrozen |= standing.Craft.Frozen;
            return flown.State != FlightState.Flying;
        }, maxSeconds: 30);
        output.WriteLine($"landed at {landedAt:0.0} s; the flown one {flown.State} at {sim.Time:0.0} s; the booster always landed {alwaysLanded}, frozen meanwhile {everFrozen}, {standing.State} {standing.CrashReason}");
        Assert.True(sim.Time - landedAt > 5.0, $"the flown one was only on the move for {sim.Time - landedAt:0.0} s after the booster landed");
        Assert.True(everFrozen, "it never froze on the deck");
        Assert.True(alwaysLanded, "it stopped being Landed on the deck");
    }

    [Fact]
    public void Flying_just_over_the_deck_it_is_saved_with_the_decks_movement()
    {
        // Saved a moment after it is set down on the deck, awake and still settling: loaded, it
        // judges its first step against the deck as it was, not against still ground.
        using var sim = DropOnDeck(offset: Vector3.Zero, speed: 0f, matchDeck: true);
        var booster = Dropped(sim);
        sim.Run(Nobody, _ => false, maxSeconds: 0.5);
        Assert.Equal(FlightState.Flying, booster.State);
        var saved = booster.GroundVelocity;
        Assert.True(saved.Length() > 0.001f, $"the deck moves at only {saved.Length():0.0000} m/s here, too slow to tell");
        using var loaded = sim.Reloaded();
        var again = Dropped(loaded);
        output.WriteLine($"saved against {saved} ({saved.Length():0.000} m/s), loaded against {again.GroundVelocity}");
        Assert.Equal(saved, again.GroundVelocity);
    }

    [Fact]
    public void Frozen_afloat_against_the_hull_it_stays_where_it_froze()
    {
        // A booster floating against the hull's side, in the water, far enough from the player (the
        // rocket on the pad, 2.4 km off) to freeze afloat. Frozen, it gets no gravity or buoyancy,
        // so should the heaving hull wake it in Jolt it would drift; it must stay where it froze.
        using var sim = new Simulation(Mission.ReachOrbit);
        var across = Vector3.Transform(Vector3.UnitZ, Rafts.Ship.Current.Rotation);
        var at = Rafts.Ship.Current.Position + across * (Scene.DroneShipHalfBeam + Scene.RocketRadius + 0.3f);
        var craft = sim.Physics.AddLoose(Vehicle.Booster, at, Planet.UprightAt(at), Vector3.Zero, Vector3.Zero);
        sim.Run(Nobody, _ => craft.Frozen, maxSeconds: 120);
        output.WriteLine($"frozen {craft.Frozen} after {sim.Time:0.0} s, aboard {craft.Aboard is not null}, {craft.Submerged:0.00} submerged");
        Assert.True(craft.Frozen);
        Assert.Null(craft.Aboard); // beside the ship, not on it
        var froze = sim.Physics.Position(craft);
        float worst = 0f;
        sim.Run(Nobody, _ => { worst = MathF.Max(worst, Vector3.Distance(sim.Physics.Position(craft), froze)); return false; }, maxSeconds: 60);
        output.WriteLine($"frozen {craft.Frozen}, active {sim.Physics.IsActive(craft)}, worst {worst:0.0000} m from where it froze");
        Assert.True(craft.Frozen);
        Assert.False(sim.Physics.IsActive(craft));
        Assert.True(worst < 0.05f, $"drifted {worst} m while frozen");
    }

    [Fact]
    public void Off_the_target_circle_it_is_landed_off_target()
    {
        using var sim = DropOnDeck(offset: new Vector3(18f, 0f, 0f), speed: 1f); // 18 m along the deck from the centre
        var booster = Dropped(sim);
        sim.Run(Nobody, _ => booster.State != FlightState.Flying, maxSeconds: 60);
        Assert.Equal(FlightState.Landed, booster.State);
        Assert.False(booster.LandedOnTarget);
        List<(string Text, Brutal.Numerics.float4 Color)> lines = [];
        var (title, _) = Hud.Ending(sim.Flight, null, lines); // flown vessel is the booster
        Assert.Equal("LANDED OFF TARGET", title);
        Assert.Contains(lines, l => l.Text.Contains("of the drone ship's centre"));
    }

    [Fact]
    public void Missing_the_ship_it_splashes_down_off_target()
    {
        using var sim = DropOnDeck(offset: new Vector3(0f, 0f, Scene.DroneShipHalfBeam + 12f), speed: 3f); // over the sea beside it
        var booster = Dropped(sim);
        sim.Run(Nobody, _ => booster.State is FlightState.Floating or FlightState.Crashed, maxSeconds: 120);
        Assert.Equal(FlightState.Floating, booster.State);
        var (title, _) = Hud.Ending(sim.Flight, null, []);
        Assert.Equal("SPLASHDOWN OFF TARGET", title);
    }

    /// <summary>
    /// The booster beside the ship, <paramref name="gap"/> clear of its side (across it, to +z),
    /// its lowest foot <paramref name="clearance"/> above the calm sea (the deck's top is
    /// <see cref="Scene.DroneShipFreeboard"/> above that), driven sideways into the hull at <paramref name="speed"/>;
    /// with <paramref name="legsOut"/> false, its legs stowed and its bell <paramref name="clearance"/> up.
    /// </summary>
    private static Simulation IntoTheSide(float gap, float clearance, float speed, bool legsOut = true)
    {
        Rafts.Ship.Set(0.0);
        var across = Vector3.Transform(Vector3.UnitZ, Rafts.Ship.Current.Rotation);
        var at = Rafts.Ship.Current.Position + Rafts.Ship.Normal * Scene.DroneShipFreeboard + across * (Scene.DroneShipHalfBeam + gap);
        var up = Planet.Up(at);
        var towards = -Vector3.Normalize(across - up * Vector3.Dot(across, up));
        return Drops.At(at, Planet.East(at), 0f, Vehicle.TwoStage, towards * speed, first: 0, last: 0, mission: Mission.ReachOrbit, flight: 1,
            clearance: clearance, legsOut: legsOut);
    }

    [Fact]
    public void Into_the_hulls_side_fast_it_crashes()
    {
        // Its legs and the bottom of its body below the deck's edge, clear of the water: the hull's
        // side stops it dead, as a wall would.
        using var sim = IntoTheSide(gap: 6f, clearance: 1f, speed: 20f);
        var booster = Dropped(sim);
        sim.Run(Nobody, _ => booster.State != FlightState.Flying, maxSeconds: 10);
        output.WriteLine($"{booster.State}: {booster.CrashReason}");
        Assert.Equal(FlightState.Crashed, booster.State);
        Assert.StartsWith("Hit the ground", booster.CrashReason);
    }

    [Fact]
    public void Low_in_the_water_into_the_hulls_side_fast_it_crashes()
    {
        // Its feet and bell already under the waves as it strikes the hull's side: whatever touches
        // it first, the water or the hull, it is lost.
        using var sim = IntoTheSide(gap: 6f, clearance: -1.5f, speed: 20f);
        var booster = Dropped(sim);
        sim.Run(Nobody, _ => booster.State != FlightState.Flying, maxSeconds: 10);
        output.WriteLine($"{booster.State}: {booster.CrashReason}");
        Assert.Equal(FlightState.Crashed, booster.State);
    }

    [Fact]
    public void Side_on_into_the_hulls_side_its_body_is_a_touchdown()
    {
        // Its legs stowed, its body 1.8 m off the hull's side and its bell 0.5 m over the sea (the
        // bottom of its body under the deck's edge), driven sideways into the side at 9 m/s (over
        // the 6 its body takes, under the 15 that stops
        // anything dead): its body touches the side, a wall, so that is a touchdown, judged as one.
        using var sim = IntoTheSide(gap: 3f, clearance: 0.5f, speed: 9f, legsOut: false);
        var booster = Dropped(sim);
        sim.Run(Nobody, _ => booster.State != FlightState.Flying, maxSeconds: 2);
        output.WriteLine($"{booster.State}: {booster.CrashReason}");
        Assert.StartsWith("Hit the ground at", booster.CrashReason);
    }

    [Fact]
    public void Afloat_against_the_hulls_side_the_side_does_not_ground_it()
    {
        // A booster (legs stowed) in the water beside the drone ship, lying along its side, 5 cm
        // off the hull and moving with it. The side beside it doesn't hold it up: nothing under it
        // does but the water. So while it floats there, within the 0.12 m a side counts as
        // touching from, it isn't down on anything (it can settle afloat and freeze, as anywhere).
        Rafts.Ship.Set(0.0);
        var rotation = Rafts.Ship.Current.Rotation;
        var (along, across) = (Vector3.Transform(Vector3.UnitX, rotation), Vector3.Transform(Vector3.UnitZ, rotation));
        var radius = Vehicle.Booster.Stages[0].Radius;
        var length = RocketGeometry.StackHeight(Vehicle.Booster, 0, 0);
        var beside = Rafts.Ship.Current.Position + across * (Scene.DroneShipHalfBeam + radius + 0.05f);
        var state = Drops.Placed(beside, along, 0f, legsOut: false, leanDegrees: 90f, clearance: -0.6f) with
        {
            Velocity = Rafts.Ship.VelocityAt(beside),
        };
        state = state with { Position = state.Position - along * (length / 2f) };
        using var sim = new Simulation(Mission.ReachOrbit, world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var booster = Dropped(sim);
        // Watched, so it is in the physics (far from the pad, it loads under formula), whose
        // judging against the hull's side this is.
        sim.Flight.BringIn(booster);
        Pilot watching = (_, _) => new FlightInput(Watched: [booster]);
        var (against, grounded) = (0, 0);
        sim.Run(watching, _ =>
        {
            var gap = Vector3.Dot(booster.CentreOfMass - Rafts.Ship.Current.Position, across) - Scene.DroneShipHalfBeam - radius;
            if (booster.State == FlightState.Flying && booster.Craft.Submerged > 0f && gap < 0.12f)
            {
                against++;
                if (booster.Grounded)
                    grounded++;
            }
            return booster.State != FlightState.Flying;
        }, maxSeconds: 10);
        output.WriteLine($"{booster.State} {booster.CrashReason} after {sim.Time:0.0} s; {against} frames afloat within 0.12 m of the side, {grounded} of them down on something");
        Assert.True(against > 10, "it was never afloat against the side: this shows nothing");
        Assert.Equal(0, grounded);
        Assert.Equal(FlightState.Floating, booster.State);
        Assert.True(sim.Run(watching, _ => booster.Craft.Frozen, maxSeconds: 30), "it never froze afloat");
    }

    [Fact]
    public void Afloat_against_a_hulls_side_in_the_roughest_sea_a_ship_is_in_it_stays_afloat()
    {
        // The same booster along the side of the drone ship in the roughest water any is in: the
        // waves heave it and the ship, knocking it against the hull and off again. None of that is
        // a blow, nor holds it up: for a minute it is afloat, never landed or wrecked against it.
        var ship = Rafts.Ships.MaxBy(s => Sea.WaveScale(Planet.Up(s.Centre)))!;
        ship.Set(0.0);
        var rotation = ship.Current.Rotation;
        var (along, across) = (Vector3.Transform(Vector3.UnitX, rotation), Vector3.Transform(Vector3.UnitZ, rotation));
        var radius = Vehicle.Booster.Stages[0].Radius;
        var length = RocketGeometry.StackHeight(Vehicle.Booster, 0, 0);
        var beside = ship.Current.Position + across * (Scene.DroneShipHalfBeam + radius + 0.05f);
        var state = Drops.Placed(beside, along, 0f, legsOut: false, leanDegrees: 90f, clearance: -0.6f) with
        {
            Velocity = ship.VelocityAt(beside),
        };
        state = state with { Position = state.Position - along * (length / 2f) };
        using var sim = new Simulation(Mission.ReachOrbit, world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var booster = Dropped(sim);
        sim.Step(new FlightInput(SwitchTo: booster)); // flown, so afloat it never freezes: the waves work at it all the while
        // Down on something while flying, or, once floating, any touch after (each one changes its touchdown).
        var (against, grounded, floating, touches) = (0, 0, 0, 0);
        var (touchdown, hardest) = (0f, 0f);
        var states = new HashSet<FlightState>();
        sim.Run(Nobody, _ =>
        {
            states.Add(booster.State);
            if (booster.State == FlightState.Floating)
            {
                if (floating++ > 0 && booster.TouchdownSpeed != touchdown)
                    touches++;
                touchdown = booster.TouchdownSpeed;
            }
            var side = Vector3.Transform(Vector3.UnitZ, ship.Current.Rotation); // the ship rolls and pitches
            var gap = Vector3.Dot(booster.CentreOfMass - ship.Current.Position, side) - Scene.DroneShipHalfBeam - radius;
            if (!booster.Craft.Frozen && booster.Craft.Submerged > 0f && gap < 0.12f)
            {
                against++;
                hardest = MathF.Max(hardest, Vector3.Dot(booster.Velocity - ship.VelocityAt(booster.CentreOfMass), -side));
                if (booster.State == FlightState.Flying && booster.Grounded)
                    grounded++;
            }
            return false;
        }, maxSeconds: 60);
        output.WriteLine($"waves {Sea.WaveScale(Planet.Up(ship.Centre)):0.00}: {booster.State} {booster.CrashReason} (frozen {booster.Craft.Frozen}); states {string.Join(", ", states)}; "
            + $"{floating} frames floating, {against} awake afloat within 0.12 m of the side (at most {hardest:0.00} m/s into it), {grounded} of them flying and down on something, {touches} touches while floating; touchdown {booster.TouchdownSpeed:0.00} m/s");
        Assert.True(against > 10, "it was never afloat against the side: this shows nothing");
        Assert.DoesNotContain(FlightState.Landed, states);
        Assert.DoesNotContain(FlightState.Crashed, states);
        Assert.Equal(0, grounded);
        Assert.Equal(0, touches);
        Assert.Equal(FlightState.Floating, booster.State);
    }

    [Fact]
    public void Afloat_struck_into_the_hulls_side_the_blow_is_judged()
    {
        // The same booster lying in the water along the drone ship's side, but 0.3 m off it and
        // moving into it at 8 m/s (over the 6 its body takes): afloat or not, its body strikes the
        // side, a wall, so that is a touchdown, judged as one.
        Rafts.Ship.Set(0.0);
        var rotation = Rafts.Ship.Current.Rotation;
        var (along, across) = (Vector3.Transform(Vector3.UnitX, rotation), Vector3.Transform(Vector3.UnitZ, rotation));
        var radius = Vehicle.Booster.Stages[0].Radius;
        var length = RocketGeometry.StackHeight(Vehicle.Booster, 0, 0);
        var beside = Rafts.Ship.Current.Position + across * (Scene.DroneShipHalfBeam + radius + 0.3f);
        var state = Drops.Placed(beside, along, 0f, legsOut: false, leanDegrees: 90f, clearance: -0.6f) with
        {
            Velocity = Rafts.Ship.VelocityAt(beside) - across * 8f,
        };
        state = state with { Position = state.Position - along * (length / 2f) };
        using var sim = new Simulation(Mission.ReachOrbit, world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var booster = Dropped(sim);
        // Watched, so it is in the physics (far from the pad, it loads under formula), whose
        // judging against the hull's side this is.
        sim.Flight.BringIn(booster);
        Pilot watching = (_, _) => new FlightInput(Watched: [booster]);
        var afloat = false;
        sim.Run(watching, _ =>
        {
            afloat |= booster.Craft.Submerged > 0f;
            return booster.State != FlightState.Flying;
        }, maxSeconds: 5);
        output.WriteLine($"{booster.State} {booster.CrashReason} after {sim.Time:0.00} s; afloat {afloat}");
        Assert.True(afloat, "it was never afloat: this shows nothing");
        Assert.Equal(FlightState.Crashed, booster.State);
        Assert.StartsWith("Hit the ground at", booster.CrashReason);
    }

    [Fact]
    public void Over_the_deck_the_ground_is_the_deck()
    {
        Rafts.Ship.Set(40.0);
        var top = Rafts.Ship.Current.Position + Rafts.Ship.Normal * Scene.DroneShipFreeboard;
        var (height, normal) = Planet.Ground(top + Rafts.Ship.Normal * 5f);
        Assert.Equal(5f, height, 0.01f);
        Assert.True(Vector3.Dot(normal, Rafts.Ship.Normal) > 0.9999f);
        Assert.Equal(Rafts.Ship.VelocityAt(top), Planet.GroundVelocity(top));
        Assert.Equal(Vector3.Zero, Planet.GroundVelocity(Vector3.Zero)); // the launch pad doesn't move
    }
}
