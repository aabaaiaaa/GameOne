using System.Numerics;
using GameOne.Physics;

namespace GameOne.Rendering;

/// <summary>
/// The engine's exhaust: from the bottom of the bell, the way it points, and how hard it is firing
/// (0 = off); how wide the bell is; and how thin the air is (0 at the surface, 1 in space), as
/// exhaust spreads out wider in thin air.
/// </summary>
internal readonly record struct Plume(Vector3 Nozzle, Vector3 Direction, float Throttle, float BellRadius = Scene.RocketBellRadius, float Vacuum = 0f);

/// <summary>
/// A small view drawn over the main one: a camera of its own, where on the screen it goes (its top
/// left corner and size, as fractions of the screen's width and height), the lights it is lit
/// by (its index in <see cref="FrameInput.LightSets"/>: those of the place nearest its vessel), and
/// its sun shadows' area (its index in <see cref="FrameInput.SunAreas"/>: its own, or one its
/// vessel is well inside), its near and far planes (its camera's, <see cref="ChaseCamera.Clip"/>), the moon's centre from its camera,
/// and the sun's disc as its camera sees it (<see cref="FrameInput.SunDisc"/>).
/// All in the scene's frame (the planet's ground frame).
/// </summary>
internal readonly record struct ViewInput(Vector3 CameraPosition, Vector3 CameraTarget, Vector3 CameraUp, Vector2 Min, Vector2 Size, int LightSet, int SunArea,
    (float Near, float Far) Clip, Vector3 MoonFromCamera, Vector4 SunDisc);

