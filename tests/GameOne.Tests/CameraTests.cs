using System.Numerics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The chase camera keeps the rocket in the middle of the screen: it trails a little behind its
/// movement, but never far, however fast it goes or however far its middle jumps when it stages.
/// (It needs no window: one without mouse input is what the small views use.)
/// </summary>
public sealed class CameraTests
{
    private const float Dt = 1f / 60f;

    [Fact]
    public void A_still_camera_hands_back_the_same_view_each_frame()
    {
        var camera = new ChaseCamera(null);
        camera.Place(0.7f, 0.4f, 120f);
        var first = camera.View;
        Assert.Same(first, camera.View); // read each frame for the input: nothing allocated while it is still
        camera.Place(0.8f, 0.4f, 120f);
        Assert.NotSame(first, camera.View);
        Assert.Equal(0.8f, camera.View.Yaw);
    }

    [Fact]
    public void Reset_camera_after_a_place_brings_back_the_chase_view_as_it_was_left()
    {
        var camera = new ChaseCamera(null);
        camera.Place(0.7f, 0.4f, 120f);
        var before = camera.View;
        camera.CentreOn(new CameraCentre(CentreKind.Site, 0));
        Assert.NotEqual(before.Distance, camera.View.Distance); // looked at as a place is
        camera.CentreOn(new CameraCentre(CentreKind.Site, 1));  // and on to another place
        camera.CentreOn(CameraCentre.Own);
        var after = camera.View;
        Assert.Equal((before.Yaw, before.Pitch, before.Distance), (after.Yaw, after.Pitch, after.Distance));
    }

    [Fact]
    public void A_view_read_from_the_camera_applies_back_the_same()
    {
        var camera = new ChaseCamera(null);
        var focus = Scene.RocketStart + new Vector3(0, 100f, 0);
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        camera.Place(0.7f, 0.4f, 120f);
        camera.ToggleZoomOut(5_000f);
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        var v = camera.View;
        Assert.True(v.Map);
        Assert.Equal(CameraCentre.Own, v.Centre);

        var other = new ChaseCamera(null);
        other.Apply(v);
        other.Update(Dt, focus, null, GroundBody.Planet, 0);
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        Assert.Equal(v, other.View);
        Assert.Equal(camera.View, other.View);
    }

    [Fact]
    public void Flying_fast_the_camera_stays_close_behind()
    {
        var camera = new ChaseCamera(null);
        var focus = Scene.RocketStart + new Vector3(0, 3000f, 0);
        var velocity = new Vector3(150f, 60f, 0f); // about as fast as the upper stage climbs
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        for (var i = 0; i < 300; i++)
        {
            focus += velocity * Dt;
            camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        }
        var lag = Vector3.Distance(camera.Target, focus);
        Assert.True(lag <= Scene.CameraMaxLag + 1e-3f, $"the camera trails {lag:0.0} m behind");
        Assert.True(lag > 0.5f, "it should still trail a little, so speed can be felt");
    }

    [Fact]
    public void When_the_middle_of_the_rocket_jumps_as_it_stages_the_camera_keeps_up()
    {
        var camera = new ChaseCamera(null);
        var focus = Scene.RocketStart + new Vector3(0, 1500f, 0);
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        focus += new Vector3(0, 14f, 0); // from the middle of the whole stack to the middle of the upper stage
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        Assert.True(Vector3.Distance(camera.Target, focus) <= Scene.CameraMaxLag + 1e-3f);
    }

    [Fact]
    public void Over_the_roughest_sea_the_camera_keeps_clear_of_the_highest_crest()
    {
        // Something sinking out of sight in the Austral Sea: the camera, behind it and a little
        // above, would be under the water, so it is lifted to keep its clearance over the waves
        // (as high as they can reach there, whatever the moment), not just over the calm sea.
        var (at, _) = SplashdownTests.RoughWater.Value;
        var camera = new ChaseCamera(null);
        var focus = at - Planet.Up(at) * 20f;
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        var up = Planet.Up(camera.Position);
        var crest = Sea.WaveScale(up) * 0.5f;
        var over = Planet.Altitude(camera.Position) - crest;
        Assert.True(Sea.WaveScale(up) > 2f, $"the waves where the camera is are only {Sea.WaveScale(up):0.00} m");
        Assert.True(over >= 1.5f - 1e-3f, $"the camera is only {over:0.00} m above the highest the waves reach there");
    }

