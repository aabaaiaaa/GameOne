using System.Numerics;
using GameOne.Physics;
using Xunit;
using Xunit.Abstractions;

namespace GameOne.Tests;

/// <summary>
/// The test pads' collider (<see cref="Plinth.Columns"/> under a slab, built by
/// <c>RocketPhysics.AddTiltedZone</c>) against the drawn plinth and <see cref="Planet.SurfaceAltitude"/>:
/// its boxes inside the drawn wall and reaching up into the slab, measured in plan; and, through
/// Jolt itself, a box dropped on each pad resting on its top, and one dropped beside it on the ground.
/// </summary>
public sealed class TestPadTests(ITestOutputHelper output)
{
    private static IEnumerable<(string Name, Plinth Plinth)> Pads() =>
        Scene.TestPads.Select((pad, i) => (pad.Name, Foundations.LandingZonePlinth[1 + i]));

    [Fact]
    public void Each_test_pads_collider_lies_inside_its_drawn_plinth_and_meets_its_slab()
    {
        float worstOut = 0f, worstGap = 0f, worstSlot = float.MaxValue;
        foreach (var (name, plinth) in Pads())
        {
            var inverse = Quaternion.Inverse(plinth.Frame);
            Vector2 Plan(Vector3 world)
            {
                var local = Vector3.Transform(world - plinth.Centre, inverse);
                return new Vector2(local.X, local.Z);
            }

            // The drawn wall: the mesh's 64 sides, each between two rim points straight down.
            const int slices = 64;
            var rim = new Vector2[slices];
            for (var k = 0; k < slices; k++)
            {
                var (s, c) = MathF.SinCos(MathF.Tau * k / slices);
                rim[k] = Plan(plinth.Centre + Vector3.Transform(new Vector3(c, 0f, s) * Scene.LandingZoneRadius + new Vector3(0, Scene.PadTop, 0), plinth.Tilt));
            }
            var area = 0f;
            for (var k = 0; k < slices; k++)
                area += rim[k].X * rim[(k + 1) % slices].Y - rim[(k + 1) % slices].X * rim[k].Y;
            var turn = MathF.Sign(area);

            // How far a point is outside the drawn wall (negative: inside), the polygon being convex.
            float Outside(Vector2 p)
            {
                var most = float.MinValue;
                for (var k = 0; k < slices; k++)
                {
                    var edge = rim[(k + 1) % slices] - rim[k];
                    var outward = Vector2.Normalize(new Vector2(edge.Y, -edge.X) * turn);
                    most = MathF.Max(most, Vector2.Dot(p - rim[k], outward));
                }
                return most;
            }

            var columns = plinth.Columns;
            float Uncovered(Vector2 p) => columns.Min(c =>
            {
                var dx = MathF.Max(MathF.Max(c.X0 - p.X, p.X - c.X1), 0f);
                var dz = MathF.Max(MathF.Abs(p.Y) - c.HalfAlong, 0f);
                return MathF.Sqrt(dx * dx + dz * dz);
            });

            var (out_, gap, slot) = (0f, 0f, float.MaxValue);
            var thick = Plinth.SlabThickness / MathF.Cos(plinth.Slope); // the slab, measured straight up
            foreach (var c in columns)
            {
                foreach (var corner in (ReadOnlySpan<Vector2>)[new(c.X0, c.HalfAlong), new(c.X0, -c.HalfAlong), new(c.X1, c.HalfAlong), new(c.X1, -c.HalfAlong)])
                    out_ = MathF.Max(out_, Outside(corner));
                // Under the top everywhere (it is lowest at the column's downhill side) ...
                Assert.True(c.Top < plinth.TopAt(c.X1), $"{name}: a column stands proud of the top");
                // ... and into the slab even at its uphill side, where the top is highest.
                slot = MathF.Min(slot, c.Top - (plinth.TopAt(c.X0) - thick));
            }
            for (var k = 0; k < slices; k++)
                for (var t = 0f; t < 1f; t += 0.05f)
                    gap = MathF.Max(gap, Uncovered(Vector2.Lerp(rim[k], rim[(k + 1) % slices], t)));

            output.WriteLine($"{name}: {columns.Count} columns; out past the drawn wall {out_ * 100:0.0} cm, the wall's furthest from a column {gap * 100:0.0} cm, the columns' tops {slot * 100:0.0} cm into the slab at least");
            (worstOut, worstGap, worstSlot) = (MathF.Max(worstOut, out_), MathF.Max(worstGap, gap), MathF.Min(worstSlot, slot));
        }

        // The columns' outer corners are on the ellipse, so at most the 64-gon's sag (2 cm) outside its chords.
        Assert.True(worstOut <= 0.03f, $"a column stands {worstOut} m out past the drawn wall");
        Assert.True(worstGap <= Plinth.ColumnFit + 0.03f, $"the drawn wall is {worstGap} m from the nearest column");
        Assert.True(worstSlot >= 0.019f, $"a column's top reaches only {worstSlot} m into the slab");
    }

    // The probe: a fairing half, whose collider is one box 2.4 m across (x), 5.4 m long (y) and
    // 1.2 m thick (z), from its origin up y and out along +z (see RocketPhysics.CreateShape).
    private static readonly float ProbeWidth = Scene.RocketRadius, ProbeLength = Scene.FairingHeight + Scene.FairingNoseHeight;

