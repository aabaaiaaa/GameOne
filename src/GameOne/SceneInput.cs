using System.Numerics;
using GameOne.Physics;
using GameOne.Rendering;

namespace GameOne;

/// <summary>An engine as it is this frame, in the scene's frame: where its bell's bottom is, which way its exhaust goes, how hard it fires, how wide its bell is, and how thick the air is there.</summary>
internal readonly record struct EngineAt(Vessel Vessel, Vector3 Nozzle, Vector3 Exhaust, float Throttle, float BellRadius, float Air);

/// <summary>
/// What is drawn of every vessel in the world each frame, gathered for the renderer in the scene's
/// frame (<see cref="BodyFrames.Scene"/>, the planet's ground frame, which the renderer draws in),
/// each vessel changed there from its own body's frame by that body's change this frame
/// (<see cref="BodyFrames.ToScene"/>, worked out once a frame for each body in use): every stage's
/// model, every engine and plume, every lit landing light, and the moon and the sun's disc, for the main view's camera.
/// </summary>
/// <remarks>
/// Precision: the moon is some 40 km from the planet's origin, where a float's step is 4 mm, so a
/// place there rounded on its own, frame by frame, would jitter against another rounded apart.
/// What is hard-edged and seen close (the stages, the moon's ground) is given from the camera
/// instead (<see cref="Origin"/>), worked out in double first and made a float only as a small
/// offset; the rest (plumes, lights, glows, smoke) are soft or lit by, and placed in the scene's
/// frame as they are. Kept from frame to frame, so nothing is allocated a frame.
/// </remarks>
internal sealed class SceneInput
{
    private readonly List<StagePart> _parts = [];
    private readonly List<Plume> _plumes = [];
    private readonly List<EngineAt> _engines = [];
    private readonly VesselLight[] _lights = new VesselLight[Flight.Capacity];
    private int _lightCount;

    /// <summary>The main view's camera in the scene's frame, as handed to the renderer (a float: <see cref="FrameInput.CameraPosition"/>).</summary>
    public Vector3 CameraPosition { get; private set; }

    /// <summary><see cref="CameraPosition"/> in double: what the stages and the moon are given from.</summary>
    public Vector3Double Origin { get; private set; }

    /// <summary>What the main view's camera looks at, and its up, in the scene's frame.</summary>
    public Vector3 CameraTarget { get; private set; }
    public Vector3 CameraUp { get; private set; }

    /// <summary>Every stage to draw, of every vessel that hasn't blown up: each one's model turned into the scene's frame, its place from <see cref="CameraPosition"/>.</summary>
    public List<StagePart> Parts => _parts;

    /// <summary>Every engine in the world as it is this frame (firing or not), in the scene's frame.</summary>
    public List<EngineAt> Engines => _engines;

    /// <summary>The flown vessel's engine, of <see cref="Engines"/>.</summary>
    public EngineAt Flown { get; private set; }

    /// <summary>Every engine firing, as a plume.</summary>
    public List<Plume> Plumes => _plumes;

    /// <summary>Every vessel whose landing lights are on (see <see cref="Vessel.LandingLights"/>), each at its bell's bottom shining down its axis, in the scene's frame.</summary>
    public ReadOnlySpan<VesselLight> Lights => _lights.AsSpan(0, _lightCount);

    /// <summary>The moon's centre from <see cref="CameraPosition"/>.</summary>
    public Vector3 MoonFromCamera { get; private set; }

    /// <summary>The sun's disc as seen from <see cref="CameraPosition"/> (<see cref="FrameInput.SunDisc"/>).</summary>
    public Vector4 SunDisc { get; private set; }

