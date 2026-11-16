namespace GameOne;

/// <summary>
/// How far a flight has got with its mission: which steps are done, and the points each was given
/// when it was. A step, once done, stays done (with its points as they were then), whatever happens
/// after: a booster that landed on the zone can take off again, or even crash, and its landing still
/// counts. A step not yet done can still be done later. It is the flight's, started afresh with each
/// flight (a loaded world starts a new one, so there is nothing of it to save).
/// </summary>
internal sealed class MissionProgress(Mission mission)
{
    private readonly bool[] _done = new bool[mission.Steps.Length];

    /// <summary>Whether step <paramref name="step"/> (by its place in the mission's steps) is done.</summary>
    public bool IsDone(int step) => _done[step];

    /// <summary>The points the steps done so far were given, together.</summary>
    public int Points { get; private set; }

    /// <summary>Ticks off each step not yet done that is done now, keeping its points as they are now.</summary>
    public void Update(Flight flight)
    {
        var steps = mission.Steps;
        for (var i = 0; i < steps.Length; i++)
        {
            if (_done[i] || !steps[i].Done(flight))
                continue;
            _done[i] = true;
            Points += steps[i].Points?.Invoke(flight) ?? 0;
        }
    }
}