/// <summary>Everything the renderer needs to know to draw one frame.</summary>
internal ref struct FrameInput
{
    /// <summary>Seconds since start, for anything animated.</summary>
    public double Time;

    /// <summary>The world's own time in seconds (<see cref="Flight.WorldTime"/>), which the sea's waves run by.</summary>
    public double WorldTime;

    /// <summary>
    /// Towards the sun this frame (<see cref="Sun.Direction"/> at <see cref="WorldTime"/>): what the
    /// sunlight, the sun's shadows, the lens flare and the far glows go by.
    /// </summary>
    public Vector3 SunDirection;

    /// <summary>
    /// How far the planet has turned among the stars (<see cref="Planet.TurnAngle"/> at <see cref="WorldTime"/>,
    /// radians about the north pole, wrapped): the sky turns its stars by it, so they rise and set.
    /// </summary>
    public float StarTurn;

    /// <summary>
    /// The moon's centre this frame from <see cref="CameraPosition"/> (worked out in double, so it
    /// holds still to the millimetre seen from on it or near it) and its radius: the sky draws it
    /// where a view's ray meets it, lit by the sun, and up close its mesh is placed from it.
    /// </summary>
    public Vector3 MoonFromCamera;
    public float MoonRadius;

    /// <summary>
    /// The sun's disc as the main view's camera sees it (<see cref="SolarSystem.DiscSeenFrom"/>,
    /// worked out in double): the way to its centre, and in w its angular radius. On the planet it
    /// is <see cref="SunDirection"/> and <see cref="SolarSystem.SunAngularRadius"/> to within the
    /// planet's radius over the sun's distance (0.2°); from the map's furthest zoom, millions of
    /// metres out, it is where the sun is from there. The sky draws the disc by it (and writes its
    /// depth), the eclipse's dimming and the corona go by it, and the lens flare's probe is put by it.
    /// </summary>
    public Vector4 SunDisc;

    /// <summary>
    /// The moon's orientation this frame (<see cref="SolarSystem.MoonOrientation"/> at <see cref="WorldTime"/>:
    /// its own axes into the ground's frame): the sky reads its map through it.
    /// </summary>
    public Quaternion MoonOrientation;

    /// <summary>
    /// What the moon gives back of the sunlight this frame (<see cref="SolarSystem.MoonShine"/> at
    /// <see cref="WorldTime"/>, linear rgb, dimmed in a lunar eclipse): the planet's night is lit by it.
    /// </summary>
    public Vector3 MoonShine;

    /// <summary>
    /// The main view's camera and what it looks at. Everything here is in the scene's frame (the
    /// planet's ground frame: <see cref="BodyFrames.Scene"/>), each vessel changed into it from its
    /// own body's (<see cref="SceneInput"/>).
    /// </summary>
    public Vector3 CameraPosition, CameraTarget;

    /// <summary>Which way is up for the camera: away from the body below it, wherever it is.</summary>
    public Vector3 CameraUp;

    /// <summary>The main view's near and far planes: its camera's (<see cref="ChaseCamera.Clip"/>).</summary>
    public (float Near, float Far) CameraClip;

    /// <summary>
    /// How far into the map the main view is, 0 (by the vessel) to 1 (zoomed out, as the map's lines
    /// are drawn in full: <see cref="Paths.MapFade"/>): the lens flare is dimmed by it to
    /// <see cref="Scene.MapFlareShare"/>, so its ghosts don't cross the map.
    /// </summary>
    public float MapShown;

    /// <summary>
    /// The sun shadows' areas (<see cref="GameOne.SunAreas"/>): the main view's first, round the
    /// flown vessel, then one for each small view whose vessel is away from those before it (each
    /// small view's is its <see cref="ViewInput.SunArea"/>).
    /// </summary>
    public ReadOnlySpan<SunArea> SunAreas;

    /// <summary>Each drone ship's model this frame (<see cref="Rafts.Ships"/>', each its <see cref="Raft.Model"/>): they ride the waves, so they move.</summary>
    public ReadOnlySpan<Matrix4x4> DroneShips;

    /// <summary>
    /// The floes to draw this frame (those within <see cref="Scene.FloeDrawDistance"/> of a view's
    /// camera), each one's model (<see cref="Meshes.FloeModel"/>): they ride the waves too. Each
    /// carries its floe's id in M14 (otherwise 0), for its looks.
    /// </summary>
    public ReadOnlySpan<Matrix4x4> Floes;

    /// <summary>
    /// Every stage to draw, of every vessel that hasn't blown up, each model's place given from
    /// <see cref="CameraPosition"/> (worked out in double: a stage on the moon, tens of kilometres
    /// out, keeps its millimetres against the moon's ground).
    /// </summary>
    public ReadOnlySpan<StagePart> Parts;

    /// <summary>Every engine firing: each vessel's, so a part left burning still shows its flame.</summary>
    public ReadOnlySpan<Plume> Plumes;

    /// <summary>The one point light (the engine flame, or an explosion's flash), and its colour times brightness.</summary>
    public Vector3 PointLightPosition, PointLight;

    public ReadOnlySpan<Fireball> Fireballs;
    public ReadOnlySpan<SmokeCloud> Clouds;

    /// <summary>
    /// The lights of the pads, zones and drone ships the views are at (<see cref="GameOne.LightSets"/>):
    /// one set per distinct place, the main view's first (each small view's is its
    /// <see cref="ViewInput.LightSet"/>). Each set with floods casting shadows has a block of the
    /// shadow map's floodlight tiles of its own.
    /// </summary>
    public ReadOnlySpan<LightSet> LightSets;

    /// <summary>
    /// How far each lit place's lights are on this frame (<see cref="PlaceLights.Lit"/>: 0, off, to 1,
    /// by <see cref="PadLights.All"/>'s index): its lamps' faces and far glow go by it, as its lights
    /// in <see cref="LightSets"/> already do.
    /// </summary>
    public ReadOnlySpan<float> PlaceLit;

    /// <summary>
    /// Every vessel whose landing lights are on this frame (<see cref="Vessel.LandingLights"/>), at
    /// most <see cref="Flight.Capacity"/>: each a far glow at its bell's bottom, as far on as its
    /// fraction. The nearest few also light the ground, among the sets' lights in <see cref="LightSets"/>.
    /// </summary>
    public ReadOnlySpan<VesselLight> VesselGlows;

    /// <summary>Where the booster is stirring the ground smoke, and how thick that smoke is (0..1) at each of <see cref="LaunchSites.SmokeSites"/>.</summary>
    public ReadOnlySpan<SmokeStir> Stirs;
    public ReadOnlySpan<float> SmokeThickness;

    /// <summary>The small views, drawn over the main one (at most <see cref="Scene.MaxSmallViews"/>; any more are left out).</summary>
    public ReadOnlySpan<ViewInput> Views;
}