    [Fact]
    public void Placed_the_camera_looks_from_the_angles_and_distance_given()
    {
        // As a test sets a view up, with no mouse to drag it: it is where the angles say, at once,
        // and held to the wheel's limits.
        var camera = new ChaseCamera(null);
        var focus = Scene.RocketStart + new Vector3(0, 3000f, 0);
        camera.Place(yaw: 0f, pitch: 0.3f, distance: 60f);
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        Assert.Equal(60f, Vector3.Distance(camera.Position, focus), 0.01f);
        Assert.Equal(MathF.Sin(0.3f), Vector3.Dot(Vector3.Normalize(camera.Position - focus), Planet.Up(focus)), 0.001f);
        Assert.Equal(0f, Vector3.Dot(camera.Position - focus, Planet.East(focus)), 0.01f); // yaw 0: due south of it
        camera.Place(0f, 0.3f, 1e6f);
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        Assert.Equal(Scene.CameraMaxDistance, Vector3.Distance(camera.Position, focus), 1f); // clamped as the wheel is
    }

    [Fact]
    public void On_a_steep_mountainside_the_camera_keeps_clear_of_the_slope_beside_it()
    {
        // Polar's pad is cut into a mountainside. A camera kept 1.5 m above the ground straight
        // under it there is much nearer the slope rising beside it, near enough for the near plane
        // to cut into the hill. Put it on the steepest ground by the pad (the vessel's middle
        // where its default angle and distance put the camera there) and measure.
        var site = LaunchSites.All.First(s => s.Name == "Polar");
        var (spot, slope) = Steepest(site);
        Assert.True(slope > 55f, $"the steepest ground by Polar's pad is only {slope:0}°");

        var up = Planet.Up(spot);
        var offset = (-Planet.North(spot) * MathF.Cos(float.DegreesToRadians(Scene.CameraPitchDegrees))
            + up * MathF.Sin(float.DegreesToRadians(Scene.CameraPitchDegrees))) * Scene.CameraDistance;
        var focus = spot + up * 1.5f - offset;
        var camera = new ChaseCamera(null);
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);

