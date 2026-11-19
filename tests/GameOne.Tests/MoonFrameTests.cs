using System.Globalization;
using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The frames, the HUD's numbers and the readout by the body the flown vessel is at: on the moon,
/// Auto by its highest ground (with the planet's hysteresis), Surface over its ground and Orbit
/// round its centre, and every reading the moon's (the body, the heights over its datum, its
/// local time, its north, the tilt, the speeds, the orbit round it); on the planet, every reading the
/// planet's (Auto by the top of its air, the pad's readings, a planet orbit's period).
/// </summary>
public sealed class MoonFrameTests(ITestOutputHelper output)
{
    private static GroundBody Moon => GroundBody.Moon;
    private static GroundBody Planet => GroundBody.Planet;

    private static WorldState World(params VesselState[] vessels) =>
        new() { Time = 0.0, Vessels = vessels, NextVesselId = vessels.Max(v => v.Id) + 1, Flights = 1 };

    /// <summary>A free-play flight in a world of <paramref name="vessel"/>, which is flown (the frame after the switch's link-up, as the moon's physics is made then).</summary>
    private static (Simulation Sim, Vessel Flown) Flying(VesselState vessel)
    {
        var sim = new Simulation(Mission.FreePlay, world: World(vessel));
        var flown = sim.Flight.WorldVessels.Single(v => v.Id == vessel.Id);
        sim.Step(new FlightInput(SwitchTo: flown));
        sim.Step(default);
        Assert.Same(flown, sim.Flight.Controlled);
        return (sim, flown);
    }

    /// <summary>A reading's first number, as a script compares it.</summary>
    private static float Number(string reading) =>
        float.Parse(reading.Split(' ')[0], NumberStyles.Float, CultureInfo.CurrentCulture);

    [Fact]
    public void On_the_moon_auto_is_orbital_once_above_its_highest_ground_by_the_margin()
    {
        var floor = Moon.MaxHeight;
        Assert.False(Moon.HasAir);
        Assert.Equal(floor, Moon.OrbitFloor);
        output.WriteLine($"the moon's highest ground {floor:0.0} m over its datum; the planet's floor {Planet.OrbitFloor:0} m");
        Assert.Equal(Frame.Orbital, Frames.Auto(Moon, Frame.Surface, floor + Scene.FrameOrbitMargin + 1f, false));
        Assert.Equal(Frame.Surface, Frames.Auto(Moon, Frame.Orbital, floor - 1f, false));
        // The planet's hysteresis: between the floor and the margin over it, each keeps the frame it had.
        Assert.Equal(Frame.Orbital, Frames.Auto(Moon, Frame.Orbital, floor + Scene.FrameOrbitMargin / 2, false));
        Assert.Equal(Frame.Surface, Frames.Auto(Moon, Frame.Surface, floor + Scene.FrameOrbitMargin / 2, false));
        Assert.Equal(Frame.Orbital, Frames.Auto(Moon, Frame.Orbital, floor, false));
        Assert.Equal(Frame.Surface, Frames.Auto(Moon, Frame.Surface, floor + Scene.FrameOrbitMargin, false));
        // Well under the planet's air's top, so by the planet's floor these would all be the surface's.
        Assert.True(floor + Scene.FrameOrbitMargin + 1f < Scene.AtmosphereHeight);
        Assert.Equal(Frame.Surface, Frames.Auto(Planet, Frame.Surface, floor + Scene.FrameOrbitMargin + 1f, false));
        // On the ground it is the surface's whatever its orbit.
        Assert.Equal(Frame.Surface, Frames.Auto(Moon, Frame.Orbital, floor + 5_000f, onTheGround: true));

        // A vessel on its orbit 400 m over the moon's highest ground is orbital by Auto; 50 m over
        // it (inside the margin, with no history) it is the surface's.
        var (high, highVessel) = Flying(InOrbit.Round(Moon, 0, 400f));
        using (high)
        {
            Assert.Equal(Frame.Orbital, highVessel.AutoFrame);
            Assert.Equal(Frame.Orbital, high.Flight.Frame);
            Assert.True(highVessel.OrbitStable);
        }
        var (low, lowVessel) = Flying(InOrbit.Round(Moon, 0, 50f));
        using (low)
        {
            Assert.Equal(Frame.Surface, lowVessel.AutoFrame);
            Assert.True(lowVessel.OrbitStable, "its orbit clears the moon's highest ground");
        }
    }

