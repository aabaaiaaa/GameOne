using System.Numerics;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// What each craft touched, read from Jolt's contacts after each step (<see cref="RocketPhysics.TouchesBegun"/>):
/// what it was (the ground, the tower, a mast, a raft or another craft), where on the craft it
/// touched, where and which way, and how fast the two were closing just before; and what each
/// craft is touching now (<see cref="RocketPhysics.Touching(Craft, SolidKind)"/>), which forgets a
/// body the game takes out (Jolt says nothing of a sleeping body's contacts as it goes).
/// </summary>
public sealed class TouchTests(ITestOutputHelper output)
{
    private const float Step = 1f / 120f;
    private static readonly float Radius = Vehicle.Booster.Stages[0].Radius, Bell = Vehicle.Booster.Stages[0].BellHeight;

    /// <summary>
    /// Steps <paramref name="physics"/> a step at a time, for at most <paramref name="seconds"/>,
    /// until <paramref name="craft"/> has a touch <paramref name="which"/> wants (any, by
    /// default): that one, and the craft's velocity just before the update it came in.
    /// </summary>
    private static (Touch Touch, Vector3 Before)? FirstTouch(RocketPhysics physics, Craft craft, Func<Touch, bool>? which = null, float seconds = 2f)
    {
        for (var t = 0f; t < seconds; t += Step)
        {
            var before = physics.Velocity(craft);
            physics.Update(Step, []);
            foreach (var touch in physics.TouchesBegun(craft))
                if (which is null || which(touch))
                    return (touch, before);
        }
        return null;
    }

    /// <summary>Steps <paramref name="physics"/> for <paramref name="seconds"/>, gathering <paramref name="craft"/>'s touches over them.</summary>
    private static List<Touch> Gather(RocketPhysics physics, Craft craft, float seconds)
    {
        var all = new List<Touch>();
        for (var t = 0f; t < seconds; t += Step)
        {
            physics.Update(Step, []);
            all.AddRange(physics.TouchesBegun(craft));
        }
        return all;
    }

    /// <summary>A booster upright over dead-level ground, its legs stowed, its bell 0.3 m up, falling at 4 m/s.</summary>
    private static (Craft Craft, Vector3 Up) DroppedOnLevelGround(RocketPhysics physics)
    {
        var (at, downhill, _) = Drops.FindSlope(0f, 0.02f, from: Drops.LevelGround);
        var placed = Drops.Placed(at, downhill, 4f, legsOut: false);
        var craft = physics.AddLoose(Vehicle.Booster, placed.Position.ToSingle(), placed.Rotation, placed.Velocity.ToSingle(), Vector3.Zero);
        return (craft, Planet.Up(at));
    }

