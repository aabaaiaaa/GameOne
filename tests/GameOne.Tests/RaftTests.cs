using System.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>Rafts (<see cref="Raft"/>, <see cref="Rafts"/>): the drone ship is one, a floe another, each measured over its own footprint.</summary>
public class RaftTests
{
    [Fact]
    public void A_disc_raft_is_measured_over_its_disc()
    {
        // A disc 10 m round on the sea 300 m north of the ship (open water, as the ship is).
        var direction = Vector3.Normalize(Scene.DroneShipCentre + Planet.North(Scene.DroneShipCentre) * 300f - Planet.Centre);
        var centre = Planet.Centre + direction * Planet.SeaRadius;
        var raft = new Raft(1, RaftShape.Disc, centre, Planet.East(centre), halfLength: 10f, halfBeam: 10f, freeboard: 0.5f, draft: 2f);
        raft.Set(23.0);

        var normal = raft.Normal;
        var x = Vector3.Transform(Vector3.UnitX, raft.Current.Rotation);
        var z = Vector3.Transform(Vector3.UnitZ, raft.Current.Rotation);
        var top = raft.Current.Position + normal * raft.Freeboard;
        Assert.Equal(2f, raft.HeightAbove(top + normal * 2f)!.Value, 0.01f);
        Assert.NotNull(raft.HeightAbove(top + normal + x * 9.9f));             // just inside its rim
        Assert.Null(raft.HeightAbove(top + normal + x * 10.1f));               // just outside it
        Assert.NotNull(raft.HeightAbove(top + normal + (x + z) * 7f));         // 9.9 m out, between its axes
        Assert.Null(raft.HeightAbove(top + normal + (x + z) * 7.2f));          // 10.2 m: round, not square
        Assert.Null(raft.HeightAbove(top - normal * (raft.Freeboard + raft.Draft + 1f))); // under its bottom
    }

    [Fact]
    public void The_ship_is_the_first_raft()
    {
        Assert.Equal(0, Rafts.Ship.Id);
        Assert.Equal(RaftShape.Box, Rafts.Ship.Shape);
        Assert.Equal(Scene.DroneShipHalfLength, Rafts.Ship.HalfLength);
        Rafts.Ship.Set(5.0);
        var over = Rafts.Ship.Current.Position + Rafts.Ship.Normal * (Scene.DroneShipFreeboard + 1f);
        Assert.Same(Rafts.Ship, Rafts.Under(over)?.Raft);
        Assert.Equal(1f, Rafts.Under(over)!.Value.Height, 0.01f);
        Assert.Null(Rafts.Under(over + Planet.North(over) * 100f)); // the sea beside it
    }

    [Fact]
    public void There_are_drone_ships_off_Home_and_off_Sentinel()
    {
        // Every site's ships, site by site in their order: Home's two, then Sentinel's one.
        var ships = LaunchSites.All.SelectMany(s => s.DroneShips.Select(centre => (s.Name, Centre: centre))).ToArray();
        Assert.Equal(["Home", "Home", "Sentinel"], ships.Select(s => s.Name));
        Assert.Equal(ships.Length, Rafts.Ships.Count);
        for (var i = 0; i < ships.Length; i++)
        {
            var ship = Rafts.Ships[i];
            Assert.True(ship.IsShip);
            Assert.Equal(RaftShape.Box, ship.Shape);
            Assert.True(Vector3.Distance(ships[i].Centre, ship.Pose(0).Position) < 5f, $"{ships[i].Name}'s ship {i} isn't where it should be");
            Assert.True(Terrain.IsSea(Planet.Up(ship.Pose(0).Position)), $"{ships[i].Name}'s ship {i} is on land");
            // Its length running east, as Home's first does.
            Assert.True(Vector3.Dot(Vector3.Transform(Vector3.UnitX, ship.Pose(0).Rotation), Planet.East(ships[i].Centre)) > 0.99f);
            ship.Set(5.0);
            var over = ship.Current.Position + ship.Normal * (Scene.DroneShipFreeboard + 1f);
            Assert.Same(ship, Rafts.Under(over)?.Raft);
        }
        Assert.Same(Rafts.Ship, Rafts.Ships[0]);
        Assert.All(Floes.Near(Planet.Centre + Planet.Up(Planet.SurfacePoint(0f, 0f)) * Planet.Radius, float.MaxValue).Take(50), floe => Assert.False(floe.IsShip));
    }
}