    [Fact]
    public void On_the_moon_surface_and_orbit_mean_its_ground_and_its_centre()
    {
        var (sim, v) = Flying(InOrbit.Round(Moon, 0, 400f));
        using (sim)
        {
            var flight = sim.Flight;
            Assert.Same(Moon, flight.Body);
            sim.Step(new FlightInput(ChooseFrame: FrameChoice.Surface));
            Assert.Equal(Frame.Surface, flight.Frame);
            // Over the moon's ground: the vessel's own velocity there (its frame is the moon's).
            Assert.Equal(v.Velocity, flight.FrameVelocity);
            var readings = Readout.FlightReadings(flight);
            Assert.Equal($"{Vector3.Dot(v.Velocity, Moon.Up(v.Position)):0.0} m/s", readings["Vertical"]);
            sim.Step(new FlightInput(ChooseFrame: FrameChoice.Orbit));
            Assert.Equal(Frame.Orbital, flight.Frame);
            // Round its centre, among its stars: what its orbit is worked out from.
            var stars = Moon.AmongStars(v.CentreOfMass, v.Velocity);
            Assert.Equal(stars, flight.FrameVelocity);
            output.WriteLine($"over the moon's ground {v.Velocity.Length():0.00} m/s, among its stars {stars.Length():0.00} m/s (its ground turns {Moon.GroundSpin(v.CentreOfMass).Length():0.00} m/s there)");
            Assert.InRange(stars.Length(), Moon.CircularSpeed(Moon.OrbitFloor + 400f) - 0.5f, Moon.CircularSpeed(Moon.OrbitFloor + 400f) + 0.5f);
        }

        // Landed: still over its ground, and among its stars only as its ground turns.
        var (landed, lander) = Flying(OnTheMoon.Lander(0, OnTheMoon.LandingSpot));
        using (landed)
        {
            landed.Step(new FlightInput(ChooseFrame: FrameChoice.Surface));
            Assert.True(landed.Flight.FrameVelocity.Length() < 0.1f, $"{landed.Flight.FrameVelocity.Length():0.000} m/s over the moon's ground");
            landed.Step(new FlightInput(ChooseFrame: FrameChoice.Orbit));
            var spin = Moon.GroundSpin(lander.CentreOfMass).Length();
            Assert.InRange(landed.Flight.FrameVelocity.Length(), spin - 0.1f, spin + 0.1f);
        }
    }

