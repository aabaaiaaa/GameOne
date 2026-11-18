using System.Numerics;

namespace GameOne;

/// <summary>
/// One vessel (or two lost together) lost, as the world's log has it (see <see cref="Flight.Log"/>);
/// or a mission run begun, one of its steps done, or the run ended, named by its flight
/// (<see cref="WorldLog.Named(MissionRun)"/>), at its site, with no speed, and always seen.
/// </summary>
/// <param name="Time">The world's time it was lost (<see cref="Flight.WorldTime"/>).</param>
/// <param name="What">The vessel's name and number: "Booster 2", or "Sat-3 and Booster 2" for two lost to one collision.</param>
/// <param name="How">What destroyed it, in a few words: its crash reason's first sentence (without its speed); or that it was removed (<see cref="WorldLog.RemovedForRoom"/>, <see cref="WorldLog.ClearedFromPad"/>) or left (<see cref="WorldLog.LeftTheSolarSystem"/>).</param>
/// <param name="Speed">How fast it hit what destroyed it, where its reason says (a collision's closing speed, a touch's), else how fast it was going (m/s); none for one removed or gone past the edge, which nothing met.</param>
/// <param name="Where">The launch site it was near, else its height ("412 km up", "on the ground", "in the sea").</param>
/// <param name="Witnessed">Whether it was in the physics when it was lost, not moved by formula: for one removed, whether the last step chose it for the physics (<see cref="Unwatched.ShouldBeInPhysics"/>), so none removed as a world is loaded, before any step has chosen, is.</param>
internal sealed record LogEntry(double Time, string What, string How, float? Speed, string Where, bool Witnessed);

/// <summary>How the world's log words what it records.</summary>
internal static class WorldLog
{
    /// <summary>Taken out to keep the world within its <see cref="Flight.Capacity"/> (the oldest first), as it is loaded or added to.</summary>
    public const string RemovedForRoom = "removed to make room";

    /// <summary>Taken off the launch pad, in the way of the new rocket put there.</summary>
    public const string ClearedFromPad = "cleared from the launch pad";

    /// <summary>Gone past the solar system's edge (<see cref="Scene.SolarSystemEdge"/>), out of the game.</summary>
    public const string LeftTheSolarSystem = "left the solar system";

    /// <summary>A mission run as the log names it (<see cref="LogEntry.What"/> of its entries): its mission and its flight number.</summary>
    public static string Named(MissionRun run) => $"{run.Mission.Name} (flight {run.Number})";

    /// <summary>A mission run begun: its rocket put on its pad.</summary>
    public const string Begun = "begun";

    /// <summary>One of a run's steps done, with the points it was given then (none said if it gave none).</summary>
    public static string StepDone(string label, int points) => points > 0 ? $"\"{label}\" done, {points:#,0} pts" : $"\"{label}\" done";

    /// <summary>A run completed, with its score.</summary>
    public static string Completed(int score) => $"completed, {score:#,0} pts";

    /// <summary>A run failed, as the step <paramref name="label"/> it needed can't be done any more.</summary>
    public static string FailedBecause(string label) => $"failed: \"{label}\" can't be done now";

    /// <summary>A run given up: by the player, or as its rocket waiting on its pad was cleared away for another.</summary>
    public const string Abandoned = "abandoned";

    /// <summary>A run given up to start its mission again, a new run in its place.</summary>
    public const string StartedAgain = "abandoned, to start it again";

    /// <summary>A vessel as the log names it: its name and its number, as vessels of one kind are many.</summary>
    public static string Named(Vessel v) => $"{v.Name} {v.Id}";

    /// <summary>One saved, as <see cref="Named(Vessel)"/> names it once brought to life: for one dropped from a world loaded with more than it holds, which never is.</summary>
    public static string Named(VesselState v) => $"{Vessel.NameOf(v.Vehicle, v.First, v.Last)} {v.Id}";

    /// <summary>
    /// What destroyed it, from its crash reason: the first sentence, up to its full stop or a
    /// colon (what follows a colon is more than a log's line wants: advice for the pilot, "deploy
    /// them (G)", or the cause spelled out, "broke up: it flew too fast through thick air"), and
    /// without the speed it gives ("at 12.5 m/s"), which the entry's own <see cref="LogEntry.Speed"/>
    /// shows (see <see cref="SpeedIn"/>), so a line never says two speeds. Full stops inside
    /// numbers ("12.5 m/s") are not the end of a sentence.
    /// </summary>
    public static string How(string? reason)
    {
        if (string.IsNullOrEmpty(reason))
            return "destroyed";
        var first = FirstSentence(reason);
        return SpeedAt(first) is { } at ? first[..at.Start] : first;
    }

    /// <summary>
    /// The speed a crash reason gives, if it gives one ("Hit another vessel at 313.5 m/s.": the two
    /// vessels' closing speed; "Hit the ground at 18.2 m/s.": its touch): how fast it hit what
    /// destroyed it, which the log shows rather than its own speed, if there is one.
    /// </summary>
    public static float? SpeedIn(string? reason) =>
        string.IsNullOrEmpty(reason) ? null : SpeedAt(FirstSentence(reason))?.Speed;

    // The first sentence of a reason, without its full stop: to ". " or ": ", or the whole.
    private static string FirstSentence(string reason)
    {
        var (stop, colon) = (reason.IndexOf(". ", StringComparison.Ordinal), reason.IndexOf(": ", StringComparison.Ordinal));
        var end = stop < 0 ? colon : colon < 0 ? stop : Math.Min(stop, colon);
        return (end < 0 ? reason : reason[..end]).TrimEnd('.');
    }

    // Where a sentence's own " at <speed> m/s" begins at its end, and the speed (written in the
    // game's culture, as the reasons are: see Vessel.HitAnotherVessel and its kin).
    private static (int Start, float Speed)? SpeedAt(string sentence)
    {
        const string Unit = " m/s";
        if (!sentence.EndsWith(Unit, StringComparison.Ordinal))
            return null;
        var at = sentence.LastIndexOf(" at ", StringComparison.Ordinal);
        return at >= 0 && float.TryParse(sentence.AsSpan(at + 4, sentence.Length - Unit.Length - at - 4), out var speed) ? (at, speed) : null;
    }

    /// <summary>
    /// Where <paramref name="p"/> is at <paramref name="body"/>, in words: the launch site within
    /// <see cref="Scene.LogSiteDistance"/> of it (where the body has the sites); else how high it
    /// is, or that it is on its ground (by its name: "on the ground", "on the moon") or in the sea.
    /// </summary>
    public static string Where(GroundBody body, Vector3 p)
    {
        var sites = LaunchSites.All;
        for (var i = 0; body.HasSites && i < sites.Length; i++)
            if (Vector3.DistanceSquared(sites[i].Pad, p) <= Scene.LogSiteDistance * Scene.LogSiteDistance)
                return sites[i].Name;
        var height = body.SurfaceAltitude(p);
        if (height > Scene.LogAirborne)
            return height >= 10000f ? $"{height / 1000f:0} km up" : height >= 1000f ? $"{height / 1000f:0.0} km up" : $"{height:0} m up";
        return body.HasSea && body.TerrainAltitude(p) < 0f ? "in the sea" : $"on {body.GroundName}";
    }
}
