using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameOne;

/// <summary>
/// A saved world, as the world list shows it: its name, what it was made from, when it was last
/// played and what is in it; or why it can't be read.
/// </summary>
internal sealed record WorldSummary(string Id, string Name, string Preset, DateTime LastPlayed, int Flights, int VesselCount, string Contents, string? Error = null);

/// <summary>What the player can ask of the world list.</summary>
internal enum WorldRequestKind
{
    /// <summary>Play a saved world (<see cref="WorldRequest.Id"/>).</summary>
    Play,
    /// <summary>Make a new world from a preset (<see cref="WorldRequest.Id"/> is the preset's) and play it.</summary>
    New,
    /// <summary>Rename a saved world to <see cref="WorldRequest.Text"/>.</summary>
    Rename,
    /// <summary>Put a saved world back to its preset.</summary>
    Reset,
    Delete,
}

/// <summary>One thing the player asked of the world list, for the game to do (the HUD only shows the list).</summary>
internal sealed record WorldRequest(WorldRequestKind Kind, string Id, string Text = "");

/// <summary>
/// Where worlds are saved: one JSON file per world in a folder (by default the user's local app
/// data, <c>GameOne/Worlds</c>; <c>GAMEONE_WORLDS</c> names another, e.g. for tests and test runs of
/// the game). Saving writes a new file and then swaps it in, so a crash mid-save never leaves half
/// a world.
/// </summary>
/// <remarks>
/// A file holds the world's state (see <see cref="WorldState"/>), with each vessel's mission and
/// vehicle by name (looked up in <see cref="Mission.All"/> and <see cref="Vehicle.All"/>), each
/// vessel's body by name (<see cref="VesselState.Body"/>, in <see cref="Bodies.All"/>), and a format
/// version, for files saved by another version of the game to be refused rather than misread.
/// </remarks>
internal sealed class WorldStore(string directory)
{
    /// <summary>
    /// The format files are written in. A file in any other can't be read: a newer one's may hold
    /// what this game doesn't know, and an older one's vessels were saved with no body (in the
    /// planet's frame, those on the moon held there by a place of their own), which isn't migrated.
    /// </summary>
    public const int FormatVersion = 2;

