using System.Text.Json;
using System.Text.Json.Serialization;
using GameOne;

namespace TerrainGen;

// The authored layout (layout.json): the world's large shapes, placed by hand, which the generator
// turns into terrain. Positions are [latitude, longitude] in degrees (the pad at [0, 0], east is
// +longitude), so they follow the planet's radius; sizes and heights are in metres. README.md
// describes each field.

internal sealed class Layout
{
    public required int Seed { get; init; }
    public required int Resolution { get; init; }
    public required Coast Coast { get; init; }
    public required Relief Relief { get; init; }
    public required List<LandMass> Land { get; init; }
    public required List<SeaLayout> Seas { get; init; }
    public required List<RangeLayout> Ranges { get; init; }
    public required DesertRule Deserts { get; init; }
    public required SnowRule Snow { get; init; }
    public required List<SiteLayout> Sites { get; init; }
    public required ErosionLayout Erosion { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, // a misspelt field is an error, not ignored
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static Layout Load(string path)
    {
        var layout = JsonSerializer.Deserialize<Layout>(File.ReadAllText(path), Options)
                     ?? throw new InvalidDataException($"{path} is empty");
        layout.Check();
        return layout;
    }

    private void Check()
    {
        if (Resolution < 16) throw new InvalidDataException("resolution must be at least 16");
        foreach (var land in Land)
            if (land.Outline.Count < 3) throw new InvalidDataException($"{land.Name}'s outline needs three points or more");
        foreach (var range in Ranges)
            if (range.Path.Count < 2) throw new InvalidDataException($"{range.Name}'s path needs two points or more");
        if (Seas.Count is 0 or > 254) throw new InvalidDataException("there must be 1 to 254 seas");
        foreach (var at in Land.SelectMany(l => l.Outline).Concat(Ranges.SelectMany(r => r.Path)).Concat(Seas.Select(s => s.At)))
            if (at.Length != 2) throw new InvalidDataException("a position is [latitude, longitude]");
        foreach (var sea in Seas)
        {
            if (sea.Waves <= 0f) throw new InvalidDataException($"{sea.Name}'s waves must be above 0");
            if (sea.Shallow.Length != 3 || sea.Deep.Length != 3 || sea.Shallow.Concat(sea.Deep).Any(c => c is < 0f or > 1f))
                throw new InvalidDataException($"{sea.Name}'s colours are [r, g, b], each 0 to 1");
            if (sea.Ice is { } ice && (sea.Kind != SeaKind.Icy || ice.Pack <= 0f || ice.Floes <= 0f))
                throw new InvalidDataException($"{sea.Name}'s ice needs an icy sea, and its pack and floes above 0");
        }
    }
}

/// <summary>A [latitude, longitude] in degrees as a unit direction.</summary>
internal static class Degrees
{
    public static System.Numerics.Vector3 Direction(float[] at) =>
        CubeSphere.FromLatLon(at[0] * MathF.PI / 180f, at[1] * MathF.PI / 180f);
}

/// <summary>How coasts are roughened from the outlines, and their cliffs.</summary>
internal sealed class Coast
{
    public required float Warp { get; init; }            // how far (m) the coast wanders from the outline
    public required float WarpWavelength { get; init; }  // the size (m) of its bays and headlands
    public required float Cliffs { get; init; }          // the share of coast (0-1) that is cliffs
    public required float CliffHeight { get; init; }     // metres
    public required float Roughness { get; init; }       // how far (m) fractal noise moves the coast in and out
    public required float RoughWavelength { get; init; } // metres: the largest of its bays and headlands
    public required float CalmRadius { get; init; }      // metres past a site's edge before the coast wanders fully
}

/// <summary>The land's rise from the coast, and its hills and plateaus, before erosion.</summary>
internal sealed class Relief
{
    public required float CoastRise { get; init; }          // metres the land rises over the first few km inland
    public required float CoastRiseLength { get; init; }    // metres: the rise's length scale
    public required float Hills { get; init; }              // metres: rolling hills' amplitude
    public required float HillWavelength { get; init; }     // metres
    public required float Plateaus { get; init; }           // metres: how high plateaus stand
    public required float PlateauWavelength { get; init; }  // metres
    public required float Roughness { get; init; }          // share of the relief added as rough noise for the water to organise
    public required float CalmRadius { get; init; }         // metres past a site's edge before hills reach full size
}

internal sealed class LandMass
{
    public required string Name { get; init; }
    public required List<float[]> Outline { get; init; }  // [lat, lon] corners, joined by straight lines in lat/lon
}

internal sealed class SeaLayout
{
    public required string Name { get; init; }
    public required float[] At { get; init; }    // a point in the sea; water joined to it by water is this sea's
    public required SeaKind Kind { get; init; }
    public required float Depth { get; init; }   // metres below sea level, out in its basin
    public required float Shelf { get; init; }   // metres: the width of the shallow shelf off its coasts
    public float Banks { get; init; }            // 0-1: how much of its basin rises to shallow banks
    public required float Waves { get; init; }   // metres from crest to trough of its biggest waves
    public required float[] Shallow { get; init; } // [r, g, b] display colour of its water over the shelf
    public required float[] Deep { get; init; }    // [r, g, b] display colour over deep water
    public IceLayout? Ice { get; init; }           // an icy sea's frozen sheet and floes round its centre (at); none if left out

