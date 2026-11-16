using System.Diagnostics;
using System.Numerics;
using GameOne;
using TerrainGen;

// TerrainGen: makes the planet's terrain map from the authored layout, or with --moon the moon's
// from its own. See README.md.
//   dotnet run --project tools/TerrainGen -c Release -- tools/TerrainGen/layout.json src/GameOne/Assets/terrain.bin.gz [--preview <folder>]
//   dotnet run --project tools/TerrainGen -c Release -- --moon tools/TerrainGen/moon.json src/GameOne/Assets/moon.bin.gz [--preview <folder>]

var moon = args.Length > 0 && args[0] == "--moon";
if (moon) args = args[1..];
if (args.Length is not (2 or 4) || (args.Length == 4 && args[2] != "--preview"))
{
    Console.Error.WriteLine("usage: TerrainGen [--moon] <layout.json> <map.bin.gz> [--preview <folder>]");
    return 2;
}

var clock = Stopwatch.StartNew();
if (moon)
{
    var moonMap = new MoonGenerator(MoonLayout.Load(args[0]), Console.WriteLine).Run();
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
    moonMap.Save(args[1]);
    Console.WriteLine($"{clock.Elapsed.TotalSeconds,6:F1}s  wrote {args[1]} ({new FileInfo(args[1]).Length / 1e6:F1} MB)");
    if (args.Length == 4)
    {
        Preview.WriteMoon(moonMap, args[3]);
        Console.WriteLine($"{clock.Elapsed.TotalSeconds,6:F1}s  previews in {args[3]}");
    }

    return 0;
}

var layout = Layout.Load(args[0]);

// The game's own sites take their places from Scene, so the map always matches the game; the
// layout gives only their sizes. Any other site in the layout says where it is. One entry can be
// several places of the same size: the test pads, each levelled under its plinth.
var sceneSites = new Dictionary<string, Vector3[]>
{
    ["Launch pad"] = [Planet.Up(Vector3.Zero)],
    ["Landing zone"] = [Planet.Up(Scene.LandingZoneCentre)],
    ["Test pads"] = [.. Scene.TestPads.Select(pad => Planet.Up(pad.Centre))],
};
// (The "Far pad" and "Far zone" entries are sizes only, for the other launch sites, below.)
string[] sizesOnly = ["Far pad", "Far zone"];
var sites = layout.Sites.Where(s => !sizesOnly.Contains(s.Name)).SelectMany(s =>
{
    if (s.At != null) return new[] { new Site(s.Name, Degrees.Direction(s.At), s.Radius, s.Blend) };
    if (!sceneSites.TryGetValue(s.Name, out var at))
        throw new InvalidDataException($"site {s.Name} needs \"at\" (only {string.Join(", ", sceneSites.Keys)} come from Scene)");
    return at.Select(direction => new Site(s.Name, direction, s.Radius, s.Blend));
}).ToList();
foreach (var name in sceneSites.Keys)
    if (sites.All(s => s.Name != name)) throw new InvalidDataException($"the layout has no site \"{name}\"");

// The other launch sites (LaunchSites.All, after Home): each pad, and its landing zone if it has
// one, levelled down to the highest ground under it (its footprint: the pad's square to its
// corners, the zone's disc), each at its own height, and without Home's wider calm: the country
// round them, its drops included, is what they are there for.
var (padSize, zoneSize) = (layout.Sites.Single(s => s.Name == "Far pad"), layout.Sites.Single(s => s.Name == "Far zone"));
foreach (var launchSite in LaunchSites.All.Skip(1))
{
    sites.Add(new Site($"{launchSite.Name} pad", launchSite.Up, padSize.Radius, padSize.Blend, Calm: false, Footprint: Scene.PadHalfSize * MathF.Sqrt(2f)));
    if (launchSite.LandingZoneDirection is { } zone)
        sites.Add(new Site($"{launchSite.Name} landing zone", zone, zoneSize.Radius, zoneSize.Blend, Calm: false, Footprint: Scene.LandingZoneRadius));
}

var ships = LaunchSites.All.SelectMany(s => s.DroneShips).Select(Planet.Up).ToArray();
var map = new Generator(layout, sites, ships, Scene.PlanetRadius, Console.WriteLine).Run();
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
map.Save(args[1]);
Console.WriteLine($"{clock.Elapsed.TotalSeconds,6:F1}s  wrote {args[1]} ({new FileInfo(args[1]).Length / 1e6:F1} MB)");

if (args.Length == 4)
{
    // And each icy sea's ice from above, its floes and some open water round it.
    var ice = layout.Seas.Where(s => s.Ice != null).Select(s =>
        ($"ice-{s.Name.ToLowerInvariant().Replace(' ', '-')}.png", Degrees.Direction(s.At), 2.4f * (s.Ice!.Pack * 1.25f + s.Ice.Floes)));
    Preview.Write(map, args[3], ice);
    Console.WriteLine($"{clock.Elapsed.TotalSeconds,6:F1}s  previews in {args[3]}");
}

return 0;