    private const string Extension = ".json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IncludeFields = true, // Vector3's and Quaternion's X, Y, Z (and W) are fields
        Converters = { new MissionByName(), new VehicleByName(), new JsonStringEnumConverter() },
    };

    /// <summary>The folder the worlds are saved in.</summary>
    public string Directory { get; } = directory;

    /// <summary>The game's own store: <c>GAMEONE_WORLDS</c> if that is set, else the user's local app data.</summary>
    public static WorldStore Default() => new(Environment.GetEnvironmentVariable("GAMEONE_WORLDS") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameOne", "Worlds"));

    private string PathOf(string id) => Path.Combine(Directory, id + Extension);

    /// <summary>Every saved world, the most recently played first.</summary>
    public IReadOnlyList<WorldSummary> List()
    {
        if (!System.IO.Directory.Exists(Directory))
            return [];
        var worlds = new List<WorldSummary>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*" + Extension))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            try
            {
                var world = Load(id);
                worlds.Add(new WorldSummary(id, world.Name, world.Preset, world.LastPlayed, world.Flights, world.Vessels.Count, Describe(world)));
            }
            catch (Exception e) when (e is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                worlds.Add(new WorldSummary(id, id, "", File.GetLastWriteTime(path), 0, 0, "", e.Message));
            }
        }
        return [.. worlds.OrderByDescending(w => w.LastPlayed)];
    }

    /// <summary>Reads a saved world. Throws if it is missing, damaged, or saved by a newer version of the game.</summary>
    public WorldState Load(string id)
    {
        var file = JsonSerializer.Deserialize<WorldFile>(File.ReadAllText(PathOf(id)), Options)
            ?? throw new InvalidDataException("The file is empty.");
        if (file.Version > FormatVersion)
            throw new InvalidDataException($"Saved by a newer version of the game (format {file.Version}).");
        if (file.Version < FormatVersion)
            throw new InvalidDataException($"Saved by an older version of the game (format {file.Version}).");
        foreach (var vessel in file.World.Vessels)
            if (!Bodies.Has(vessel.Body))
                throw new InvalidDataException($"A vessel is at an unknown body, \"{vessel.Body}\".");
        return file.World with { Id = id };
    }

    /// <summary>Saves <paramref name="world"/> (which must have an <see cref="WorldState.Id"/>), as last played now.</summary>
    public void Save(WorldState world)
    {
        if (world.Id.Length == 0)
            throw new ArgumentException("A world needs an id to be saved.", nameof(world));
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathOf(world.Id);
        var temporary = path + ".saving";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new WorldFile(FormatVersion, world with { LastPlayed = DateTime.Now }), Options));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Makes a new saved world from preset <paramref name="presetId"/>, named after it (with a number, if that name is taken), and returns it.</summary>
    public WorldState Create(string presetId)
    {
        var preset = WorldPresets.Make(presetId);
        var names = List().Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = preset.Name;
        for (var n = 2; names.Contains(name); n++)
            name = $"{preset.Name} {n}";
        var now = DateTime.Now;
        var world = preset with { Id = $"{now:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x10000):x4}", Name = name, Created = now };
        Save(world);
        return world with { LastPlayed = DateTime.Now };
    }

    public void Rename(string id, string name) => Save(Load(id) with { Name = name });

    /// <summary>Puts a saved world back to its preset: same name, a fresh start (its clock and flights too).</summary>
    public WorldState Reset(string id)
    {
        var world = Load(id);
        var fresh = WorldPresets.Make(world.Preset) with { Id = id, Name = world.Name, Created = world.Created };
        Save(fresh);
        return fresh;
    }

    public void Delete(string id) => File.Delete(PathOf(id));

    /// <summary>The player's settings, in the same folder (not a .json file, so never listed as a world).</summary>
    private string SettingsPath => Path.Combine(Directory, "player.settings");

    /// <summary>The player's settings, or the defaults if none have been saved or the file can't be read (an old one is simply replaced).</summary>
    public Settings LoadSettings()
    {
        try
        {
            return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath), Options) ?? new() : new();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void SaveSettings(Settings settings)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temporary = SettingsPath + ".saving";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, SettingsPath, overwrite: true);
    }

    /// <summary>What is in a world, in a few words: e.g. "4 satellites in orbit, 1 booster landed".</summary>
    public static string Describe(WorldState world)
    {
        if (world.Vessels.Count == 0)
            return "nothing yet";
        const string debris = "piece of debris";
        var parts = world.Vessels
            .Select(v =>
            {
                var guided = v.Vehicle.Stages[v.First..(v.Last + 1)].Any(s => s.Guided);
                var whole = v.First == 0 && v.Last == v.Vehicle.Stages.Length - 1 && v.Vehicle.Stages.Length > 1;
                // As the HUD names them: a whole rocket by its name, a part by its bottom stage's.
                var noun = !guided ? debris : (whole ? v.Vehicle.Name : v.Vehicle.Stages[v.First].Name).ToLowerInvariant();
                var where = !guided ? "" : v.State switch
                {
                    FlightState.Landed => " landed",
                    FlightState.Floating => " floating",
                    FlightState.Ready => " on the pad",
                    // From the centre of mass, where the velocity is, as the vessel's own orbit is read.
                    _ => Bodies.Named(v.Body) is var body && body.Orbit(v.CentreOfMass, body.AmongStars(v.CentreOfMass, v.Velocity)).Periapsis > body.OrbitFloor ? " in orbit" : " flying",
                };
                return (Noun: noun, Where: where);
            })
            .GroupBy(v => v)
            .OrderBy(g => g.Key.Noun == debris).ThenByDescending(g => g.Count())
            .Select(g =>
            {
                var count = g.Count();
                var plural = count == 1 ? g.Key.Noun : g.Key.Noun == debris ? "pieces of debris" : g.Key.Noun + "s";
                return $"{count} {plural}{g.Key.Where}";
            });
        return string.Join(", ", parts);
    }

    /// <summary>What a file holds: the format it was written in, and the world.</summary>
    private sealed record WorldFile(int Version, WorldState World);

    private sealed class MissionByName : JsonConverter<Mission>
    {
        public override Mission Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() is { } name && Mission.All.FirstOrDefault(m => m.Name == name) is { } mission
                ? mission
                : throw new JsonException($"Unknown mission \"{reader.GetString()}\".");

        public override void Write(Utf8JsonWriter writer, Mission value, JsonSerializerOptions options) => writer.WriteStringValue(value.Name);
    }

    private sealed class VehicleByName : JsonConverter<Vehicle>
    {
        public override Vehicle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() is { } name && Vehicle.All.FirstOrDefault(v => v.Name == name) is { } vehicle
                ? vehicle
                : throw new JsonException($"Unknown vehicle \"{reader.GetString()}\".");

        public override void Write(Utf8JsonWriter writer, Vehicle value, JsonSerializerOptions options) => writer.WriteStringValue(value.Name);
    }
}