    [Fact]
    public void A_craft_dropped_on_level_ground_touches_it_at_its_speed()
    {
        using var physics = new RocketPhysics(GroundBody.Planet);
        var (craft, up) = DroppedOnLevelGround(physics);
        var touched = FirstTouch(physics, craft);
        Assert.NotNull(touched);
        var m = touched.Value.Touch;
        var axis = physics.Up(craft);
        var bellBottom = physics.Position(craft) - axis * Bell; // where it is now, a step's travel (4 cm) on from where it touched
        // How far a point is from the bell's bottom: from the disc it ends in (Scene.RocketBellRadius
        // round, the booster's bell's), not from the disc's centre or its rim.
        float FromBell(Vector3 p)
        {
            var along = Vector3.Dot(p - bellBottom, axis);
            var outside = MathF.Max(0f, (p - bellBottom - axis * along).Length() - Scene.RocketBellRadius);
            return MathF.Sqrt(along * along + outside * outside);
        }
        output.WriteLine($"{m.With} at its {m.Part}, fresh {m.Fresh}, closing at {m.ClosingSpeed:0.000} m/s, normal·up {Vector3.Dot(m.Normal, up):0.000}, {FromBell(m.Point):0.000} m from the bell's bottom");
        Assert.Equal(SolidKind.Ground, m.With);
        Assert.Equal(CraftPart.Bell, m.Part);
        Assert.True(m.Fresh);
        Assert.Null(m.Other);
        // 4 m/s, and what falling the 0.3 m to the ground adds: √(4² + 2 g 0.3) is 4.68 m/s.
        Assert.InRange(m.ClosingSpeed, MathF.Sqrt(16f + 2f * Scene.SurfaceGravity * 0.3f) - 0.2f, MathF.Sqrt(16f + 2f * Scene.SurfaceGravity * 0.3f) + 0.2f);
        Assert.True(Vector3.Dot(m.Normal, up) > 0.9f, $"the normal isn't up out of the ground: {Vector3.Dot(m.Normal, up)}");
        Assert.True(FromBell(m.Point) < 0.5f, $"the point is {FromBell(m.Point)} m from the bell's bottom");

        // It bounces (by a centimetre: the ground's restitution, 0.1, the larger, is Jolt's), and
        // comes down again; once it stands (its bell touching for a quarter of a second on end), it
        // touches the ground no more at its bell: each tile's triangle its bell comes onto as it
        // settles is a contact of its own, but no new touch.
        var standing = 0;
        for (var t = 0f; t < 1f && standing < 30; t += Step)
        {
            physics.Update(Step, []);
            standing = physics.Touching(craft, SolidKind.Ground, CraftPart.Bell) ? standing + 1 : 0;
        }
        Assert.Equal(30, standing);
        var later = Gather(physics, craft, 1.5f);
        Assert.True(physics.Touching(craft, SolidKind.Ground, CraftPart.Bell), "it isn't touching the ground at its bell as it stands");
        Assert.DoesNotContain(later, l => l.Fresh && l.With == SolidKind.Ground && l.Part == CraftPart.Bell);
    }

    [Fact]
    public void A_booster_set_down_on_its_legs_touches_the_ground_once_at_its_feet()
    {
        // Its legs out, its lowest foot (hanging at full stroke) 0.3 m up, falling at 3 m/s: the
        // legs hold it up within their stroke themselves, so Jolt's feet (where the legs are pushed
        // right up) never touch, but its feet touch the ground all the same, the first of them
        // anew, at √(3² + 2 g 0.3), 3.86 m/s; the others coming down, and standing there, no new touch.
        using var physics = new RocketPhysics(GroundBody.Planet);
        var (at, downhill, _) = Drops.FindSlope(0f, 0.02f, from: Drops.LevelGround);
        var placed = Drops.Placed(at, downhill, 3f);
        var craft = physics.AddLoose(Vehicle.Booster, placed.Position.ToSingle(), placed.Rotation, placed.Velocity.ToSingle(), Vector3.Zero);
        physics.SetLegs(craft, 1f);
        var all = Gather(physics, craft, 2f);
        foreach (var m in all)
            output.WriteLine($"{m.With} at its {m.Part}, fresh {m.Fresh}, closing at {m.ClosingSpeed:0.000} m/s, normal·up {Vector3.Dot(m.Normal, Planet.Up(at)):0.000}, {Planet.GroundAltitude(m.Point):0.000} m over the ground");
        var expected = MathF.Sqrt(9f + 2f * Scene.SurfaceGravity * Drops.Clearance);
        var fresh = Assert.Single(all, m => m.Fresh);
        Assert.Equal(SolidKind.Ground, fresh.With);
        Assert.Equal(CraftPart.Foot, fresh.Part);
        Assert.InRange(fresh.ClosingSpeed, expected - 0.2f, expected + 0.2f);
        Assert.True(Vector3.Dot(fresh.Normal, Planet.Up(at)) > 0.9f, "the normal isn't up out of the ground");
        Assert.InRange(Planet.GroundAltitude(fresh.Point), -0.05f, 0.05f); // on the ground, under the foot
    }