    [Fact]
    public void On_the_moon_the_readout_reads_the_moon()
    {
        var (sim, lander) = Flying(OnTheMoon.Lander(0, OnTheMoon.LandingSpot));
        using (sim)
        {
            var flight = sim.Flight;
            var readings = Readout.FlightReadings(flight);
            foreach (var (name, value) in readings.OrderBy(r => r.Key))
                output.WriteLine($"{name} = {value}");

            Assert.Equal("Moon", readings["Body"]);
            Assert.Equal("Landed", readings["State"]);
            // Its heights are over the moon's datum and its ground: it stands on the ground at the spot.
            var ground = Moon.Height(OnTheMoon.LandingSpot);
            Assert.InRange(Number(readings["Altitude"]), ground - 2f, ground + 2f);
            Assert.InRange(Number(readings["Ground"]), 0f, 1f);
            Assert.Equal($"{Moon.Altitude(lander.Position):0} m", readings["Moon"]);
            Assert.Equal("-90 °", readings["Moon height"]);
            Assert.Equal($"{Readout.HeightOf(flight, Planet):0} °", readings["Planet height"]);
            Assert.Equal($"{Readout.DistanceTo(flight, Planet):0} m", readings["Planet"]);
            Assert.True(Number(readings["Planet"]) > Scene.MoonDistance - Scene.MoonRadius - Planet.SeaRadius - 1_000f, "the planet is out across the gap");
            // Upright on the moon, still on its ground, with no air.
            Assert.Equal("0 °", readings["Tilt"]);
            Assert.InRange(Math.Abs(Number(readings["Vertical"])), 0f, 0.1f);
            Assert.InRange(Number(readings["Horizontal"]), 0f, 0.1f);
            Assert.Equal(0f, Number(readings["Air"]));
            Assert.Equal("Surface", readings["Frame"]);
            // Still, it only falls back round the moon: no orbit.
            Assert.Equal("(falls back)", readings["Periapsis"]);
            Assert.DoesNotContain("Period", readings.Keys);
            // Its local time is by its own day: the moon's sun over its ground.
            Assert.Equal(Sun.Clock(Sun.LocalSeconds(Moon, lander.Position, flight.WorldTime)), readings["Local time"]);
            // The compass's north is the moon's: the camera facing the moon's north there reads 0°,
            // its east 90°: the compass goes by the body the flown vessel is at (Hud.CompassFor).
            var at = lander.Position;
            var facingNorth = Hud.CompassFor(flight, at, Moon.North(at)).Facing;
            var facingEast = Hud.CompassFor(flight, at, Moon.East(at)).Facing;
            output.WriteLine($"the compass facing the moon's north {float.RadiansToDegrees(facingNorth):0.0}°, its east {float.RadiansToDegrees(facingEast):0.0}°");
            Assert.InRange(facingNorth, -1e-3f, 1e-3f);
            Assert.InRange(facingEast, MathF.PI / 2 - 1e-3f, MathF.PI / 2 + 1e-3f);
            // Landed and upright: no nose mark and no travel mark.
            var marks = Hud.CompassFor(flight, at, Moon.North(at));
            Assert.Null(marks.Nose);
            Assert.Null(marks.Travel);
            // The nearest pad's lights are found across the bodies: the lander put in the planet's frame.
            var fromPlanet = Bodies.Change(Moon, Planet, flight.WorldTime).Point((Vector3Double)at).ToSingle();
            var nearest = PadLights.All.MinBy(p => (p.Origin - fromPlanet).LengthSquared())!;
            var naive = PadLights.All.MinBy(p => (p.Origin - at).LengthSquared())!;
            output.WriteLine($"nearest pad lights across the bodies {nearest.Name}; by the moon-frame place read as the planet's {naive.Name}");
            Assert.Equal(nearest.Name, readings["Pad lights"].Split(", ")[1]);
            // The mission's landing site (none in free play) isn't read from here.
            Assert.Null(flight.LandingSite);
        }
    }

