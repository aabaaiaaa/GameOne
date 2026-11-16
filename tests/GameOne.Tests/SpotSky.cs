using System.Numerics;

namespace GameOne.Tests;

/// <summary>
/// The sky over the moon's landing spot (<see cref="Scene.MoonLandingSpot"/>): whether the sun is up
/// there by the game's own rule for dark, and whether the planet's top limb is above its horizon.
/// A lander wants both, so a world that puts one there starts at a <see cref="FairTime"/>.
/// </summary>
internal static class SpotSky
{
    /// <summary>How often the sky is looked at, in world seconds.</summary>
    public const double Step = 5.0;

    /// <summary>How long after a fair time both must hold, in world seconds.</summary>
    public const double Holds = 120.0;

    /// <summary>The spot on the moon's ground, in the moon's frame.</summary>
    public static Vector3 Place { get; } = SpotPlace();

    private static Vector3 SpotPlace()
    {
        var direction = CubeSphere.FromLatLon(
            float.DegreesToRadians(Scene.MoonLandingSpot.Latitude), float.DegreesToRadians(Scene.MoonLandingSpot.Longitude));
        return GroundBody.Moon.Centre + direction * (GroundBody.Moon.Radius + MoonGround.Height(direction));
    }

    /// <summary>Whether it is not dark at the spot at <paramref name="t"/>, by the game's rule held as <paramref name="wasLit"/> says.</summary>
    public static bool Lit(double t, bool wasLit) => !Sun.IsDark(GroundBody.Moon, Place, t, !wasLit);

    /// <summary>Whether the planet's top limb is above the spot's horizon at <paramref name="t"/>: its centre's elevation, as the readout works it, plus its half-width.</summary>
    public static bool PlanetSeen(double t)
    {
        var moon = GroundBody.Moon;
        var away = Bodies.CentreIn(GroundBody.Planet, moon, t) - (Vector3Double)Place;
        var distance = away.Length();
        var up = (Vector3Double)moon.Up(Place);
        var centre = Math.Asin(Math.Clamp((up.X * away.X + up.Y * away.Y + up.Z * away.Z) / distance, -1.0, 1.0));
        return centre + Math.Asin(GroundBody.Planet.Radius / distance) > 0.0;
    }

    /// <summary>
    /// The first world time after <paramref name="after"/>, by <see cref="Step"/>, with the sun up and
    /// the planet in the sky, and both for the next <see cref="Holds"/> seconds. Looks over a year.
    /// </summary>
    public static double FairTime(double after)
    {
        var year = Scene.YearDays * (double)Scene.DaySeconds;
        var lit = false;
        for (var t = after + Step; t <= after + year; t += Step)
        {
            lit = Lit(t, lit);
            if (lit && PlanetSeen(t) && HoldsFrom(t))
                return t;
        }
        throw new InvalidOperationException($"No fair time at the landing spot within a year of {after}");
    }

    private static bool HoldsFrom(double t)
    {
        var lit = true;
        for (var dt = Step; dt <= Holds; dt += Step)
        {
            lit = Lit(t + dt, lit);
            if (!lit || !PlanetSeen(t + dt))
                return false;
        }
        return true;
    }
}