    /// <summary>
    /// Gathers this frame's: the main view's camera (<paramref name="camera"/>, in the frame of
    /// <paramref name="frames"/>' view's body), every stage (in each body's physics, and of each
    /// vessel under formula), every engine (none firing while <paramref name="paused"/>) and every lit
    /// landing light, all in the scene's frame as <paramref name="frames"/> has each body this frame.
    /// </summary>
    public void Gather(Flight flight, BodyFrames frames, ChaseCamera camera, bool paused)
    {
        var (position, target, up) = InScene(frames, frames.View, camera);
        (CameraPosition, CameraTarget, CameraUp) = (position, target, up);
        Origin = position;

        _parts.Clear();
        var bodies = Bodies.All;
        for (var b = 0; b < bodies.Count; b++)
            if (flight.PhysicsAt(bodies[b]) is { } physics)
            {
                var first = _parts.Count;
                physics.AddParts(_parts);
                ToScene(first, frames.ToScene(bodies[b]));
            }
        var world = flight.WorldVessels;
        for (var i = 0; i < world.Count; i++)
            if (world[i].OnFormula)
            {
                var first = _parts.Count;
                world[i].FormulaParts(_parts); // out of the physics, with no body there to draw: from its own state
                ToScene(first, frames.ToScene(world[i].Body));
            }

        _engines.Clear();
        Flown = default;
        _plumes.Clear();
        _lightCount = 0;
        for (var i = 0; i < world.Count; i++)
        {
            var v = world[i];
            var toScene = frames.ToScene(v.Body);
            var (nozzle, exhaust, throttle, air) = Engine(v, paused);
            var engine = new EngineAt(v, toScene.Point(nozzle).ToSingle(), toScene.Direction(exhaust).ToSingle(), throttle, v.CurrentStage.BellRadius, air);
            _engines.Add(engine);
            if (v == flight.Controlled)
                Flown = engine;
            if (throttle > 0f)
                _plumes.Add(new Plume(engine.Nozzle, engine.Exhaust, throttle, engine.BellRadius, 1f - air));
            if (v.LandingLights > 0f && _lightCount < _lights.Length)
            {
                var foot = Vector3.Transform(new Vector3(0f, -v.CurrentStage.BellHeight, 0f), v.Model);
                _lights[_lightCount++] = new VesselLight(toScene.Point(foot).ToSingle(), toScene.Direction(-v.Up).ToSingle(), v.LandingLights, v.Id);
            }
        }

        if (Flown.Vessel != flight.Controlled) // (not in the world's list: still drawn from)
        {
            var v = flight.Controlled;
            var toScene = frames.ToScene(v.Body);
            var (nozzle, exhaust, throttle, air) = Engine(v, paused);
            Flown = new EngineAt(v, toScene.Point(nozzle).ToSingle(), toScene.Direction(exhaust).ToSingle(), throttle, v.CurrentStage.BellRadius, air);
        }

        var moon = GroundBody.Moon;
        MoonFromCamera = (frames.ToScene(moon).Point(moon.Centre) - Origin).ToSingle();
        SunDisc = SunFrom(frames, Origin);
    }

    /// <summary>The parts from <paramref name="first"/> on, turned into the scene's frame by <paramref name="toScene"/>, each placed from <see cref="Origin"/>.</summary>
    private void ToScene(int first, in FrameChange toScene)
    {
        for (var k = first; k < _parts.Count; k++)
            _parts[k] = _parts[k] with { Model = toScene.Model(_parts[k].Model, Origin) };
    }

    /// <summary>
    /// A camera in <paramref name="body"/>'s frame, in the scene's: where it is, what it looks at
    /// and its up. What it looks at is placed from where it is, by the way it looks (small numbers,
    /// so the view's direction keeps its precision however far out it is).
    /// </summary>
    public static (Vector3 Position, Vector3 Target, Vector3 Up) InScene(BodyFrames frames, GroundBody body, ChaseCamera camera)
    {
        if (body == BodyFrames.Scene)
            return (camera.Position, camera.Target, camera.WorldUp);
        var toScene = frames.ToScene(body);
        var position = toScene.Point(camera.Position).ToSingle();
        return (position, position + toScene.Direction(camera.Target - camera.Position).ToSingle(), toScene.Direction(camera.WorldUp).ToSingle());
    }

    /// <summary>The moon's centre from a camera at <paramref name="camera"/> in the scene's frame, worked out in double.</summary>
    public static Vector3 MoonFrom(BodyFrames frames, Vector3 camera)
    {
        var moon = GroundBody.Moon;
        return (frames.ToScene(moon).Point(moon.Centre) - camera).ToSingle();
    }

    /// <summary>
    /// The sun's disc as seen from a camera at <paramref name="camera"/> in the scene's frame (the
    /// planet's ground frame, which the sun's place is given in), worked out in double.
    /// </summary>
    public static Vector4 SunFrom(BodyFrames frames, Vector3Double camera) =>
        SolarSystem.DiscSeenFrom(SolarSystem.SunCentreAt(frames.Time) - camera);

    /// <summary>Where <paramref name="vessel"/>'s engine is (in its body's frame), which way its exhaust goes, how hard it is firing, and how thick its body's air is there.</summary>
    internal static (Vector3 Nozzle, Vector3 Exhaust, float Throttle, float Air) Engine(Vessel vessel, bool paused)
    {
        var nozzle = Vector3.Transform(new Vector3(0, -vessel.CurrentStage.BellHeight, 0), vessel.Model);
        var throttle = vessel.EngineLit && !paused ? vessel.Throttle : 0f;
        var body = vessel.Body;
        return (nozzle, -vessel.Up, throttle, body.AirDensity(body.Altitude(nozzle)));
    }
}