    [Fact]
    public void In_a_moon_orbit_the_readout_reads_the_orbit_round_the_moon()
    {
        var (sim, v) = Flying(InOrbit.Round(Moon, 0, 400f));
        using (sim)
        {
            var flight = sim.Flight;
            var readings = Readout.FlightReadings(flight);
            foreach (var name in new[] { "Body", "Frame", "Altitude", "Ground", "Vertical", "Horizontal", "Tilt", "Apoapsis", "Periapsis", "Period", "Orbit speed", "Orbit speed needed" })
                output.WriteLine($"{name} = {readings[name]}");

            Assert.Equal("Moon", readings["Body"]);
            Assert.Equal("Orbit", readings["Frame"]);
            var height = Moon.OrbitFloor + 400f;
            var radius = Moon.SeaRadius + height;
            // Round: both ends its height over the moon's datum, its lap and speed by the moon's pull.
            Assert.InRange(Number(readings["Apoapsis"]), height - 3f, height + 3f);
            Assert.InRange(Number(readings["Periapsis"]), height - 3f, height + 3f);
            var lap = Math.Tau * Math.Sqrt(Math.Pow(radius, 3) / Moon.Mu);
            Assert.InRange(Number(readings["Period"]), lap - 2, lap + 2);
            var speed = Moon.CircularSpeed(height);
            Assert.InRange(Number(readings["Orbit speed"]), speed - 1f, speed + 1f);
            Assert.InRange(Number(readings["Orbit speed needed"]), speed - 1f, speed + 1f);
            // Round the planet these would be nothing like it: its pull is some 30 times the moon's.
            Assert.True(MathF.Abs(Planet.CircularSpeed(height) - speed) > 100f);
            // Level and upright over the moon's ground, moving across it at its speed there.
            Assert.InRange(Number(readings["Vertical"]), -0.5f, 0.5f);
            var across = Moon.OverGround(v.CentreOfMass, Moon.AmongStars(v.CentreOfMass, v.Velocity)).Length();
            Assert.InRange(Number(readings["Horizontal"]), across - 0.5f, across + 0.5f);
            Assert.InRange(Number(readings["Tilt"]), 0f, 1f);
            // Going east round the moon: the compass's travel mark at 90° by the moon's north.
            var travel = Hud.CompassFor(flight, v.Position, Moon.North(v.Position)).Travel;
            Assert.NotNull(travel);
            Assert.InRange(travel.Value, MathF.PI / 2 - 0.01f, MathF.PI / 2 + 0.01f);
            Assert.InRange(Number(readings["Altitude"]), height - 15f, height + 1f); // its lowest point, under its centre of mass
        }
    }

    [Fact]
    public void A_moon_orbit_out_past_where_it_would_leave_the_moons_pull_shows_no_lap()
    {
        // From 400 m over the moon's highest ground, an ellipse topping out at 0.9 and at 1.2 times
        // the radius the moon's pull is left at: the first a lap round the moon, the second never
        // flown (it leaves into the planet's pull on the way up).
        var leave = (double)Moon.SphereRadius * Scene.SphereLeaveFactor;
        foreach (var (share, leaves) in new[] { (0.9, false), (1.2, true) })
        {
            var orbit = InOrbit.Round(Moon, 0, 400f);
            var centre = orbit.CentreOfMass.ToSingle();
            var (r, top) = ((double)(centre - Moon.Centre).Length(), leave * share);
            var speed = (float)Math.Sqrt(Moon.Mu * (2 / r - 2 / (r + top)));
            // As one that reached orbit round the planet before it came (its small view's line would say so).
            var (sim, v) = Flying(orbit with { Velocity = Moon.OverGround(centre, Moon.East(centre) * speed), AchievedOrbit = true });
            using (sim)
            {
                var readings = Readout.FlightReadings(sim.Flight);
                var status = Hud.Status(v, brief: false).Text;
                output.WriteLine($"topping {share:0.0} times the leaving radius ({leave:0} m from its centre): apoapsis {v.Apoapsis:0} m over its datum, Apoapsis = {readings["Apoapsis"]}, Period = {readings.GetValueOrDefault("Period") ?? "(none)"}; its view's line \"{status}\"");
                Assert.Equal(leaves, v.LeavesItsPull);
                Assert.Equal(leaves, sim.Flight.LeavesItsPull);
                // An orbit it never flies a lap of is not one it stays on: no "in orbit" in its small view's line.
                Assert.Equal(!leaves, v.OrbitStable);
                Assert.Equal(!leaves, status.StartsWith("in orbit"));
                Assert.Equal(!leaves, Hud.Status(v, brief: true).Text == "in orbit");
                if (leaves)
                {
                    Assert.Equal("leaves its pull", readings["Apoapsis"]);
                    Assert.DoesNotContain("Period", readings.Keys);
                    Assert.Null(sim.Flight.OrbitLap);
                }
                else
                {
                    Assert.InRange(Number(readings["Apoapsis"]), top - Moon.SeaRadius - 5, top - Moon.SeaRadius + 5);
                    var lap = Math.Tau * Math.Sqrt(Math.Pow((r + top) / 2, 3) / Moon.Mu);
                    Assert.InRange(Number(readings["Period"]), lap - 2, lap + 2);
                }
                Assert.Equal($"{v.Periapsis:0} m", readings["Periapsis"]);
            }
            // One that has never reached orbit, flown a few seconds with its engine off: the lap
            // earns it orbit, the way out of the moon's pull never does.
            var (fresh, f) = Flying(orbit with { Velocity = Moon.OverGround(centre, Moon.East(centre) * speed), AchievedOrbit = false });
            using (fresh)
            {
                fresh.Run((_, _) => default, _ => false, maxSeconds: 3);
                Assert.Equal(FlightState.Flying, f.State);
                Assert.False(f.EngineLit);
                Assert.Equal(!leaves, f.AchievedOrbit);
            }
        }
        // Round the planet, with no sphere to leave, an orbit never leaves its pull.
        using var planet = new Simulation(Mission.FreePlay, world: World(InOrbit.Round(Planet, 0, 2_000f)));
        Assert.False(planet.Flight.WorldVessels.Single(w => w.Id == 0).LeavesItsPull);
    }

