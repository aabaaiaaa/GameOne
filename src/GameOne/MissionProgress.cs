namespace GameOne;

/// <summary>
/// How far a mission run has got: which steps are done, and the points each was given when it
/// was. A step, once done, stays done (with its points as they were then), whatever happens
/// after: a booster that landed on the zone can take off again, or even crash, and its landing still
/// counts. A step not yet done can still be done later. It is its run's (<see cref="MissionRun"/>).
/// </summary>
internal sealed class MissionProgress(Mission mission)
{
    private readonly bool[] _done = new bool[mission.Steps.Length];
    private readonly int[] _points = new int[mission.Steps.Length];

    /// <summary>Whether step <paramref name="step"/> (by its place in the mission's steps) is done.</summary>
    public bool IsDone(int step) => _done[step];

    /// <summary>The points step <paramref name="step"/> was given when it was done (none if it isn't).</summary>
    public int PointsFor(int step) => _points[step];

    /// <summary>The points the steps done so far were given, together.</summary>
    public int Points { get; private set; }

    /// <summary>Ticks off each step not yet done that is done now in <paramref name="run"/>, keeping its points as they are now.</summary>
    public void Update(MissionRun run)
    {
        var steps = mission.Steps;
        for (var i = 0; i < steps.Length; i++)
        {
            if (_done[i] || !steps[i].Done(run))
                continue;
            _done[i] = true;
            _points[i] = steps[i].Points?.Invoke(run) ?? 0;
            Points += _points[i];
        }
    }
}
