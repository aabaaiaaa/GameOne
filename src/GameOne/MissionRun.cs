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

    /// <summary>Whether it is still going, or how it ended (and why: <see cref="EndReason"/>).</summary>
    public RunEnd End { get; internal set; }

    public string? EndReason { get; internal set; }

    /// <summary>Its flight's world time, for the steps that measure by it.</summary>
    public double WorldTime => _flight.WorldTime;

    /// <summary>Whether every needed step is done. Never, for a mission with no steps: there was nothing to do.</summary>
    public bool Succeeded => Mission.HasObjectives && Mission.Steps.Select((step, i) => step.Optional || Progress.IsDone(i)).All(done => done);

    /// <summary>Whether step <paramref name="step"/> (by its place in the steps) can't be done now: it isn't done, and what it needed has been destroyed.</summary>
    public bool IsLost(int step) => Mission.Steps[step].Lost is { } lost && !Progress.IsDone(step) && lost(this);

    /// <summary>
    /// Whether it has failed: a step it needs can't be done any more, because what it needed (the
    /// upper stage to land, say) was destroyed. A lost bonus doesn't fail it.
    /// </summary>
    public bool Failed => Mission.Steps.Where((step, i) => !step.Optional && IsLost(i)).Any();

    /// <summary>
    /// Its points, once it has succeeded (null until then): the steps' done so far, each as it was
    /// when done. A bonus done later adds to them.
    /// </summary>
    public int? Score => Succeeded && !Failed ? Progress.Points : null;

    /// <summary>Whether every needed step is done and a bonus is still open: neither done nor lost.</summary>
    public bool OptionalOpen => Succeeded && Mission.Steps.Where((step, i) => step.Optional && !Progress.IsDone(i) && !IsLost(i)).Any();

    internal void Add(Vessel v) => _vessels.Add(v);

    internal void Remove(Vessel v) => _vessels.Remove(v);

    /// <summary>Ticks off each step done now (see <see cref="MissionProgress.Update"/>). With none of its vessels left, nothing can do one.</summary>
    internal void Update()
    {
        if (_vessels.Count > 0)
            Progress.Update(this);
    }
}