    [Fact]
    public void A_mission_vessel_landed_on_the_moon_is_never_on_its_planet_site_and_measures_it_across_the_bodies()
    {
        // Launch and Land's booster, set down on the moon where the moon's frame has the landing
        // zone's own coordinates: measured round the moon's ground as the planet's site is, it would
        // be on it; across the bodies the zone is out at the planet, tens of km off.
        var mission = Mission.LaunchAndLand;
        var vehicle = mission.Vehicle;
        var site = mission.LandingSiteFor(0, vehicle.Stages.Length - 1)!.Value;
        var (standing, rotation) = OnTheMoon.Standing(Vector3.Normalize(site - Moon.Centre));
        var up = Vector3.Transform(Vector3.UnitY, rotation);
        var state = new VesselState
        {
            Id = 0, Flight = 1, Mission = mission, Vehicle = vehicle, First = 0, Last = vehicle.Stages.Length - 1,
            Fuel = [.. vehicle.Stages.Select(s => s.FuelMass * 0.2f)],
            State = FlightState.Flying, Position = standing + up * 0.5f, Rotation = rotation,
            CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(vehicle, 0, vehicle.Stages.Length - 1),
            Velocity = Vector3.Zero, AngularVelocity = Vector3.Zero, Aim = up, LegsWanted = true, LegDeploy = 1f,
            HasBeenAirborne = true, MaxAltitude = 10f, Body = Moon.Name,
        };
        var (sim, v) = Flying(state);
        using (sim)
        {
            Assert.Equal(site, v.LandingSite);
            for (var i = 0; i < 60 * 15 && v.State != FlightState.Landed; i++)
                sim.Step(default);
            Assert.Equal(FlightState.Landed, v.State);
            var t = sim.Flight.WorldTime;
            var across = Vector3.Distance(v.Position, Bodies.Change(Planet, Moon, t).Point((Vector3Double)site).ToSingle());
            var roundTheMoon = Moon.Radius * Moon.AngleBetween(v.Position, site);
            output.WriteLine($"landed at {v.TouchdownSpeed:0.0} m/s; the zone {v.DistanceToLandingSite:0} m off across the bodies ({across:0} m), {roundTheMoon:0.0} m round the moon's ground by its coordinates");
            Assert.True(roundTheMoon < Scene.LandingTargetRadius, "the test's place isn't on the zone's coordinates");
            Assert.False(v.LandedOnTarget);
            Assert.InRange(v.DistanceToLandingSite, across - 1f, across + 1f);
            Assert.True(v.DistanceToLandingSite > 100 * Scene.LandingTargetRadius);
            Assert.Equal(0, Mission.TargetLandingPoints(v));
            Assert.Equal(Bodies.Change(Planet, Moon, t).Point((Vector3Double)site).ToSingle(), v.LandingSiteHere);
        }
    }