    /// <summary>
    /// Two boosters lying side by side 500 m up over the zone, their sides <paramref name="gap"/>
    /// apart, the first moving at <paramref name="speed"/> towards the second, the second at
    /// <paramref name="otherSpeed"/> towards the first; the second frozen, with <paramref name="frozen"/>
    /// (as one restored landed is).
    /// </summary>
    private static (Craft Mover, Craft Still) SideBySide(RocketPhysics physics, bool frozen, float speed = 3f, float gap = 0.3f, float otherSpeed = 0f)
    {
        var at = Scene.LandingZoneCentre + Planet.Up(Scene.LandingZoneCentre) * 500f;
        var east = Planet.East(at);
        var lying = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(east, MathF.PI / 2f) * Planet.UprightAt(at));
        var half = Radius + gap / 2f;
        var mover = physics.AddLoose(Vehicle.Booster, at - east * half, lying, east * speed, Vector3.Zero);
        var still = frozen
            ? physics.Restore(Vehicle.Booster, 0, 0, at + east * half, lying, Vector3.Zero, Vector3.Zero, 0f, fairingOn: false, frozen: true)
            : physics.AddLoose(Vehicle.Booster, at + east * half, lying, -east * otherSpeed, Vector3.Zero);
        Assert.Equal(frozen, still.Frozen);
        return (mover, still);
    }

    [Fact]
    public void Two_crafts_touching_each_touch_the_other_at_their_closing_speed()
    {
        using var physics = new RocketPhysics(GroundBody.Planet);
        var (a, b) = SideBySide(physics, frozen: false);
        var aMet = new List<Touch>();
        var bMet = new List<Touch>();
        for (var t = 0f; t < 2f && aMet.Count == 0; t += Step)
        {
            physics.Update(Step, []);
            aMet.AddRange(physics.TouchesBegun(a));
            bMet.AddRange(physics.TouchesBegun(b));
        }
        foreach (var m in aMet.Concat(bMet))
            output.WriteLine($"{m.With}, closing at {m.ClosingSpeed:0.000} m/s, normal {m.Normal}");
        var ma = Assert.Single(aMet, m => m.With == SolidKind.Craft);
        var mb = Assert.Single(bMet, m => m.With == SolidKind.Craft);
        Assert.Same(b, ma.Other);
        Assert.Same(a, mb.Other);
        Assert.True(ma.Fresh && mb.Fresh, "a craft is always touched anew");
        Assert.InRange(ma.ClosingSpeed, 2.8f, 3.2f);
        Assert.InRange(mb.ClosingSpeed, 2.8f, 3.2f);
        Assert.True(Vector3.Dot(ma.Normal, mb.Normal) < -0.9f, $"their normals aren't opposite: {Vector3.Dot(ma.Normal, mb.Normal)}");
    }

    [Fact]
    public void A_craft_touching_a_frozen_one_touches_it_at_its_own_speed()
    {
        // At 3 m/s it wakes the frozen one as it comes (over Scene.WakeSpeed, within Scene.WakeMargin),
        // still as it wakes.
        using (var physics = new RocketPhysics(GroundBody.Planet))
        {
            var (mover, frozen) = SideBySide(physics, frozen: true);
            var touched = FirstTouch(physics, mover, m => m.With == SolidKind.Craft);
            Assert.NotNull(touched);
            var m = touched.Value.Touch;
            output.WriteLine($"closing at {m.ClosingSpeed:0.000} m/s");
            Assert.Same(frozen, m.Other);
            Assert.InRange(m.ClosingSpeed, 2.8f, 3.2f);
        }

        // Too slow to wake it (0.5 m/s, 5 cm off), it touches it still frozen: the frozen one is touched
        // as still, though it was moving in the step before it was frozen. (Frozen, it doesn't fall
        // as the mover does, so the mover touches it at its whole velocity, its fall in it.)
        using (var physics = new RocketPhysics(GroundBody.Planet))
        {
            var (mover, other) = SideBySide(physics, frozen: false, speed: 0.5f, gap: 0.05f, otherSpeed: 0.5f);
            physics.Update(Step, []);
            Assert.Empty(physics.TouchesBegun(mover));
            physics.Freeze(other);
            Touch? touched = null;
            var (before, wasFrozen) = (Vector3.Zero, false);
            for (var t = 0f; t < 1f && touched is null; t += Step)
            {
                (before, wasFrozen) = (physics.Velocity(mover), other.Frozen);
                physics.Update(Step, []);
                touched = physics.TouchesBegun(mover).Where(m => m.With == SolidKind.Craft).Select(m => (Touch?)m).FirstOrDefault();
            }
            Assert.NotNull(touched);
            var m = touched.Value;
            output.WriteLine($"closing at {m.ClosingSpeed:0.000} m/s, its own {before.Length():0.000} m/s, frozen {wasFrozen}");
            Assert.True(wasFrozen, "the other woke before they touched");
            Assert.Same(other, m.Other);
            Assert.InRange(m.ClosingSpeed, before.Length() - 0.1f, before.Length() + 0.1f);
        }
    }

    [Fact]
    public void The_tower_and_a_mast_are_met_as_what_they_are()
    {
        // Upright, 8 m up, 0.3 m from the tower's side (the side towards the pad), moving into it at 1 m/s.
        using (var physics = new RocketPhysics(GroundBody.Planet))
        {
            var site = LaunchSites.All[0];
            var local = Scene.TowerOffset + new Vector3(Scene.TowerHalfWidth + Radius + 0.3f, 8f, 0f);
            var craft = physics.AddLoose(Vehicle.Booster, site.ToWorld(local), site.Frame, Vector3.Transform(-Vector3.UnitX, site.Frame), Vector3.Zero);
            var touched = FirstTouch(physics, craft);
            Assert.NotNull(touched);
            var (m, before) = touched.Value;
            var into = -Vector3.Dot(m.Closing, m.Normal);
            output.WriteLine($"{m.With} at its {m.Part}, fresh {m.Fresh}, closing at {m.ClosingSpeed:0.000} m/s ({into:0.000} into it), its own {before.Length():0.000} m/s");
            Assert.Equal(SolidKind.Tower, m.With);
            Assert.True(m.Fresh);
            Assert.Equal(CraftPart.Hull, m.Part); // its side, 8 m up
            // Its whole velocity (it falls as it goes: there is no ground under it), 1 m/s of it into the tower.
            Assert.InRange(m.ClosingSpeed, before.Length() - 0.1f, before.Length() + 0.1f);
            Assert.InRange(into, 0.9f, 1.1f);
        }

        // And as far from a pad's first mast, 5 m up it, moving into it.
        using (var physics = new RocketPhysics(GroundBody.Planet))
        {
            var place = PadLights.All.First(p => p.Ship is null);
            var mast = place.Masts[0];
            var local = mast.Foot + new Vector3(Scene.MastHalfWidth + Radius + 0.3f, 5f, 0f);
            var craft = physics.AddLoose(Vehicle.Booster, place.ToWorld(local), place.Frame, Vector3.Transform(-Vector3.UnitX, place.Frame), Vector3.Zero);
            var touched = FirstTouch(physics, craft);
            Assert.NotNull(touched);
            Assert.Equal(SolidKind.Mast, touched.Value.Touch.With);
            Assert.True(touched.Value.Touch.Fresh);
        }
    }

    /// <summary>
    /// A booster upright on the drone ship's deck as it is at time 0 (the physics' start), its
    /// bell 0.3 m over <paramref name="onDeck"/> (in the ship's frame), falling at 3 m/s against the deck there.
    /// </summary>
    private static Craft OverTheShip(RocketPhysics physics, Raft ship, Vector3 onDeck)
    {
        ship.Set(0.0);
        var pose = ship.Current;
        var origin = pose.Position + Vector3.Transform(onDeck + new Vector3(0f, Bell + 0.3f, 0f), pose.Rotation);
        return physics.AddLoose(Vehicle.Booster, origin, pose.Rotation, ship.VelocityAt(origin) - ship.Normal * 3f, Vector3.Zero);
    }

    [Fact]
    public void The_drone_ship_is_met_at_the_speed_against_its_deck()
    {
        var ship = Rafts.Ships[0];
        using (var physics = new RocketPhysics(GroundBody.Planet))
        {
            var craft = OverTheShip(physics, ship, new Vector3(0f, ship.Freeboard, 0f));
            var touched = FirstTouch(physics, craft);
            Assert.NotNull(touched);
            var (m, before) = touched.Value;
            var against = (before - ship.VelocityAt(m.Point)).Length();
            output.WriteLine($"{m.With}, closing at {m.ClosingSpeed:0.000} m/s, against the deck {against:0.000}");
            Assert.Equal(SolidKind.Raft, m.With);
            Assert.Same(ship, m.Raft);
            Assert.InRange(m.ClosingSpeed, against - 0.2f, against + 0.2f);
        }

        // Onto the top of one of its masts.
        using (var physics = new RocketPhysics(GroundBody.Planet))
        {
            var mast = PadLights.All.First(p => p.Ship == ship).Masts[0];
            var craft = OverTheShip(physics, ship, mast.Foot + new Vector3(0f, mast.Height, 0f));
            var touched = FirstTouch(physics, craft);
            Assert.NotNull(touched);
            Assert.Equal(SolidKind.Mast, touched.Value.Touch.With);
            Assert.True(touched.Value.Touch.Fresh);
        }
    }

    [Fact]
    public void With_its_legs_out_its_body_is_met_at_its_body()
    {
        // Upright, its legs out, 500 m up over the zone, touched on its body's side by another upright
        // one (its legs stowed, 5 m higher), coming at it across at 3 m/s from every way round:
        // only its feet stand out further than its body, and only at their pads' height, so
        // however far out Jolt puts the point, a touch high on its side is on its body.
        for (var k = 0; k < 8; k++)
        {
            using var physics = new RocketPhysics(GroundBody.Planet);
            var at = Scene.LandingZoneCentre + Planet.Up(Scene.LandingZoneCentre) * 500f;
            var (up, upright) = (Planet.Up(at), Planet.UprightAt(at));
            var (s, c) = MathF.SinCos(MathF.Tau * (k + 0.5f) / 8f);
            var across = Planet.East(at) * c + Vector3.Cross(Planet.East(at), up) * s;
            var legged = physics.AddLoose(Vehicle.Booster, at, upright, Vector3.Zero, Vector3.Zero);
            physics.SetLegs(legged, 1f);
            Assert.True(legged.LegsDeployed);
            var other = physics.AddLoose(Vehicle.Booster, at + up * 5f + across * (2f * Radius + 0.3f), upright, -across * 3f, Vector3.Zero);
            var touched = FirstTouch(physics, legged, m => m.With == SolidKind.Craft);
            Assert.NotNull(touched);
            var m = touched.Value.Touch;
            output.WriteLine($"from {k * 45 + 22.5f}°: at its {m.Part}, {Vector3.Dot(m.Point - physics.Position(legged), up):0.00} m up it, closing at {m.ClosingSpeed:0.000} m/s");
            Assert.Same(other, m.Other);
            Assert.Equal(CraftPart.Hull, m.Part);
        }
    }

    [Fact]
    public void Its_feet_and_bell_are_one_place_to_touch_down_its_body_another()
    {
        // The feet and the bell are its lowest points together (as Vessel's touchdowns have always
        // taken them), so a touch on either while the other already touches the ground or a raft
        // is no new touch; its body is a place of its own. (Told from its live contacts, set here:
        // on level ground its feet are always below its bell, so the two don't touch together.)
        using var physics = new RocketPhysics(GroundBody.Planet);
        var craft = physics.AddLoose(Vehicle.Booster, Scene.LandingZoneCentre + Planet.Up(Scene.LandingZoneCentre) * 500f,
            Planet.UprightAt(Scene.LandingZoneCentre), Vector3.Zero, Vector3.Zero);
        void Set(SolidKind with, CraftPart part, int count) => craft.Touches[Craft.TouchIndex(with, part)] = count;

        Assert.True(RocketPhysics.IsNewTouch(craft, SolidKind.Ground, CraftPart.Bell));
        Set(SolidKind.Ground, CraftPart.Foot, 1);
        Assert.False(RocketPhysics.IsNewTouch(craft, SolidKind.Ground, CraftPart.Bell));  // on its feet, its bell coming down
        Assert.False(RocketPhysics.IsNewTouch(craft, SolidKind.Raft, CraftPart.Foot));    // another foot, onto a raft
        Assert.True(RocketPhysics.IsNewTouch(craft, SolidKind.Ground, CraftPart.Hull));   // its body, toppling
        Assert.True(RocketPhysics.IsNewTouch(craft, SolidKind.Mast, CraftPart.Bell));     // a mast is always touched anew
        Set(SolidKind.Ground, CraftPart.Foot, 0);
        Set(SolidKind.Raft, CraftPart.Bell, 1);
        Assert.False(RocketPhysics.IsNewTouch(craft, SolidKind.Ground, CraftPart.Foot));  // on its bell on a deck, its feet coming down
        Set(SolidKind.Raft, CraftPart.Bell, 0);
        Set(SolidKind.Ground, CraftPart.Hull, 1);
        Assert.True(RocketPhysics.IsNewTouch(craft, SolidKind.Ground, CraftPart.Bell));   // lying on its body, righted onto its bell
        Assert.False(RocketPhysics.IsNewTouch(craft, SolidKind.Ground, CraftPart.Hull));
        Assert.True(physics.Touching(craft, SolidKind.Ground, CraftPart.Hull));            // Touching still goes part by part
        Assert.False(physics.Touching(craft, SolidKind.Ground, CraftPart.Bell));
    }

    [Fact]
    public void A_removed_craft_and_a_removed_tile_leave_no_contacts()
    {
        using var physics = new RocketPhysics(GroundBody.Planet);
        var (first, _) = DroppedOnLevelGround(physics);
        Assert.NotNull(FirstTouch(physics, first));
        Gather(physics, first, 0.5f);
        Assert.True(physics.Touching(first, SolidKind.Ground));
        Assert.True(physics.LiveContacts > 0);
        physics.Remove(first); // between steps, as a vessel's rules take one out: Jolt would report its contacts ended only in the next
        Assert.False(physics.Touching(first, SolidKind.Ground));
        Assert.Empty(physics.TouchesBegun(first));
        Assert.Equal(0, physics.LiveContacts);

        // Another, frozen where it stands, and the tiles under it taken out before the next step
        // (Jolt ends a frozen craft's contacts in the step after it is frozen, so until then they
        // are live): what it touched goes with them.
        var (second, _) = DroppedOnLevelGround(physics);
        Assert.NotNull(FirstTouch(physics, second));
        Gather(physics, second, 0.5f);
        physics.Freeze(second);
        var touching = physics.LiveContacts;
        output.WriteLine($"{touching} live contacts, frozen, on {physics.TileCount} tiles");
        Assert.True(touching > 0);
        Assert.True(physics.Touching(second, SolidKind.Ground));
        foreach (var cell in second.TileCells.Where(physics.HasTile).ToList())
            physics.Tiles.Remove(cell);
        Assert.Equal(0, physics.LiveContacts);
        Assert.False(physics.Touching(second, SolidKind.Ground));
    }

    [Fact]
    public void A_crowd_drops_no_contact_events()
    {
        // Ten boosters stood on their bells in a ring on level ground, each just touching the next
        // (their bodies a centimetre into each other): every one has contacts with the ground and
        // with both its neighbours, beginning together. Jolt's queue keeps them all (a lost
        // beginning would be a touch missed, a lost end a contact live for ever).
        const int Count = 10;
        using var physics = new RocketPhysics(GroundBody.Planet);
        var (at, _, _) = Drops.FindSlope(0f, 0.02f, from: Drops.LevelGround);
        var up = Planet.Up(at);
        var east = Planet.East(at);
        var north = Vector3.Cross(east, up);
        var ring = (Radius - 0.005f) / MathF.Sin(MathF.PI / Count); // the gap between neighbours' centres a little under two radii
        var crowd = new List<Craft>();
        for (var i = 0; i < Count; i++)
        {
            var (s, c) = MathF.SinCos(MathF.Tau * i / Count);
            var spot = at + (east * c + north * s) * ring;
            var placed = Drops.Placed(spot, east, 0f, legsOut: false, clearance: 0.01f);
            crowd.Add(physics.AddLoose(Vehicle.Booster, placed.Position.ToSingle(), placed.Rotation, Vector3.Zero, Vector3.Zero));
        }
        for (var t = 0f; t < 2f; t += Step)
            physics.Update(Step, []);
        var touching = crowd.Count(craft => physics.Touching(craft, SolidKind.Craft));
        output.WriteLine($"{physics.LiveContacts} live contacts; {touching} of {Count} touching another; {physics.DroppedContacts} events dropped");
        Assert.Equal(Count, touching);
        Assert.Equal(0u, physics.DroppedContacts);
    }
}