        var clear = NearestGround(camera.Position, 3f);
        Assert.True(clear >= 1.2f, $"the camera is only {clear:0.00} m from the {slope:0}° slope beside it");
    }

    private static float DegreesBetween(Vector3 a, Vector3 b) =>
        float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)));

    private static ChaseCamera OnThePad(out Vector3 focus)
    {
        var camera = new ChaseCamera(null);
        focus = Scene.RocketStart;
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        return camera;
    }

    [Fact]
    public void A_middle_drag_turns_the_view_in_place()
    {
        var camera = OnThePad(out var focus);
        var before = camera.Position;
        camera.DragLook(new Vector2(0f, -10000f));
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        Assert.True(Vector3.Distance(before, camera.Position) < 0.001f, "the camera moved");
        Assert.True(DegreesBetween(camera.Forward, Planet.Up(camera.Position)) < 0.5f, "not looking straight up");
        Assert.True(camera.LookingFree);
    }

    [Fact]
    public void The_middle_button_drags_free_look_and_the_right_orbits()
    {
        // The window's buttons routed as the mouse's events are (ChaseCamera.TurnFor), each to the
        // button ImGui judges it by, and the drag then turned as the judged one is (Turn).
        Assert.Equal(ChaseCamera.MouseTurn.Look, ChaseCamera.TurnFor(GlfwMouseButton.Middle));
        Assert.Equal(ChaseCamera.MouseTurn.Orbit, ChaseCamera.TurnFor(GlfwMouseButton.Right));
        Assert.Null(ChaseCamera.TurnFor(GlfwMouseButton.Left));
        Assert.Equal(ImGuiMouseButton.Middle, ChaseCamera.ButtonFor(ChaseCamera.MouseTurn.Look));
        Assert.Equal(ImGuiMouseButton.Right, ChaseCamera.ButtonFor(ChaseCamera.MouseTurn.Orbit));

        var camera = OnThePad(out var focus);
        var before = camera.Position;
        camera.Turn(ChaseCamera.TurnFor(GlfwMouseButton.Middle)!.Value, new Vector2(0f, -10000f));
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        Assert.True(camera.LookingFree, "the middle drag didn't look");
        Assert.True(Vector3.Distance(before, camera.Position) < 0.001f, "the middle drag moved the camera");
        camera.Turn(ChaseCamera.TurnFor(GlfwMouseButton.Right)!.Value, new Vector2(10f, 0f));
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        Assert.False(camera.LookingFree, "the right drag didn't orbit");
    }

    [Fact]
    public void A_press_is_judged_once_imgui_has_taken_it_in_and_drags_only_off_the_panels()
    {
        // Never at its first Update (ImGui's pass before it ran before the press came), even clicked.
        Assert.Null(ChaseCamera.JudgePress(1, clicked: true, wantCapture: false));
        // Waiting while ImGui hasn't taken it in, up to the most it waits.
        for (var wait = 2; wait <= ChaseCamera.MaxJudgeWait; wait++)
            Assert.Null(ChaseCamera.JudgePress(wait, clicked: false, wantCapture: false));
        // Taken in: a drag off the panels, none on one; and past the most it waits, judged anyway.
        Assert.True(ChaseCamera.JudgePress(2, clicked: true, wantCapture: false));
        Assert.False(ChaseCamera.JudgePress(2, clicked: true, wantCapture: true));
        Assert.True(ChaseCamera.JudgePress(ChaseCamera.MaxJudgeWait + 1, clicked: false, wantCapture: false));
        Assert.False(ChaseCamera.JudgePress(ChaseCamera.MaxJudgeWait + 1, clicked: false, wantCapture: true));
    }

    [Fact]
    public void A_right_press_moved_under_the_slop_is_a_click_and_one_moved_past_it_only_turns_the_camera()
    {
        Assert.True(ChaseCamera.IsClick(3.9f));
        Assert.False(ChaseCamera.IsClick(Scene.RightClickSlopPixels));

        // A right press, the pointer moved along these steps, let go, and judged as the window's
        // events and ImGui's pass would have it (off the panels): the click it was, if any, and how
        // far the camera turned.
        (Vector2? Click, Vector3 Facing) Press(bool letGoBeforeJudged, params Vector2[] moves)
        {
            var camera = OnThePad(out var focus);
            var at = new Vector2(640f, 360f);
            camera.Pointer(at);
            camera.Button(ChaseCamera.MouseTurn.Orbit, down: true);
            foreach (var move in moves)
                camera.Pointer(at += move);
            if (letGoBeforeJudged)
                camera.Button(ChaseCamera.MouseTurn.Orbit, down: false);
            camera.JudgeMouse(orbitClicked: true, lookClicked: false, wantCapture: false); // (its first Update goes by)
            camera.JudgeMouse(orbitClicked: true, lookClicked: false, wantCapture: false);
            if (!letGoBeforeJudged)
                camera.Button(ChaseCamera.MouseTurn.Orbit, down: false);
            camera.Update(Dt, focus, null, GroundBody.Planet, 0);
            return (camera.TakeRightClick(), camera.GroundForward);
        }
        var still = OnThePad(out _).GroundForward;
        float Turned(Vector3 facing) => DegreesBetween(still, facing);

        // A wobble of 3 px, let go before or after it is judged: a click where it was let go, the camera barely turned.
        foreach (var early in (ReadOnlySpan<bool>)[true, false])
        {
            var (click, facing) = Press(early, new Vector2(2f, 0f), new Vector2(1f, 0f));
            Assert.Equal(new Vector2(643f, 360f), click);
            Assert.True(Turned(facing) < 1.1f, $"a click turned the camera {Turned(facing):0.0}°");
        }
        // Out 3 px and back (6 px moved, ending where it began): a drag, no click.
        Assert.Null(Press(false, new Vector2(3f, 0f), new Vector2(-3f, 0f)).Click);
        // A right drag of 100 px: no click, and the camera turned as any right drag turns it.
        var drag = Press(false, [.. Enumerable.Repeat(new Vector2(10f, 0f), 10)]);
        Assert.Null(drag.Click);
        var dragged = OnThePad(out var padFocus);
        dragged.DragOrbit(new Vector2(100f, 0f));
        dragged.Update(Dt, padFocus, null, GroundBody.Planet, 0);
        Assert.True(DegreesBetween(dragged.GroundForward, drag.Facing) < 0.01f, "the drag turned the camera other than a right drag does");
        Assert.Equal(float.RadiansToDegrees(100f * Scene.CameraRadiansPerDragPixel), Turned(drag.Facing), 0.01f);
        // On a panel (ImGui wanting the mouse), a still press is no click.
        var panel = OnThePad(out _);
        panel.Button(ChaseCamera.MouseTurn.Orbit, down: true);
        panel.Button(ChaseCamera.MouseTurn.Orbit, down: false);
        panel.JudgeMouse(true, false, wantCapture: true);
        panel.JudgeMouse(true, false, wantCapture: true);
        Assert.Null(panel.TakeRightClick());
    }

    [Fact]
    public void Free_look_reaches_straight_down_too()
    {
        var camera = OnThePad(out var focus);
        camera.DragLook(new Vector2(0f, 10000f));
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        Assert.True(DegreesBetween(camera.Forward, -Planet.Up(camera.Position)) < 0.5f);
    }

    [Fact]
    public void The_chase_camera_reaches_straight_up_and_down_without_going_over()
    {
        var limit = 90f - 0.25f;
        foreach (var sign in (ReadOnlySpan<float>)[1f, -1f])
        {
            var camera = new ChaseCamera(null);
            var focus = Scene.RocketStart + new Vector3(0, 3000f, 0);
            camera.Update(Dt, focus, null, GroundBody.Planet, 0);
            camera.DragOrbit(new Vector2(0f, sign * 1e6f));
            camera.Update(Dt, focus, null, GroundBody.Planet, 0);
            var pitch = float.RadiansToDegrees(camera.View.Pitch);
            Assert.Equal(sign * limit, pitch, 0.01f);
            // Dragging on past the end changes nothing.
            camera.DragOrbit(new Vector2(0f, sign * 1e6f));
            camera.Update(Dt, focus, null, GroundBody.Planet, 0);
            Assert.Equal(pitch, float.RadiansToDegrees(camera.View.Pitch), 0.001f);
        }
    }

    [Fact]
    public void Looking_up_from_below_a_landed_vessel_the_camera_slides_along_the_ground_under_it()
    {
        var camera = new ChaseCamera(null);
        var focus = Scene.RocketStart + new Vector3(0f, 10f, 0f); // a booster's middle, its bell on the pad
        camera.Place(0.6f, float.DegreesToRadians(-89f), Scene.CameraDistance);
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        var up = Planet.Up(focus);
        Assert.True(Planet.SurfaceAltitude(camera.Position) >= Scene.CameraGroundClearance - 0.01f,
            $"the camera is {Planet.SurfaceAltitude(camera.Position):0.00} m above the ground");
        Assert.True(NearestGround(camera.Position, 3f) >= Scene.CameraGroundClearance * 0.8f);
        var across = camera.Position - focus;
        across -= up * Vector3.Dot(across, up);
        Assert.True(across.Length() <= Scene.RocketRadius * 2f, $"the camera is {across.Length():0.0} m to the side of what it looks at");
        Assert.True(Vector3.Dot(camera.Position - focus, up) < 0f, "it was lifted over the vessel instead of slid under it");
        Assert.True(DegreesBetween(camera.Forward, up) < 10f);
    }

    [Fact]
    public void At_the_extremes_the_view_does_not_spin()
    {
        foreach (var sign in (ReadOnlySpan<float>)[1f, -1f])
        {
            var camera = new ChaseCamera(null);
            var focus = Scene.RocketStart + new Vector3(0, 3000f, 0);
            camera.Update(Dt, focus, null, GroundBody.Planet, 0);
            camera.DragOrbit(new Vector2(0f, sign * 1e6f));
            camera.Update(Dt, focus, null, GroundBody.Planet, 0);
            var before = camera.Up;
            for (var i = 0; i < 120; i++)
            {
                focus += new Vector3(0.7f, 0f, 0.4f); // flying about under the camera, yaw held
                camera.DragOrbit(new Vector2(0f, sign * (i % 2 == 0 ? -0.5f : 0.5f)));
                camera.Update(Dt, focus, null, GroundBody.Planet, 0);
                Assert.True(DegreesBetween(before, camera.Up) < 1f, $"the up turned {DegreesBetween(before, camera.Up):0.0}° in a frame");
                before = camera.Up;
            }
        }
    }

    [Fact]
    public void Steering_away_from_the_camera_keeps_its_last_usable_facing_at_the_extremes()
    {
        foreach (var sign in (ReadOnlySpan<float>)[1f, -1f])
        {
            var camera = OnThePad(out var focus);
            camera.DragOrbit(new Vector2(200f, 0f));
            camera.Update(Dt, focus, null, GroundBody.Planet, 0);
            var usable = camera.GroundForward;
            camera.DragOrbit(new Vector2(0f, sign * 1e6f));
            camera.Update(Dt, focus, null, GroundBody.Planet, 0);
            Assert.True(DegreesBetween(usable, camera.GroundForward) < 0.01f, "the facing was lost at the extreme");
            camera.DragOrbit(new Vector2(150f, 0f)); // turned about while straight up or down
            camera.Update(Dt, focus, null, GroundBody.Planet, 0);
            Assert.True(DegreesBetween(usable, camera.GroundForward) < 0.01f, "turned about at the extreme, the facing changed");
            Assert.True(MathF.Abs(Vector3.Dot(camera.GroundForward, Planet.Up(focus))) < 0.01f);
            Assert.True(MathF.Abs(Vector3.Dot(camera.GroundRight, camera.GroundForward)) < 0.01f);
        }
    }

    [Fact]
    public void A_low_vessel_seen_from_below_never_has_the_camera_in_it()
    {
        foreach (var height in (ReadOnlySpan<float>)[1.5f, 3f])
        foreach (var pitch in (ReadOnlySpan<float>)[-89f, -40f, -8f])
        {
            var camera = new ChaseCamera(null);
            var focus = Scene.RocketStart - Planet.Up(Scene.RocketStart) * Scene.RocketBellHeight + new Vector3(0f, Scene.PadTop + height, 0f);
            camera.Place(0.6f, float.DegreesToRadians(pitch), Scene.CameraDistance);
            camera.Update(Dt, focus, null, GroundBody.Planet, 0);
            camera.Update(Dt, focus, null, GroundBody.Planet, 0);
            var where = $"(focus {height} m up, pitch {pitch}°)";
            Assert.True(float.IsFinite(camera.Forward.X + camera.Forward.Y + camera.Forward.Z), "Forward " + where);
            Assert.True(float.IsFinite(camera.Up.X + camera.Up.Y + camera.Up.Z), "Up " + where);
            Assert.True(Vector3.Distance(camera.Position, focus) >= Scene.CameraSlideMinDistance - 0.01f, $"the camera is {Vector3.Distance(camera.Position, focus):0.00} m from the vessel " + where);
            Assert.True(Planet.SurfaceAltitude(camera.Position) >= Scene.CameraGroundClearance - 0.01f, "clear of the ground " + where);
        }
    }

    [Fact]
    public void Turning_down_under_a_low_vessel_the_camera_moves_without_a_jump()
    {
        // Under 3 m up, the slide's least distance comes into play as the camera turns down: it keeps
        // on that sphere round the vessel, so a small turn moves it a little, never a pop across.
        foreach (var height in (ReadOnlySpan<float>)[1.5f, 2.5f])
        {
            var focus = Scene.RocketStart - Planet.Up(Scene.RocketStart) * Scene.RocketBellHeight + new Vector3(0f, Scene.PadTop + height, 0f);
            Vector3? last = null;
            var worst = 0f;
            for (var pitch = -5f; pitch >= -89f; pitch -= 0.5f)
            {
                var camera = new ChaseCamera(null);
                camera.Place(0.6f, float.DegreesToRadians(pitch), Scene.CameraDistance);
                camera.Update(Dt, focus, null, GroundBody.Planet, 0);
                camera.Update(Dt, focus, null, GroundBody.Planet, 0);
                if (last is { } before && Vector3.Distance(before, focus) < 1.1f * Scene.CameraSlideMinDistance)
                    worst = MathF.Max(worst, Vector3.Distance(before, camera.Position));
                last = camera.Position;
            }
            // Near it (the slide in along the ground before it is quick, by its own rule), half a degree on
            // the 3 m sphere is some 3 cm, coming onto it no more than the slide's own step.
            Assert.True(worst < 0.25f, $"{height} m up: the camera jumped {worst:0.00} m for half a degree");
        }
    }

    [Fact]
    public void Looking_up_from_below_a_vessel_on_a_steep_slope_the_camera_keeps_clear_of_the_ground()
    {
        var site = LaunchSites.All.First(s => s.Name == "Polar");
        var (spot, _) = Steepest(site);
        var focus = spot + Planet.Up(spot) * 10f;
        var camera = new ChaseCamera(null);
        camera.Place(0f, float.DegreesToRadians(-80f), Scene.CameraDistance);
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        var clear = NearestGround(camera.Position, 3f);
        Assert.True(clear >= 1.2f, $"the camera is only {clear:0.00} m from the slope");
        Assert.True(Vector3.Distance(camera.Position, focus) >= Scene.CameraSlideMinDistance - 0.01f);
    }

    [Fact]
    public void Free_look_after_an_extreme_chase_pitch_keeps_a_level_horizon()
    {
        foreach (var pitch in (ReadOnlySpan<float>)[-89f, 89f])
        {
            var camera = new ChaseCamera(null);
            var focus = Scene.RocketStart + new Vector3(0, 3000f, 0);
            camera.Place(0.6f, float.DegreesToRadians(pitch), Scene.CameraDistance);
            camera.Update(Dt, focus, null, GroundBody.Planet, 0);
            // Free look, aimed up or down to the horizon from where the chase pitch had it.
            camera.DragLook(new Vector2(float.DegreesToRadians(90f) / Scene.CameraRadiansPerDragPixel, (pitch > 0 ? -1f : 1f) * float.DegreesToRadians(89f) / Scene.CameraRadiansPerDragPixel)); // and across the way the chase view faces
            camera.Update(Dt, focus, null, GroundBody.Planet, 0);
            Assert.True(camera.LookingFree);
            var up = Planet.Up(camera.Position);
            Assert.True(MathF.Abs(Vector3.Dot(camera.Forward, up)) < 0.05f, $"not aimed at the horizon: {Vector3.Dot(camera.Forward, up):0.000}");
            Assert.True(float.IsFinite(camera.Up.X + camera.Up.Y + camera.Up.Z) && float.IsFinite(camera.Forward.X));
            Assert.True(DegreesBetween(camera.Up, up) < 2f, $"the horizon is tilted {DegreesBetween(camera.Up, up):0.0}°");
        }
    }

    [Fact]
    public void A_view_placed_at_an_extreme_pitch_faces_by_its_own_yaw()
    {
        var camera = new ChaseCamera(null);
        var focus = Scene.RocketStart + new Vector3(0, 3000f, 0);
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        camera.Place(1.2f, float.DegreesToRadians(88f), Scene.CameraDistance);
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        var expected = new ChaseCamera(null);
        expected.Place(1.2f, 0.1f, Scene.CameraDistance);
        expected.Update(Dt, focus, null, GroundBody.Planet, 0);
        Assert.True(DegreesBetween(camera.GroundForward, expected.GroundForward) < 0.1f);
    }

    [Fact]
    public void A_right_drag_or_the_wheel_looks_at_the_vessel_again()
    {
        foreach (var undo in new Action<ChaseCamera>[] { c => c.DragOrbit(new Vector2(10f, 0f)), c => c.Zoom(1f) })
        {
            var camera = OnThePad(out var focus);
            camera.DragLook(new Vector2(300f, -200f));
            camera.Update(Dt, focus, null, GroundBody.Planet, 0);
            Assert.True(camera.LookingFree);
            undo(camera);
            camera.Update(Dt, focus, null, GroundBody.Planet, 0);
            Assert.False(camera.LookingFree);
            Assert.True(Vector3.Distance(camera.Target, camera.Focus) < 0.001f);
        }
    }

    [Fact]
    public void On_the_pad_free_look_frames_the_high_sky()
    {
        var display = new Vector2(1280f, 720f);
        var camera = OnThePad(out var focus);
        var up = Planet.Up(camera.Position);
        var ahead = Vector3.Normalize(camera.Forward - up * Vector3.Dot(camera.Forward, up));
        var sky = camera.Position + (ahead * MathF.Cos(float.DegreesToRadians(80f)) + up * MathF.Sin(float.DegreesToRadians(80f))) * 5000f;
        bool OnScreen() => camera.ToScreen(sky, display, out var s, out _) && s.X >= 0 && s.X <= display.X && s.Y >= 0 && s.Y <= display.Y;
        Assert.False(OnScreen(), "already framed before free look");
        camera.DragLook(new Vector2(0f, -10000f));
        camera.Update(Dt, focus, null, GroundBody.Planet, 0);
        Assert.True(OnScreen());
    }

    // The steepest ground within 150 m of the site's pad, off the pad itself, and how steep (degrees).
    private static (Vector3 Spot, float Slope) Steepest(LaunchSite site)
    {
        var (best, steepest) = (site.Pad, 0f);
        for (var x = -150f; x <= 150f; x += 5f)
        for (var z = -150f; z <= 150f; z += 5f)
        {
            if (MathF.Max(MathF.Abs(x), MathF.Abs(z)) < Scene.PadHalfSize + 10f)
                continue;
            var up = Planet.Up(site.ToWorld(new Vector3(x, 0f, z)));
            var slope = float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(Terrain.Normal(up), up), -1f, 1f)));
            if (Terrain.IsSea(up) || slope <= steepest)
                continue;
            (best, steepest) = (Planet.Centre + up * (Planet.SeaRadius + Terrain.Height(up)), slope);
        }
        return (best, steepest);
    }

    // How near the surface comes to p, sampled every 10 cm across the ground within reach of it.
    private static float NearestGround(Vector3 p, float reach)
    {
        var (east, north) = (Planet.East(p), Planet.North(p));
        var nearest = float.MaxValue;
        for (var x = -reach; x <= reach; x += 0.1f)
        for (var z = -reach; z <= reach; z += 0.1f)
        {
            var q = p + east * x + north * z;
            nearest = MathF.Min(nearest, Vector3.Distance(p, q - Planet.Up(q) * Planet.SurfaceAltitude(q)));
        }
        return nearest;
    }

    /// <summary>The angle between two directions, in degrees (by atan, which keeps small angles).</summary>
    private static float Degrees(Vector3 a, Vector3 b) =>
        float.RadiansToDegrees(MathF.Atan2(Vector3.Cross(a, b).Length(), Vector3.Dot(a, b)));

    [Fact]
    public void Across_the_edge_the_camera_keeps_its_place_and_view()
    {
        // A vessel coming in to the moon at 100 m/s, crossing into its sphere of influence from the
        // side (where the planet's up and the moon's are some 80 degrees apart). Two cameras follow
        // it in the planet's frame; at the crossing one is changed across into the moon's frame
        // (Rebase, as GameView does at the moment it is next drawn) and follows it there, the other
        // stays in the planet's to show where the view would have been.
        var (planet, moon) = (GroundBody.Planet, GroundBody.Moon);
        const double T0 = 1000.0;
        var toMoon = Bodies.Change(planet, moon, T0);
        var moonCentre = Bodies.CentreIn(moon, planet, T0).ToSingle();
        var across = Vector3.Normalize(Vector3.Cross(moonCentre - planet.Centre, planet.NorthPole));
        var inward = -Vector3.Normalize(toMoon.Direction(across).ToSingle()); // in the moon's frame
        Vector3 InMoon(double t) => moon.Centre - inward * (moon.SphereRadius + 50f) + inward * (100f * (float)(t - T0));
        Vector3 InPlanet(double t) => Bodies.Change(moon, planet, t).Point(InMoon(t)).ToSingle();

        var (stays, crosses) = (new ChaseCamera(null), new ChaseCamera(null));
        var t = T0;
        for (var i = 0; i < 60; i++, t += Dt)
            foreach (var camera in (ReadOnlySpan<ChaseCamera>)[stays, crosses])
                camera.Update(Dt, InPlanet(t), Dt, planet, 0);

        // Across: at once, and after the next frame, where the camera left in the planet's frame is.
        var change = Bodies.Change(planet, moon, t);
        Assert.True(Degrees(change.Direction(planet.Up(stays.Focus)).ToSingle(), moon.Up(change.Point(stays.Focus).ToSingle())) > 60f, "the two ups are far apart");
        var (place, forward) = (change.Point(stays.Position).ToSingle(), Vector3.Normalize(change.Direction(stays.Forward).ToSingle()));
        crosses.Rebase(change);
        Assert.True(Vector3.Distance(place, crosses.Position) < 0.01f, $"moved {Vector3.Distance(place, crosses.Position):0.000} m as it was changed across");
        Assert.True(Degrees(forward, crosses.Forward) < 0.1f, $"turned {Degrees(forward, crosses.Forward):0.000} degrees as it was changed across");
        stays.Update(Dt, InPlanet(t), Dt, planet, 0);
        crosses.Update(Dt, InMoon(t), Dt, moon, 0);
        var off = Vector3.Distance(change.Point(stays.Position).ToSingle(), crosses.Position);
        var turned = Degrees(Vector3.Normalize(change.Direction(stays.Forward).ToSingle()), crosses.Forward);
        Assert.True(off < 0.01f, $"{off * 1000:0.0} mm from where it would have been");
        Assert.True(turned < 0.1f, $"looking {turned:0.000} degrees away from where it would have");

        // Then its up eases round to the moon's, in about CameraBodyEaseSeconds of real time, never
        // more than 5 degrees in a frame; half way it is still well short of it.
        var (worst, last, halfWay) = (0f, crosses.WorldUp, 0f);
        var frames = (int)MathF.Ceiling(Scene.CameraBodyEaseSeconds / Dt) + 1;
        for (var i = 0; i < frames; i++)
        {
            t += Dt;
            crosses.Update(Dt, InMoon(t), Dt, moon, 0);
            worst = MathF.Max(worst, Degrees(last, crosses.WorldUp));
            last = crosses.WorldUp;
            if (i == frames / 2)
                halfWay = Degrees(crosses.WorldUp, moon.Up(crosses.Focus));
        }
        Assert.True(worst < 5f, $"its up turned {worst:0.00} degrees in a frame");
        Assert.True(halfWay > 10f, $"half way through it was already within {halfWay:0.0} degrees of the moon's up");
        var still = Degrees(crosses.WorldUp, moon.Up(crosses.Focus));
        Assert.True(still < 0.01f, $"its up is still {still:0.000} degrees off the moon's");
    }
}
