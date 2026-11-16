using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerrainGen;

// The moon's authored layout (moon.json): its big features placed by hand, which the moon's
// generator turns into its map. Positions are [latitude, longitude] in degrees, moon-fixed (the
// near side's middle at [0, 0], north towards the moon's north pole, east +longitude); sizes and
// heights are in metres. README.md describes each field.

internal sealed class MoonLayout
{
    public required int Seed { get; init; }
    public required int Resolution { get; init; }
    public required List<CraterLayout> Craters { get; init; }
    public required CanyonLayout Canyon { get; init; }
    public required TailLayout Tail { get; init; }
    public required SmallCratersLayout SmallCraters { get; init; }
    public required FlatsLayout Flats { get; init; }
    public required PlainsLayout Plains { get; init; }
    public required MoonSiteLayout Site { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, // a misspelt field is an error, not ignored
    };

    public static MoonLayout Load(string path)
    {
        var layout = JsonSerializer.Deserialize<MoonLayout>(File.ReadAllText(path), Options)
                     ?? throw new InvalidDataException($"{path} is empty");
        layout.Check();
        return layout;
    }

    private void Check()
    {
        if (Resolution < 16) throw new InvalidDataException("resolution must be at least 16");
        if (Tail.Path.Count < 2) throw new InvalidDataException("the tail's path needs two points or more");
        if (Tail.TipWidth <= 0f || Tail.BaseWidth <= 0f || Tail.MountainRadius <= Tail.CraterDiameter * 0.5f || Tail.CraterDepth <= 0f || Tail.Wander < 0f)
            throw new InvalidDataException("the tail needs widths and a crater depth above 0, no negative wander, and its mountain wider than its crater");
        foreach (var at in Craters.Select(c => c.At).Append(Canyon.At).Concat(Tail.Path))
            if (at.Length != 2) throw new InvalidDataException("a position is [latitude, longitude]");
        foreach (var crater in Craters)
            if (crater.Diameter <= 0f || crater.Depth <= 0f || crater.RimHeight < 0f || crater.Rays < 0)
                throw new InvalidDataException($"{crater.Name} needs a diameter and depth above 0, and no negative rim or rays");
        if (Canyon.Radius <= 0f || Canyon.Width <= 0f || Canyon.Depth <= 0f || Canyon.Wander < 0f || Canyon.Branches < 0 || Canyon.EndBearing <= Canyon.StartBearing)
            throw new InvalidDataException("the canyon needs a radius, width and depth above 0, no negative wander or branches, and its end bearing after its start");
        if (SmallCraters.Count < 0 || SmallCraters.Smallest <= 0f || SmallCraters.Largest < SmallCraters.Smallest)
            throw new InvalidDataException("the small craters need a count, and their smallest above 0 and no larger than their largest");
        if (SmallCraters.NearFace is { } face && (face.Within <= 0f || face.Largest < SmallCraters.Smallest))
            throw new InvalidDataException("the near face's clearing needs a reach above 0 and its largest crater no smaller than the smallest");
        if (Flats.Share is <= 0f or >= 1f || Flats.Wavelength <= 0f || Flats.Density is < 0f or > 1f || Flats.Largest < SmallCraters.Smallest)
            throw new InvalidDataException("the flats need a share between 0 and 1, a wavelength above 0, a density from 0 to 1, and their largest crater no smaller than the smallest");
    }
}

/// <summary>A big crater: a bowl below the plains, a raised rim (paler), and its thrown-out rock round it, in streaks (rays).</summary>
internal sealed class CraterLayout
{
    public required string Name { get; init; }
    public required float[] At { get; init; }        // its centre, [lat, lon]
    public required float Diameter { get; init; }    // metres, rim crest to rim crest
    public required float Depth { get; init; }       // metres its floor's middle is below the plains
    public required float RimHeight { get; init; }   // metres its rim's crest is above them
    public required int Rays { get; init; }          // how many pale streaks of thrown-out rock run out from it
}

