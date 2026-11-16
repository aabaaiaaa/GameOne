namespace GameOne;

/// <summary>How a mission run stands: still going, or how it ended.</summary>
internal enum RunEnd
{
    InProgress,
    Completed,
    Failed,
    Abandoned,
}

/// <summary>
/// One flight of a mission in its world (a "run"): the mission, where it was launched from, its
/// flight number, and the vessels that are its own (the rocket, then each part that came off it).
/// Its steps are judged against those vessels alone, whichever vessel is flown, so any number of
/// runs can be in progress in a world at once, each ticking off its own steps
/// (see <see cref="MissionProgress"/>).
/// </summary>
internal sealed class MissionRun
{
    private readonly Flight _flight;
    private readonly List<Vessel> _vessels = [];

    public MissionRun(Flight flight, Mission mission, LaunchSite site, int number, double begunAt)
    {
        _flight = flight;
        Mission = mission;
        Site = site;
        Number = number;
        BegunAt = begunAt;
        Progress = new MissionProgress(mission);
    }

    /// <summary>A run in progress brought back as <paramref name="state"/> has it (see <see cref="Capture"/>), with none of its vessels yet: its flight adds them.</summary>
    public MissionRun(Flight flight, RunState state)
    {
        _flight = flight;
        Mission = state.Mission;
        Site = LaunchSites.Named(state.Site);
        Number = state.Number;
        BegunAt = state.BegunAt;
        LastFlownId = state.LastFlown;
        Progress = new MissionProgress(state.Mission, state.Done, state.Points);
    }

    /// <summary>Everything about it but its vessels, to save it or bring it back later (see <see cref="WorldState.Runs"/>).</summary>
    public RunState Capture() => new()
    {
        Mission = Mission,
        Site = Site.Name,
        Number = Number,
        Done = [.. Mission.Steps.Select((_, i) => Progress.IsDone(i))],
        Points = [.. Mission.Steps.Select((_, i) => Progress.PointsFor(i))],
        LastFlown = LastFlownId,
        BegunAt = BegunAt,
    };

    public Mission Mission { get; }

    /// <summary>Where its rocket was launched from.</summary>
    public LaunchSite Site { get; }

    /// <summary>Its flight number in its world (1 is the first): every vessel of it has this <see cref="Vessel.Flight"/>.</summary>
    public int Number { get; }

    /// <summary>The world's time when it began.</summary>
    public double BegunAt { get; }

    /// <summary>Its vessels, in the order they came to be: the rocket first, then each part as it came off.</summary>
    public IReadOnlyList<Vessel> Vessels => _vessels;

    /// <summary>How far it has got: the steps done, and their points.</summary>
    public MissionProgress Progress { get; }

    /// <summary>The <see cref="Vessel.Id"/> of the vessel of it flown last.</summary>
    public int LastFlownId { get; set; }

    /// <summary>
    /// Whether it is still going, or how it ended (and why: <see cref="EndReason"/>). An ended run
    /// is no one's: its vessels belong to no run from then on (see <see cref="Flight.RunOf"/>), and
    /// it is never saved.
    /// </summary>
    public RunEnd End { get; private set; }

    /// <summary>Why it ended: for a failed one, the label of the first needed step it lost.</summary>
    public string? EndReason { get; private set; }

    /// <summary>Its flight's world time, for the steps that measure by it.</summary>
    public double WorldTime => _flight.WorldTime;

    /// <summary>Whether every needed step is done. Never, for a mission with no steps: there was nothing to do.</summary>
    public bool Succeeded => Mission.HasObjectives && Mission.Steps.Select((step, i) => step.Optional || Progress.IsDone(i)).All(done => done);

    /// <summary>
    /// Whether step <paramref name="step"/> (by its place in the steps) can't be done now: it isn't
    /// done, and what it needed has been destroyed, or none of the run's vessels is left to do it
    /// (all removed: wrecks cleared away, or taken out to make room), or the run was completed
    /// without it (a bonus left open).
    /// </summary>
    public bool IsLost(int step) =>
        !Progress.IsDone(step) && (End == RunEnd.Completed || _vessels.Count == 0 || Mission.Steps[step].Lost is { } lost && lost(this));

    /// <summary>
    /// Whether it has failed: a step it needs can't be done any more, because what it needed (the
    /// upper stage to land, say) was destroyed. A lost bonus doesn't fail it.
    /// </summary>
    public bool Failed => FirstLost() >= 0;

    // The first needed step lost, by its place in the steps, or -1: without allocating, as the
    // flight asks it of a vessel's run whenever it looks the run up (see Flight.RunOf).
    private int FirstLost()
    {
        for (var i = 0; i < Mission.Steps.Length; i++)
            if (!Mission.Steps[i].Optional && IsLost(i))
                return i;
        return -1;
    }

    /// <summary>
    /// Its points, once it has succeeded (null until then): the steps' done so far, each as it was
    /// when done. A bonus done later adds to them.
    /// </summary>
    public int? Score => Succeeded && !Failed ? Progress.Points : null;

    /// <summary>Whether every needed step is done and a bonus is still open: neither done nor lost.</summary>
    public bool OptionalOpen => Succeeded && Mission.Steps.Where((step, i) => step.Optional && !Progress.IsDone(i) && !IsLost(i)).Any();

    internal void Add(Vessel v) => _vessels.Add(v);

    internal void Remove(Vessel v) => _vessels.Remove(v);

    /// <summary>
    /// Ticks off each step done now (see <see cref="MissionProgress.Update"/>; with none of its
    /// vessels left, nothing can do one), then ends the run if it is over: failed once a needed
    /// step is lost (named by the first such), completed once every needed step is done and no
    /// bonus is still open. One with a bonus open waits for <see cref="Complete"/>. Nothing more
    /// happens to a run once it has ended.
    /// </summary>
    internal void Update()
    {
        if (End != RunEnd.InProgress)
            return;
        if (_vessels.Count > 0)
            Progress.Update(this);
        if (Failed)
            EndAs(RunEnd.Failed, Mission.Steps[FirstLost()].Label, null);
        else if (Succeeded && !OptionalOpen)
            EndAs(RunEnd.Completed, null, null);
    }

    /// <summary>Completes it with a bonus still open, which is lost (the player's "Complete mission"); nothing if no bonus is open.</summary>
    internal void Complete()
    {
        if (End == RunEnd.InProgress && OptionalOpen)
            EndAs(RunEnd.Completed, null, null);
    }

    /// <summary>Gives it up, for <paramref name="reason"/>, logged as <paramref name="how"/> (<see cref="WorldLog.Abandoned"/>, or <see cref="WorldLog.StartedAgain"/>).</summary>
    internal void Abandon(string reason, string how = WorldLog.Abandoned)
    {
        if (End == RunEnd.InProgress)
            EndAs(RunEnd.Abandoned, reason, how);
    }

    /// <summary>Step <paramref name="step"/> has just been done (see <see cref="MissionProgress.Update"/>): its flight logs it.</summary>
    internal void StepDone(int step) => _flight.RunStepDone(this, step);

    private void EndAs(RunEnd end, string? reason, string? how)
    {
        (End, EndReason) = (end, reason);
        _flight.RunEnded(this, how);
    }
}