    public SeaInfo Info() => new(Name, Kind, Waves,
        new System.Numerics.Vector3(Shallow[0], Shallow[1], Shallow[2]), new System.Numerics.Vector3(Deep[0], Deep[1], Deep[2]));
}

/// <summary>The sea ice round an icy sea's centre: a frozen sheet, its edge wandering, and a band of floes out from it.</summary>
internal sealed class IceLayout
{
    public required float Pack { get; init; }   // metres from the sea's centre to the frozen sheet's edge, on average (it wanders by a quarter of this)
    public required float Floes { get; init; }  // metres: the width of the band of floes out from the sheet's edge
}

internal sealed class RangeLayout
{
    public required string Name { get; init; }
    public required List<float[]> Path { get; init; }  // its crest line, [lat, lon] points
    public required float Height { get; init; }        // metres: its highest peaks, before erosion
    public required float Width { get; init; }         // metres from the crest to its foot
}

internal sealed class DesertRule
{
    public required float MaxLatitude { get; init; }         // degrees
    public required float MinDistanceFromSea { get; init; }  // metres
    public required float MaxHeight { get; init; }           // metres: higher ground is scrub and rock
    public required float Coverage { get; init; }            // 0-1: how much of the land the rule allows is desert
}

internal sealed class SnowRule
{
    public required float MinLatitude { get; init; }  // degrees: snow towards the poles from here
    public required float MinHeight { get; init; }    // metres: and on peaks above this anywhere
}

internal sealed class SiteLayout
{
    public required string Name { get; init; }
    public float[]? At { get; init; }           // omitted for the game's own sites (Program takes them from Scene)
    public required float Radius { get; init; } // metres levelled flat at elevation 0
    public required float Blend { get; init; }  // metres over which it blends back to the terrain
}

internal sealed class ErosionLayout
{
    public required int[] Iterations { get; init; } // per level, coarsest first: each level is twice the resolution of the last, the last the map's
    public required float Incision { get; init; }   // how deep running water cuts: at rest, depth below the layout = incision x (sqrt(area) - sqrt(channelArea)) x slope (finer levels cut less)
    public required float ChannelArea { get; init; }  // square metres that must drain through a texel before water cuts a channel there
    public required float Diffusion { get; init; }  // how much slopes soften each iteration (0-1; finer levels soften more)
    public required float Talus { get; init; }      // degrees: steeper loose slopes slump
}