/// <summary>
/// The canyon: an arc of a circle round <c>At</c>, <c>Radius</c> metres out along the ground, from
/// <c>StartBearing</c> clockwise to <c>EndBearing</c> (compass degrees from <c>At</c>: 180 is due
/// south), a flat floor between steep walls, shallowing to nothing at its two horns. It is rough,
/// not an even arc: its middle line wanders in and out by about <c>Wander</c>, its width and depth
/// swing along it, its walls are ragged, slumps fill it part way here and there, and
/// <c>Branches</c> side canyons leave its outer wall.
/// </summary>
internal sealed class CanyonLayout
{
    public required float[] At { get; init; }
    public required float Radius { get; init; }         // metres from At to the canyon's middle line, on average
    public required float Wander { get; init; }         // metres its middle line wanders in and out from that, about
    public required int Branches { get; init; }         // how many side canyons leave it
    public required float StartBearing { get; init; }   // degrees
    public required float EndBearing { get; init; }     // degrees, more than StartBearing (it may pass 360)
    public required float Width { get; init; }          // metres from rim to rim across it
    public required float Depth { get; init; }          // metres its floor is below the plains
}

/// <summary>
/// The far side's tail: a long ridge along <c>Path</c> (a smooth curve through its points, tip
/// first), narrow and low at its tip and rising and widening to its base, where it meets a
/// mountain with a crater in its top. Rough, as the canyon is: its line wanders and its crest
/// rises and falls a little.
/// </summary>
internal sealed class TailLayout
{
    public required List<float[]> Path { get; init; }   // [lat, lon] points the ridge's crest curves through, from its tip to the mountain
    public required float TipHeight { get; init; }      // metres its crest stands above the plains at its tip, ...
    public required float BaseHeight { get; init; }     // ... and at the line's end, in the mountain: where the mountain's flank rises past it, the ridge goes under it, so it joins the flank as a spur well below the summit
    public required float TipWidth { get; init; }       // metres across its foot at its tip, ...
    public required float BaseWidth { get; init; }      // ... and at the mountain
    public required float Wander { get; init; }         // metres its line wanders from the smooth curve, about
    public required float MountainHeight { get; init; } // metres the mountain's summit crater's rim stands above the plains
    public required float MountainRadius { get; init; } // metres from its middle to its foot
    public required float CraterDiameter { get; init; } // metres, rim to rim, of the crater in its top
    public required float CraterDepth { get; init; }    // metres its floor is below its rim
}

/// <summary>
/// Small craters scattered by the seed over the plains (none on the big features or the landing
/// spot): many more small ones than large, overlapping where they fall together, the larger ones'
/// rims faintly pale.
/// </summary>
internal sealed class SmallCratersLayout
{
    public required int Count { get; init; }
    public required float Smallest { get; init; }   // metres across
    public required float Largest { get; init; }    // metres across
    public required float PaleOver { get; init; }   // metres across: a crater wider than this has a faintly pale rim
    public NearFaceLayout? NearFace { get; init; }  // the near side's middle kept clear of the larger ones, so the face reads from the planet
}

/// <summary>The near side's middle, where only the smaller craters fall: within <c>Within</c> degrees of [0, 0], none wider than <c>Largest</c> metres.</summary>
internal sealed class NearFaceLayout
{
    public required float Within { get; init; }    // degrees from [0, 0]
    public required float Largest { get; init; }   // metres across
}

/// <summary>
/// Patches of flat plains between the craters, laid out by low noise of about <c>Wavelength</c>
/// metres: <c>Share</c> of the ground, where craters fall at <c>Density</c> times the rate
/// elsewhere and none wider than <c>Largest</c>. The landing spot lies in one.
/// </summary>
internal sealed class FlatsLayout
{
    public required float Share { get; init; }        // of the moon's ground, 0 to 1
    public required float Wavelength { get; init; }   // metres
    public required float Density { get; init; }      // craters there, as a share of the rate elsewhere
    public required float Largest { get; init; }      // metres across: the largest crater there
}

/// <summary>The plains' gentle rise and fall: fractal noise of this height at this size.</summary>
internal sealed class PlainsLayout
{
    public required float Relief { get; init; }       // metres up and down, about
    public required float Wavelength { get; init; }   // metres: the size of its broadest swells
}

/// <summary>The landing spot (its place and radius from Scene: MoonLandingSpot, MoonLandingSpotRadius), levelled flat and blending back over this.</summary>
internal sealed class MoonSiteLayout
{
    public required float Blend { get; init; }   // metres
}
