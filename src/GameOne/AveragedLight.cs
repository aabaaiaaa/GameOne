namespace GameOne;

/// <summary>
/// How far the light is shown averaged (<see cref="Weight"/>, 0 the true light to 1 averaged):
/// from <see cref="Scene.AveragedLightRate"/> up the planet's day passes in under five real
/// seconds, at the top rate some fourteen times a second, so the sun's light, its shadows, its disc
/// and the sky's colour would only flicker. There the renderer draws an even dusk instead (the
/// sun's day's mean from straight over every place, no sun shadows, the sky's sun low over every
/// horizon, no disc, corona or flare), and the pad and landing lights hold at their place's dark
/// share of a day (<see cref="Sun.DarkShare"/>). Eased by real time over
/// <see cref="Scene.AveragedLightEaseSeconds"/>, on as the rate climbs to it and off as the warp
/// drops below it, so neither change is a jump.
/// </summary>
internal sealed class AveragedLight
{
    /// <summary>0 for the true light, 1 for the averaged, between while it eases.</summary>
    public float Weight { get; private set; }

    /// <summary>A frame of <paramref name="realSeconds"/> at <paramref name="timeScale"/>: the weight a step nearer 1 at the averaging's rates, else nearer 0.</summary>
    public void Step(float timeScale, float realSeconds)
    {
        var step = realSeconds / Scene.AveragedLightEaseSeconds;
        Weight = Averages(timeScale) ? MathF.Min(1f, Weight + step) : MathF.Max(0f, Weight - step);
    }

    /// <summary>Straight to where <paramref name="timeScale"/> puts it, with no ease (a flight begun or a world restored).</summary>
    public void Reset(float timeScale) => Weight = Averages(timeScale) ? 1f : 0f;

    private static bool Averages(float timeScale) => timeScale >= Scene.AveragedLightRate;
}