    [Fact]
    public void On_the_planet_the_frames_and_readout_are_as_today()
    {
        Assert.Equal(Scene.AtmosphereHeight, Planet.OrbitFloor);
        Assert.Equal(Frame.Orbital, Frames.Auto(Planet, Frame.Surface, Scene.AtmosphereHeight + Scene.FrameOrbitMargin + 1f, false));
        Assert.Equal(Frame.Surface, Frames.Auto(Planet, Frame.Surface, Scene.AtmosphereHeight + Scene.FrameOrbitMargin, false));
        Assert.Equal(Frame.Orbital, Frames.Auto(Planet, Frame.Orbital, Scene.AtmosphereHeight, false));
        Assert.Equal(Frame.Surface, Frames.Auto(Planet, Frame.Orbital, Scene.AtmosphereHeight - 1f, false));

        // On the pad: the planet's.
        using (var sim = new Simulation(Mission.FreePlay))
        {
            var flight = sim.Flight;
            var readings = Readout.FlightReadings(flight);
            Assert.Equal("Planet", readings["Body"]);
            Assert.Equal("Surface", readings["Frame"]);
            Assert.Equal($"{flight.Altitude:0} m", readings["Altitude"]);
            Assert.InRange(Number(readings["Altitude"]), Scene.HomeElevation - 1f, Scene.HomeElevation + 3f);
            Assert.Equal("-90 °", readings["Planet height"]);
            Assert.Equal($"{Planet.Altitude(flight.Position):0} m", readings["Planet"]);
            // The moon by its centre and Home's up: its datum's distance, and its centre's height over Home's horizon.
            var toMoon = SolarSystem.MoonCentre(flight.WorldTime) - flight.Position;
            Assert.Equal($"{toMoon.Length() - Scene.MoonRadius:0} m", readings["Moon"]);
            Assert.Equal($"{float.RadiansToDegrees(MathF.Asin(Math.Clamp(Vector3.Dot(Planet.Up(flight.Position), toMoon) / toMoon.Length(), -1f, 1f))):0} °", readings["Moon height"]);
            Assert.Equal(Sun.Clock(Sun.LocalSeconds(Planet, flight.Position, flight.WorldTime)), readings["Local time"]);
            Assert.Equal("Home's pad", readings["Pad lights"].Split(", ")[1]);
            Assert.True(Number(readings["Air"]) > 90f, $"the air {readings["Air"]} at the pad");
        }

        // In a planet orbit: the orbit round the planet, its floor the air's top.
        var (orbit, v) = Flying(InOrbit.Round(Planet, 0, 2_000f));
        using (orbit)
        {
            var readings = Readout.FlightReadings(orbit.Flight);
            var height = Scene.AtmosphereHeight + 2_000f;
            Assert.Equal("Planet", readings["Body"]);
            Assert.Equal("Orbit", readings["Frame"]);
            Assert.InRange(Number(readings["Periapsis"]), height - 3f, height + 3f);
            var lap = Math.Tau * Math.Sqrt(Math.Pow(Planet.SeaRadius + height, 3) / Planet.Mu);
            Assert.InRange(Number(readings["Period"]), lap - 2, lap + 2);
            Assert.InRange(Number(readings["Orbit speed needed"]), Planet.CircularSpeed(height) - 1f, Planet.CircularSpeed(height) + 1f);
            Assert.True(v.OrbitStable);
        }
    }
}
