using System.Numerics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The sea ice in the two icy seas: a frozen sheet (<see cref="SurfaceKind.PackIce"/>, ground a
/// little above the sea) round each one's centre, and a band of floes (<see cref="SurfaceKind.FloeField"/>,
/// water) round that. Coming down on the sheet is landing on the ground, and at its edge nothing
/// sinks into it. And the places on the ice other tests start from.
/// </summary>
public sealed class IceTests(ITestOutputHelper output)
{
    /// <summary>An icy sea's centre (its <c>at</c>), and how far its sheet (<c>pack</c>) and then its floes (<c>floes</c>) reach, from the generator's layout in the repo.</summary>
    internal static (Vector3 Centre, float Pack, float Floes) Layout(string sea) =>
        Seas.Value.TryGetValue(sea, out var ice) ? ice : throw new ArgumentException($"{sea} has no ice in the layout");

    /// <summary>Every sea in the layout with ice, by name.</summary>
    internal static readonly Lazy<Dictionary<string, (Vector3 Centre, float Pack, float Floes)>> Seas = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "tools", "TerrainGen", "layout.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "tools", "TerrainGen", "layout.json")), options);
        var seas = new Dictionary<string, (Vector3, float, float)>();
        foreach (var sea in json.RootElement.GetProperty("seas").EnumerateArray())
        {
            if (!sea.TryGetProperty("ice", out var ice)) continue;
            var at = sea.GetProperty("at");
            var centre = CubeSphere.FromLatLon(float.DegreesToRadians(at[0].GetSingle()), float.DegreesToRadians(at[1].GetSingle()));
            seas[sea.GetProperty("name").GetString()!] = (centre, ice.GetProperty("pack").GetSingle(), ice.GetProperty("floes").GetSingle());
        }

        return seas;
    });

    /// <summary>The direction <paramref name="metres"/> from <paramref name="from"/> along the ground, on a bearing (degrees clockwise from north).</summary>
    internal static Vector3 Travel(Vector3 from, float bearingDegrees, float metres)
    {
        var east = Vector3.Normalize(Vector3.Cross(new Vector3(0, 0, -1), from)); // as Planet.East
        var north = Vector3.Cross(from, east);
        var b = bearingDegrees * MathF.PI / 180f;
        var heading = north * MathF.Cos(b) + east * MathF.Sin(b);
        var angle = metres / Scene.PlanetRadius;
        return Vector3.Normalize(from * MathF.Cos(angle) + heading * MathF.Sin(angle));
    }

    /// <summary>
    /// The bearing the edge and band helpers search along, out from the sea's centre: north. In
    /// both icy seas the sheet's edge runs straight across there for a booster's width, so a foot
    /// out over it is over the water and the rest of it is on the ice (on a tongue or in a bight,
    /// the feet either side would be over the water too, or on the ice).
    /// </summary>
    internal const float SearchBearing = 0f;

    /// <summary>How far (metres) out from <paramref name="sea"/>'s centre, on <paramref name="bearing"/>, the sheet ends: where the ground first goes under the sea.</summary>
    internal static float EdgeDistance(string sea, float bearing = SearchBearing)
    {
        var (centre, pack, _) = Layout(sea);
        float inside = 0f, outside = pack * 2f;
        for (var d = 0f; d <= pack * 2f; d += 5f)
        {
            if (Terrain.IsSea(Travel(centre, bearing, d)))
            {
                outside = d;
                break;
            }

            inside = d;
        }

        for (var k = 0; k < 30; k++)
        {
            var mid = 0.5f * (inside + outside);
            if (Terrain.IsSea(Travel(centre, bearing, mid))) outside = mid;
            else inside = mid;
        }

        return inside;
    }

    /// <summary>
    /// A unit direction on <paramref name="sea"/>'s frozen sheet 3 m in from its edge, out from the
    /// sea's centre on <paramref name="bearing"/>: a booster standing there with a leg pointing
    /// out (<see cref="Outward"/>) has that foot (4.9 m out) over the water.
    /// </summary>
    internal static Vector3 SheetEdge(string sea, float bearing = SearchBearing) => Travel(Layout(sea).Centre, bearing, EdgeDistance(sea, bearing) - 3f);

    /// <summary>The way out from <paramref name="sea"/>'s centre at <paramref name="at"/>, along the ground on <paramref name="bearing"/>: towards the sheet's edge and the water.</summary>
    internal static Vector3 Outward(string sea, Vector3 at, float bearing = SearchBearing)
    {
        var centre = Layout(sea).Centre;
        var further = Travel(centre, bearing, Scene.PlanetRadius * MathF.Acos(Math.Clamp(Vector3.Dot(centre, at), -1f, 1f)) + 1f);
        return Vector3.Normalize(further - at);
    }

    /// <summary>A unit direction in the middle of <paramref name="sea"/>'s frozen sheet: its centre.</summary>
    internal static Vector3 SheetMiddle(string sea) => Layout(sea).Centre;

    /// <summary>A unit direction in the middle of <paramref name="sea"/>'s floe band, out from the sheet's edge on the search bearing.</summary>
    internal static Vector3 FloeBand(string sea)
    {
        var (centre, _, floes) = Layout(sea);
        var edge = EdgeDistance(sea);
        var end = edge;
        while (end < edge + floes * 2f && Terrain.Kind(Travel(centre, SearchBearing, end + 2f)) is SurfaceKind.FloeField or SurfaceKind.PackIce) end += 2f;
        return Travel(centre, SearchBearing, 0.5f * (edge + end));
    }

    /// <summary>The point on the ground (the ice) in <paramref name="direction"/>.</summary>
    private static Vector3 Point(Vector3 direction) => Planet.Centre + direction * (Planet.SeaRadius + Terrain.Height(direction));

    private static Vessel Dropped(Simulation sim) => sim.Flight.WorldVessels.First(v => v.Id == 0);

    private static readonly Pilot Nobody = (_, _) => default;

    [Fact]
    public void A_booster_lands_on_the_frozen_sheet()
    {
        var at = Point(SheetMiddle("Boreal Sea"));
        Assert.Equal(SurfaceKind.PackIce, Terrain.Kind(Planet.Up(at)));
        using var sim = Drops.At(at, Planet.East(at), 2f);
        var vessel = Dropped(sim);
        Assert.True(sim.Run(Nobody, _ => vessel.State != FlightState.Flying, maxSeconds: 30));
        output.WriteLine($"{vessel.State} after {sim.Time:0.0} s, touching down at {vessel.TouchdownSpeed:0.0} m/s, {vessel.Craft.Submerged:0.00} submerged; {vessel.CrashReason}");
        Assert.Equal(FlightState.Landed, vessel.State);
        Assert.True(vessel.TouchdownSpeed < Scene.SafeTouchdownSpeed, $"it touched down at {vessel.TouchdownSpeed} m/s");
        Assert.Equal(0f, vessel.Craft.Submerged);
    }

    [Fact]
    public void A_hard_landing_on_the_ice_is_a_crash()
    {
        var at = Point(SheetMiddle("Boreal Sea"));
        using var sim = Drops.At(at, Planet.East(at), 9f);
        var vessel = Dropped(sim);
        Assert.True(sim.Run(Nobody, _ => vessel.State != FlightState.Flying, maxSeconds: 30));
        output.WriteLine($"{vessel.State} after {sim.Time:0.0} s; {vessel.CrashReason}");
        Assert.Equal(FlightState.Crashed, vessel.State);
        Assert.StartsWith("Hit the ground", vessel.CrashReason);
    }

    [Fact]
    public void Standing_at_the_ice_edge_it_stands_or_falls_but_never_falls_through()
    {
        var (state, reason) = AtTheEdge("Boreal Sea", SearchBearing);
        if (state == FlightState.Crashed)
            Assert.StartsWith("Hit the ground at", reason); // gone over: wrecked only by how hard it came down
    }

    [Fact]
    public void All_round_both_sheets_a_booster_at_the_edge_stands_or_falls_and_never_sinks_in()
    {
        // Eight bearings round each sheet, its corners, tongues and bights among them: wherever
        // it is set down 3 m in from the edge, a leg out over the water, it ends with a verdict by
        // the ground's rules (standing or lying down, or crashed: its side or a leg coming down on the ice
        // too fast as it tips), however it lies, and nothing of it sinks into the ice.
        var verdicts = new List<string>();
        foreach (var sea in new[] { "Boreal Sea", "Austral Sea" })
        for (var bearing = 0f; bearing < 360f; bearing += 45f)
        {
            var (state, reason) = AtTheEdge(sea, bearing);
            verdicts.Add($"{sea} {bearing}°: {state} {reason}");
        }

        output.WriteLine(string.Join(Environment.NewLine, verdicts));
    }

    [Fact]
    public void Lying_across_the_ice_edge_the_ground_stays_under_it()
    {
        // West of the Austral Sea's centre, a booster with its legs stowed laid on its side across
        // the edge: its bell end on the ice, its middle and top out over the sheet's foot and the
        // water. The ice stays solid under its bell end, and lying still on its body, set down
        // gently, it has landed. (Once the sheet ended in a cliff over deep water, and a booster lying so,
        // its middle out of the ground tiles' reach, sank in until its ends were measured too; now
        // the sheet's foot is a few metres under its middle, so this passes even without that:
        // TerrainTileTests.An_upright_craft_gets_its_tiles_by_its_bottom_ends_height checks it.) It is watched for half a minute, past the verdict (a landed vessel stays in the
        // world), whatever its state, wet or not.
        const string sea = "Austral Sea";
        const float bearing = 270f;
        var edge = SheetEdge(sea, bearing);
        var ground = Point(edge);
        var up = Planet.Up(ground);
        var outward = Outward(sea, edge, bearing);
        var vehicle = Vehicle.Booster;
        var stage = vehicle.Stages[0];
        // Its axis along the ground, out over the edge: upright, then turned a quarter about the way across.
        var rotation = Quaternion.Concatenate(Planet.UprightAt(ground), Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(up, outward)), MathF.PI / 2f));
        // Its body's bottom 2 m further in than that (5 m in from the edge), its bell on the ice behind it, just clear of it.
        var position = ground - outward * 2f + up * (stage.BellRadius + 0.3f);
        var state = new VesselState
        {
            Id = 0, Flight = 0, Mission = Mission.LaunchAndLand, Vehicle = vehicle, First = 0, Last = 0,
            Fuel = [stage.FuelMass * 0.3f],
            State = FlightState.Flying, Position = position, Rotation = rotation,
            CentreOfMassOffset = RocketGeometry.CentreOfMassOffset(vehicle, 0, 0),
            Velocity = Vector3.Zero, AngularVelocity = Vector3.Zero,
            Aim = outward, MaxAltitude = 400f, HasBeenAirborne = true,
        };
        using var sim = new Simulation(world: new WorldState { Vessels = [state], NextVesselId = 1 });
        var vessel = Dropped(sim);
        sim.Step(new FlightInput(SwitchTo: vessel));
        var middle = Vector3.Transform(new Vector3(0f, RocketGeometry.StackHeight(vehicle, 0, 0) / 2f, 0f), vessel.Model);
        Assert.True(Terrain.IsSea(Planet.Up(middle)), "its middle isn't out over the water");
        var deepest = float.MaxValue;
        var verdict = double.NaN;
        sim.Run(Nobody, _ =>
        {
            deepest = MathF.Min(deepest, IntoTheIce(sim, vessel));
            if (double.IsNaN(verdict) && vessel.State != FlightState.Flying) verdict = sim.Time;
            return false;
        }, maxSeconds: 30);
        output.WriteLine($"{vessel.State} after {verdict:0.0} s ({vessel.CrashReason}), leaning {vessel.TiltDegrees:0}°, {vessel.Craft.Submerged:0.00} submerged; " +
                         $"over 30 s its lowest point over the sheet came to {deepest:0.00} m from the ice under it");
        Assert.True(deepest < float.MaxValue, "nothing of it was over the sheet");
        Assert.Equal(FlightState.Landed, vessel.State);
    }

    /// <summary>
    /// A booster dropped at 1 m/s at <see cref="SheetEdge"/> on <paramref name="bearing"/>, its
    /// first leg pointing out over the water, run until it has landed or crashed (which it must
    /// within 60 s), checking all along that none of it sinks into the ice. Returns how it ended.
    /// </summary>
    private (FlightState State, string? Reason) AtTheEdge(string sea, float bearing)
    {
        var edge = SheetEdge(sea, bearing);
        using var sim = Drops.At(Point(edge), Outward(sea, edge, bearing), 1f);
        var vessel = Dropped(sim);
        var deepest = float.MaxValue;
        var ended = sim.Run(Nobody, _ =>
        {
            deepest = MathF.Min(deepest, NeverIntoTheIce(sim, vessel));
            return vessel.State is FlightState.Landed or FlightState.Crashed;
        }, maxSeconds: 60);
        output.WriteLine($"{sea}, {EdgeDistance(sea, bearing):0.0} m out on a bearing of {bearing}° (3 m in from the edge, the ice " +
                         $"{Terrain.Height(edge):+0.00;-0.00} m above the sea): {vessel.State} after {sim.Time:0.0} s, leaning {vessel.TiltDegrees:0}°, " +
                         $"{vessel.Craft.Submerged:0.00} submerged; out of the water, its lowest point over the sheet came to {deepest:+0.00;-0.00} m from the ice under it; {vessel.CrashReason}");
        Assert.True(ended, $"{sea} on a bearing of {bearing}°: after 60 s it is {vessel.State}, leaning {vessel.TiltDegrees:0}°");
        return (vessel.State, vessel.CrashReason);
    }

    /// <summary>
    /// Out of the water, no point of it (its feet, its bell's rim, and its hull all round, a metre
    /// apart down its length) is more than 1 m into the ice wherever it is over the sheet (a point
    /// out over the water has nothing under it to sink into). Returns the furthest into the ice any
    /// is (negative: into it), or <see cref="float.MaxValue"/> if none is over the sheet.
    /// </summary>
    private static float NeverIntoTheIce(Simulation sim, Vessel vessel) =>
        vessel.Craft.Submerged > 0f || vessel.State == FlightState.Crashed ? float.MaxValue : IntoTheIce(sim, vessel);

    /// <summary>
    /// As <see cref="NeverIntoTheIce"/>, whatever its state and wet or not: no point of it over the sheet
    /// more than 1 m into the ice. Returns the furthest into the ice any is, or <see cref="float.MaxValue"/>.
    /// </summary>
    private static float IntoTheIce(Simulation sim, Vessel vessel)
    {
        var deepest = float.MaxValue;
        var model = vessel.Model;
        var stage = vessel.CurrentStage;
        void Check(Vector3 point)
        {
            var under = Planet.Up(point);
            if (Terrain.IsSea(under)) return;
            var into = Planet.Altitude(point) - Terrain.Height(under);
            deepest = MathF.Min(deepest, into);
            Assert.True(into > -1f, $"a point of it is {-into:0.00} m into the ice at {sim.Time:0.0} s");
        }

        if (vessel.LegDeploy > 0.99f) // its feet, where they stand when its legs are out (stowed, they are along its body)
            for (var leg = 0; leg < RocketGeometry.LegCount; leg++)
                Check(Vector3.Transform(RocketGeometry.Foot(LegGeometry.Rocket, leg), model));
        for (var i = 0; i < 8; i++)
            Check(Vector3.Transform(RocketGeometry.BellRim(stage, i, 8), model));
        var axis = vessel.Up;
        var a = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
        var b = Vector3.Cross(axis, a);
        for (var y = 0f; y <= vessel.Height; y += 1f)
        for (var k = 0; k < 8; k++)
        {
            var (s, c) = MathF.SinCos(MathF.Tau * k / 8);
            Check(Vector3.Transform(new Vector3(0, y, 0), model) + (a * c + b * s) * stage.Radius);
        }

        return deepest;
    }
}