    /// <summary>The probe lying flat, its bottom face on the plane with normal <paramref name="normal"/>, its length along <paramref name="along"/>.</summary>
    private static Quaternion Lying(Vector3 normal, Vector3 along) =>
        Quaternion.CreateFromRotationMatrix(Rows(Vector3.Cross(along, normal), along, normal));

    private static Matrix4x4 Rows(Vector3 x, Vector3 y, Vector3 z) =>
        new(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f);

    /// <summary>The probe's bottom corners, in the world.</summary>
    private static Vector3[] BottomCorners(RocketPhysics physics, Craft probe)
    {
        var model = physics.Model(probe);
        return [.. ((ReadOnlySpan<Vector3>)[new(-ProbeWidth, 0, 0), new(ProbeWidth, 0, 0), new(-ProbeWidth, ProbeLength, 0), new(ProbeWidth, ProbeLength, 0)])
            .ToArray().Select(c => Vector3.Transform(c, model))];
    }

    private static float Speed(RocketPhysics physics, Craft probe) => physics.Velocity(probe).Length();

    [Fact]
    public void A_box_dropped_on_each_test_pad_rests_on_its_top_and_one_dropped_beside_it_on_the_ground()
    {
        using var physics = new RocketPhysics(GroundBody.Planet);
        physics.Reset(Vehicle.Booster, LaunchSites.Home);
        physics.Remove(physics.Crafts[0]);
        var r = Scene.LandingZoneRadius;

        foreach (var (name, plinth) in Pads())
        {
            var steep = plinth.Slope > float.DegreesToRadians(40f); // only the 45° pad
            Vector3 Across(Vector3 local) => Vector3.Transform(local, plinth.Tilt); // a direction in the top's plane

            // On the top: at its centre, and with its end 0.5 m inside the rim, uphill and downhill.
            foreach (var (where, direction, from) in (ReadOnlySpan<(string, Vector3, float)>)
                     [("centre", Vector3.UnitX, -ProbeLength / 2), ("uphill", -Vector3.UnitX, r - 0.5f - ProbeLength), ("downhill", Vector3.UnitX, r - 0.5f - ProbeLength)])
            {
                var along = Across(direction);
                var start = plinth.Top + along * from + plinth.Normal * 0.05f;
                var probe = physics.AddLoose(Vehicle.FairingHalf, start, Lying(plinth.Normal, along), Vector3.Zero, Vector3.Zero);
                // A box slides on the 45° pad (steeper than its friction holds), so there it is
                // watched for half a second as it slides on the top; elsewhere it must come to rest.
                var seconds = steep ? 0.5f : 3f;
                var lowest = float.MaxValue;
                var highest = float.MinValue;
                for (var t = 0f; t < seconds; t += 1f / 60)
                {
                    physics.Update(1f / 60, []);
                    if (t < 0.25f) continue; // dropped from 5 cm: down by now
                    foreach (var corner in BottomCorners(physics, probe))
                    {
                        var above = Vector3.Dot(corner - plinth.Top, plinth.Normal);
                        (lowest, highest) = (MathF.Min(lowest, above), MathF.Max(highest, above));
                        Assert.True(plinth.HeightAbove(corner) is not null, $"{name}, {where}: the box has left the top");
                    }
                }
                var speed = Speed(physics, probe);
                output.WriteLine($"{name}, {where}: its bottom {lowest * 100:0.0} to {highest * 100:0.0} cm off the top, at {speed:0.000} m/s after {seconds} s");
                Assert.True(lowest >= -0.1f && highest <= 0.1f, $"{name}, {where}: the box's bottom is {lowest:0.00} to {highest:0.00} m off the top");
                if (!steep)
                    Assert.True(speed < 0.05f, $"{name}, {where}: still moving at {speed} m/s");
                physics.Remove(probe);
            }

            // Beside it, on the ground: lying level, square to the drawn wall, its end 0.3 m out
            // from it, past the downhill rim and at 30° round from it either way.
            foreach (var degrees in (ReadOnlySpan<float>)[0f, 30f, -30f])
            {
                var (s, c) = MathF.SinCos(float.DegreesToRadians(degrees));
                var rimPoint = plinth.Centre + Across(new Vector3(c, 0f, s) * r) + plinth.Normal * Scene.PadTop;
                var outward = Vector3.Normalize(Vector3.Cross(plinth.Up, Across(new Vector3(-s, 0f, c)))); // the wall's normal, as drawn
                var under = Planet.Up(rimPoint);
                var ground = Planet.Centre + under * (Planet.SeaRadius + Terrain.SurfaceHeight(under));
                var start = ground + outward * 0.3f + plinth.Up * 0.1f;
                var probe = physics.AddLoose(Vehicle.FairingHalf, start, Lying(plinth.Up, outward), Vector3.Zero, Vector3.Zero);
                for (var t = 0f; t < 3f; t += 1f / 60)
                    physics.Update(1f / 60, []);
                var heights = BottomCorners(physics, probe).Select(p => Planet.Altitude(p) - Terrain.SurfaceHeight(Planet.Up(p))).ToArray();
                output.WriteLine($"{name}, {degrees}° beside the downhill rim: its bottom {heights.Min() * 100:0.0} to {heights.Max() * 100:0.0} cm off the ground");
                Assert.True(heights.Min() >= -0.1f && heights.Max() <= 0.1f, $"{name}, {degrees}° beside the downhill rim: the box's bottom is {heights.Min():0.00} to {heights.Max():0.00} m off the ground");
                physics.Remove(probe);
            }
        }
    }
}
