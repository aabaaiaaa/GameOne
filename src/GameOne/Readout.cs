using System.Numerics;
using System.Text;

namespace GameOne;

/// <summary>
/// What the HUD shows, as text, for a tool watching the game from outside: the GameDriver, whose
/// scripts wait on readings ("until Altitude >= 400"), so they fly by what a player can see. The
/// driver is its own application, for any game; this is GameOne's side of its interface, kept here
/// rather than through the game. Only when <c>GAMEDRIVER_READOUT=1</c>, as the driver sets: each
/// frame, every reading that changed goes to stdout as <c>hud name=value</c> (<c>hud -name</c> once
/// it is no longer shown), by the name the HUD shows it under and in the same format.
/// </summary>
internal sealed class Readout
{
    /// <summary>Whether anything is listening: when not, the game doesn't make a readout at all.</summary>
    public static readonly bool On = Environment.GetEnvironmentVariable("GAMEDRIVER_READOUT") == "1";

    private Dictionary<string, string> _shown = [], _last = [];
    private readonly StringBuilder _lines = new();

    /// <summary>After the frame is drawn: writes whatever the HUD showed that it didn't the frame before.</summary>
    public void Write(Flight flight, Hud hud)
    {
        Read(flight, hud);
        foreach (var (name, value) in _shown)
            if (!_last.TryGetValue(name, out var was) || was != value)
                _lines.Append("hud ").Append(name).Append('=').Append(value).Append('\n');
        foreach (var name in _last.Keys)
            if (!_shown.ContainsKey(name))
                _lines.Append("hud -").Append(name).Append('\n');
        if (_lines.Length > 0)
        {
            Console.Out.Write(_lines.ToString());
            Console.Out.Flush();
            _lines.Clear();
        }
        (_last, _shown) = (_shown, _last);
        _shown.Clear();
    }

    /// <summary>The HUD's readings this frame, shown as it shows them (see <see cref="Hud"/>'s instruments and mission panel).</summary>
    private void Read(Flight flight, Hud hud)
    {
        Show("Menu", hud.Screen.ToString());
        Show("Controls", hud.ControlsOpen ? "open" : "folded"); // the controls list, bottom left
        Show("Camera", CameraSubject.Describe(flight, hud.CameraOn)); // what the main camera is on: Own, or another vessel, a body, a site or a point of interest
        if (hud.Hidden)
            Show("HUD", "hidden"); // F2: nothing over the main view but a menu
        if (hud.TimeScale != 1f)
            Show("Time", $"{hud.TimeScale:0.##}x");

        ReadFlight(flight, _shown);
        if (Timings.Enabled && Timings.Summary is { } timings)
            ReadTimings(timings, _shown);

        // Where each marker is on the screen, from its middle, as the driver's clicks are given,
        // so a script can click one ("click marker"): only those drawn this frame, as they move with
        // the camera. Two with the same name (two boosters) read as the first drawn (the one that comes first,
        // as the markers rank them). The coming mark on the vessel coming at the flown one (its own, not
        // among the markers: shown whatever their settings) is its own reading, by its name as the
        // log has it and where it is, as theirs; and as it is clicked as its vessel's marker, it is
        // listed among them too, by that name ("Booster 1", which no ordinary marker has), first, as
        // it is drawn over them.
        if (hud.ComingMark is ({ } comingName, var mark))
        {
            Show("Coming mark", $"{comingName} {mark.X:0} {mark.Y:0}");
            _shown.TryAdd($"Marker {comingName}", $"{mark.X:0} {mark.Y:0}");
        }
        foreach (var (name, at) in hud.MarkersDrawn)
            _shown.TryAdd($"Marker {name}", $"{at.X:0} {at.Y:0}");
        // The strip's buttons, which move with the instruments' panel, the mission menu's ("Mission:
        // FREE PLAY", "Site: Home", "Change world"), which move with its height, the world list's
        // ("Play: Empty world", "Delete: Empty world", "Yes, delete it", "No", "New: Empty world"),
        // which move with its rows and their scroll, and the open "Add view" list's entries, by the
        // vessel's name ("Add view: Satellite"; two of a name read as the first listed): read out as
        // markers are, so a script clicks one wherever it is ("click marker").
        foreach (var (name, at) in hud.ButtonsDrawn)
            _shown.TryAdd($"Marker {name}", $"{at.X:0} {at.Y:0}");
        foreach (var (vessel, at) in hud.ViewsOffered)
            _shown.TryAdd($"Marker Add view: {vessel.Name}", $"{at.X:0} {at.Y:0}");
        // The flown vessel's actions: its middle on the main view, for a script to right-click
        // ("rclick marker"), and while its menu is open, that it is (its items are among the buttons above).
        if (hud.FlownVesselAt is { } flown)
            _shown.TryAdd("Marker Flown vessel", $"{flown.X:0} {flown.Y:0}");
        if (hud.ActionsOpen)
            Show("Actions", "open");
        // The predicted collisions' crosses the map drew (see Paths): how many, as a script can't read the map's lines.
        Show("Collisions", $"{hud.CollisionsDrawn}");

        // The carry-on screen's choices, in its order, each its button's text and its line (its
        // button is among the markers above, as "Carry on: <mission>" for a run, "Carry on: <vessel>"
        // by the log's name for one of no run); and the word of a run ended unflown, while it shows.
        if (hud.Screen == MenuScreen.CarryOn)
        {
            var rows = hud.CarryOnRows;
            for (var i = 0; i < rows.Count; i++)
                Show($"Carry on {i + 1}", $"{rows[i].Label} / {rows[i].Line}");
        }
        if (hud.MustChoose)
            Show("Must choose", "yes"); // a world just picked: Esc and Close leave neither the carry-on screen nor the missions
        if (hud.NoticeShown is { } notice)
            Show("Notice", notice);

        var (title, lines) = hud.LastMessage;
        if (title is not null)
            Show("Message", title);
        if (lines is { Count: > 0 })
            Show("Message text", string.Join(" / ", lines.Select(l => l.Text)));
    }

    /// <summary>
    /// What <see cref="Write"/> would emit now, less the HUD's own readings (the menu, the controls
    /// list, the time and the big message): the readings that are the flight's alone, which need
    /// a flight but no HUD: read-only inspection for the tests (the game only calls
    /// <see cref="Write"/>).
    /// </summary>
    internal static Dictionary<string, string> FlightReadings(Flight flight)
    {
        var shown = new Dictionary<string, string>();
        ReadFlight(flight, shown);
        return shown;
    }

    /// <summary>
    /// <paramref name="body"/>'s centre's angle above the flown vessel's horizon, in degrees
    /// (negative below it): by the up of the body the vessel is at, and the centre as that body's
    /// frame has it then (<see cref="Bodies.CentreIn"/>). The body it is at is always straight
    /// down (-90°); seen from the moon, the planet rises and sets.
    /// </summary>
    internal static float HeightOf(Flight flight, GroundBody body)
    {
        var at = flight.Position;
        var up = flight.Body.Up(at);
        var towards = Vector3.Normalize(Bodies.CentreIn(body, flight.Body, flight.WorldTime).ToSingle() - at);
        return float.RadiansToDegrees(MathF.Asin(Math.Clamp(Vector3.Dot(up, towards), -1f, 1f)));
    }

    /// <summary>How far the flown vessel is above <paramref name="body"/>'s datum (the zero of its heights: the sea on the planet), straight to its centre, in the flown vessel's body's frame.</summary>
    internal static float DistanceTo(Flight flight, GroundBody body) =>
        (float)(Bodies.CentreIn(body, flight.Body, flight.WorldTime) - (Vector3Double)flight.Position).Length() - body.SeaRadius;

    // Each body's readings' names, by its index (made once: the readout runs every frame).
    private static readonly string[] HeightNames = [.. Bodies.All.Select(b => b.Name + " height")];

    /// <summary>The flight's readings (see <see cref="FlightReadings"/>), into <paramref name="shown"/>.</summary>
    private static void ReadFlight(Flight flight, Dictionary<string, string> shown)
    {
        void Show(string name, string value) => shown[name] = value;

        // The clocks at the top of the flight panel: the world's (Home's day and time of day) and
        // the time of day where the flown vessel is.
        Show("World time", Sun.WorldClock(flight.WorldTime));
        Show("Local time", Sun.Clock(Sun.LocalSeconds(flight.Body, flight.Position, flight.WorldTime)));
        // How lit the place nearest the flown vessel is (0 off, 1 on, between while it fades). Not
        // PadLights.Nearest: that has a reach limit and the shadows' hysteresis, and this wants
        // simply the nearest place. The fraction comes first: a script compares a reading's first
        // number, and a drone ship's name has a number of its own. The places are the sites' body's,
        // so the flown vessel is put in that body's frame to measure (from another body, across the bodies).
        var (nearest, nearestSquared) = (0, float.MaxValue);
        var from = flight.Body == Sites.Body ? flight.Position
            : Bodies.Change(flight.Body, Sites.Body, flight.WorldTime).Point((Vector3Double)flight.Position).ToSingle();
        for (var i = 0; i < PadLights.All.Count; i++)
        {
            var squared = (PadLights.All[i].Origin - from).LengthSquared();
            if (squared < nearestSquared)
                (nearest, nearestSquared) = (i, squared);
        }
        Show("Pad lights", $"{flight.Lights.Lit[nearest]:0.00}, {PadLights.All[nearest].Name}");
        Show("Landing lights", $"{flight.Controlled.LandingLights:0.00}");
        Show("Vessel", flight.Controlled.Name);
        Show("Vessel id", $"{flight.Controlled.Id}"); // which one it is, as the log names it (a world carried on flies the same one)
        Show("Position", $"{flight.Position.X:0.00} {flight.Position.Y:0.00} {flight.Position.Z:0.00}"); // in metres, in its body's frame: where it is to the centimetre
        Show("Body", flight.Body.Name); // where the flown vessel is: the body its readings below are of
        if (flight.LinkUp.Reading is { } link)
            Show("Link", link); // the link-up over the main view, switching to or carrying on a vessel: connecting, then connected
        foreach (var viewLink in flight.ViewLinks)
            if (viewLink is { Vessel: { } linked, Reading: { } reading })
                Show($"Link {WorldLog.Named(linked)}", reading); // a small view's link-up, given to a vessel at a body with no physics yet
        if (flight.WatchLink.Reading is { } watchLink)
            Show("Camera link", watchLink); // the link-up over the main view as its camera is put on a vessel at a body with no physics yet

        Show("State", flight.Controlled.State.ToString()); // Ready, Flying, Landed, Floating or Crashed
        if (flight.Warning is { } collision)
            Show("Warning", Hud.WarningLine(collision)); // a fast collision coming, in the HUD's words
        if (flight.NearMissShown is { } nearMiss)
            Show("Near miss", Hud.NearMissLine(nearMiss)); // a near miss the setting slows for, coming (not one that is the warning's collision too)
        foreach (var coming in flight.Comings) // what is coming at each controlled vessel: what a small view flashes for (red, or amber for a near miss)
        {
            var what = coming.Other is { } other ? WorldLog.Named(other) : Collisions.Name(coming.Body, coming.With, coming.At);
            Show($"Coming at {WorldLog.Named(coming.Watched)}", $"{what} in {coming.Seconds:0.0} s{(coming.NearMiss ? ", a near miss" : "")}"); // a near miss flashes amber, a collision red
        }
        if (flight.Slowed is { } slowed)
            Show("Slowed", $"{slowed.Scale:0.###}x, {slowed.Reason}"); // the forced slow-down: how slow, and what for
        Show("Altitude", $"{flight.Altitude:0} m"); // above the sea
        Show("Ground", $"{flight.Ground:0} m"); // over what is under it
        Show("Vertical", $"{flight.VerticalSpeed:0.0} m/s");
        Show("Horizontal", $"{flight.HorizontalSpeed:0.0} m/s");
        Show("Tilt", $"{flight.TiltDegrees:0} °");
        Show("Air", $"{flight.Controlled.Body.AirDensity(flight.Controlled.Body.Altitude(flight.Position)) * 100:0.0} %");
        // Where each body is from the flown vessel, in its own body's frame (the view's): how far
        // above its datum, and its centre's angle above the vessel's horizon (negative below it).
        foreach (var body in Bodies.All)
        {
            Show(body.Name, $"{DistanceTo(flight, body):0} m");
            Show(HeightNames[body.Index], $"{HeightOf(flight, body):0} °");
        }
        Show("Frame", flight.Frame switch { Frame.Orbital => "Orbit", Frame.Target => "Target", _ => "Surface" });
        Show("Frame choice", flight.FrameChoice.ToString());
        Show("Target", TargetName(flight) ?? "none"); // the picked target, by its marker's name (or "vessel <id>" once gone)
        if (flight.RelativeSpeed is { } relative)
            Show("Relative speed", $"{relative:0.0} m/s");
        if (flight.DistanceToTarget is { } toTarget)
            Show("To target", $"{toTarget:0} m");
        // The orbit round the body it is at, its heights over that body's datum (as the HUD shows them).
        if (flight.LandingSite is null || flight.Apoapsis > flight.Body.OrbitFloor)
        {
            Show("Apoapsis", !float.IsFinite(flight.Apoapsis) ? "escaping" : flight.LeavesItsPull ? "leaves its pull" : $"{flight.Apoapsis:0} m");
            Show("Periapsis", flight.Periapsis > -flight.Body.Radius * 0.9f ? $"{flight.Periapsis:0} m" : "(falls back)");
            if (flight.OrbitLap is { } lap)
                Show("Period", $"{lap:0} s"); // in seconds, where the HUD shows minutes and seconds
            Show("Orbit speed", $"{flight.OrbitSpeed:0} m/s"); // among the stars
            Show("Orbit speed needed", $"{flight.OrbitSpeedNeeded:0} m/s"); // a round orbit's where it is, or at the orbit floor if lower
        }
        Show("Throttle", $"{flight.Throttle * 100:0}%");
        Show("Fuel", $"{flight.Fuel * 100:0}%");
        if (flight.StageCount > 1)
            Show("Stage", $"{flight.CurrentStage.Name.ToLowerInvariant()} (stage {flight.Stage + 1} of {flight.StageCount})");
        if (flight.HasLegs)
            Show("Legs", flight.LegDeploy switch { >= 1f => "deployed", <= 0f => "stowed", _ => flight.LegsWanted ? "deploying" : "stowing" });
        if (flight.HasBay)
            Show("Bay doors", flight.BayDoors switch { >= 1f => "open", <= 0f => "closed", _ => flight.BayDoorsWanted ? "opening" : "closing" });
        Show("Hold", flight.Holding.ToString());
        if (flight.Guide is { } guide)
        {
            // The node's time in seconds, not minutes and seconds as the HUD has it, so a script can compare it.
            Show("Node", $"{guide.Seconds:0.0} s");
            Show("Delta-v left", $"{guide.Left.Length():0.0} m/s{(guide.Overshot ? "  past it" : "")}");
            Show("Burn", guide.Short ? "more than the fuel left" : $"{guide.BurnSeconds:0.0} s at full");
            Show("Node matched", guide.Matched ? "yes" : "no");
        }
        Show("Nodes", $"{flight.Controlled.Chain?.Nodes.Length ?? 0}");

        Show("Site", flight.Site.Name);
        Show("Mission", flight.Mission.Name);
        if (flight.Run is { } run)
        {
            var steps = run.Mission.Steps;
            for (var i = 0; i < steps.Length; i++)
                Show(steps[i].Label, run.Progress.IsDone(i) ? "[x]" : run.IsLost(i) ? "[!]" : "[ ]");
            if (run.Failed)
                Show("Mission failed", "yes");
        }
        Show("Best altitude", $"{flight.MaxAltitude:0} m");
        if (flight.Run?.Score is { } score)
            Show("Score", $"{score}");
        // The flown vessel's run, by the log's name ("LAUNCH AND LAND (flight 1)", with how it ended
        // once it has: shown until another vessel is flown), or none; and every run in progress, by
        // that name, with its steps done and points so far, whichever vessel is flown.
        Show("Run", flight.Run is { } flown
            ? WorldLog.Named(flown) + flown.End switch { RunEnd.Completed => ", complete", RunEnd.Failed => ", failed", RunEnd.Abandoned => ", abandoned", _ => "" }
            : "none");
        var runs = flight.Runs;
        Show("Runs in progress", $"{runs.Count}");
        foreach (var r in runs)
        {
            var done = 0;
            for (var i = 0; i < r.Mission.Steps.Length; i++)
                if (r.Progress.IsDone(i))
                    done++;
            Show($"Run {WorldLog.Named(r)}", $"{done} of {r.Mission.Steps.Length} steps, {r.Progress.Points} pts");
        }
        Show("Can rewind", flight.CanRewind ? $"{flight.RewindSeconds:0} s" : "no"); // how far back the try-again window's rewind goes, in the world's seconds
    }

    /// <summary>
    /// The timings panel's numbers (see <see cref="Hud"/>'s timings panel), while "Show timings" is
    /// on: all of them, whole, wherever the panel is and however much of it fits, so a script reads
    /// them here rather than off the screen; and the pieces of the tiles', the contact's and the rules' parts
    /// (<see cref="StepDetail"/>), which the panel hasn't room for. Each is named "Timings ..." (the panel's own words, kept
    /// apart from the flight's readings: its "Frame" is not the frame of reference). They change once
    /// a second, with the panel's average.
    /// </summary>
    internal static void ReadTimings(TimingsSummary t, Dictionary<string, string> shown)
    {
        void Show(string name, string value) => shown["Timings " + name] = value;
        string Ms(double ms) => double.IsNaN(ms) ? "n/a" : $"{ms:0.00} ms";

        Show("frame", Ms(t.FrameMs));
        Show("fps", $"{t.Fps:0}");
        Show("warp asked", $"{t.WarpAsked:0.##}x");
        Show("warp reached", $"{t.WarpReached:0.#}x");
        Show("physics", Ms(t[StepPart.Jolt] + t[StepPart.Tiles] + t[StepPart.Water] + t[StepPart.Contact] + t[StepPart.Rest]));
        Show("steps a frame", $"{t.PhysicsSteps:0.#}");
        Show("Jolt", Ms(t[StepPart.Jolt]));
        Show("ground tiles", Ms(t[StepPart.Tiles]));
        // Their pieces, within them (see StepDetail), which the panel hasn't room for.
        Show("ground tiles: heights", Ms(t[StepDetail.Samples]));
        Show("ground tiles: Jolt bodies made", Ms(t[StepDetail.Bodies]));
        Show("ground tiles: added", Ms(t[StepDetail.Adds]));
        Show("ground tiles: taken out", Ms(t[StepDetail.Removals]));
        Show("water and rafts", Ms(t[StepPart.Water]));
        Show("contact and nearness", Ms(t[StepPart.Contact]));
        Show("contact and nearness: movers", Ms(t[StepDetail.Movers]));
        Show("contact and nearness: near", Ms(t[StepDetail.Near]));
        Show("contact and nearness: grounded", Ms(t[StepDetail.Grounded]));
        Show("contact and nearness: freezing", Ms(t[StepDetail.Freezing]));
        Show("contact and nearness: wrecks and parting", Ms(t[StepDetail.Wrecks]));
        Show("contact and nearness: touches", Ms(t[StepDetail.Touches]));
        Show("contact and nearness: vessels' checks", Ms(t[StepDetail.Vessels]));
        Show("the rest", Ms(t[StepPart.Rest]));
        Show("rules", Ms(t[StepPart.Rules]));
        Show("rules: collisions ahead", Ms(t[StepDetail.Collisions]));
        Show("rules: under formula", Ms(t[StepDetail.Formula]));
        Show("rules: collisions under formula", Ms(t[StepDetail.FormulaCollisions]));
        Show("prepare", Ms(t[FramePart.Prepare]));
        Show("HUD", Ms(t[FramePart.Hud]));
        Show("shadows", Ms(t[FramePart.Shadows]));
        Show("record", Ms(t[FramePart.Record]));
        Show("submit", Ms(t[FramePart.Submit]));
        Show("waiting for the GPU", Ms(t[FramePart.Wait]));
        Show("GPU", Ms(t.GpuMs));
        Show("GPU shadows", Ms(t[GpuPass.Shadows]));
        Show("GPU scene", Ms(t[GpuPass.Scene]));
        Show("GPU effects", Ms(t[GpuPass.Effects]));
        Show("GPU small views", Ms(t[GpuPass.SmallViews]));
        Show("GPU HUD", Ms(t[GpuPass.Hud]));
        Show("awake", $"{t.Awake}");
        Show("frozen", $"{t.Frozen}");
        Show("afloat", $"{t.Afloat}");
        Show("ground tiles laid", $"{t.Tiles}");
        Show("under formula", $"{t.UnderFormula}"); // out of the physics (those in it are the awake and the frozen)
    }

    /// <summary>
    /// The flown vessel's target's name, as its marker has it, if it has one. A vessel no longer in
    /// the world (gone since the flight last looked) reads as <c>vessel &lt;id&gt;</c>, so a target that is
    /// set never reads as none.
    /// </summary>
    private static string? TargetName(Flight flight)
    {
        switch (flight.Target)
        {
            case { Kind: TargetKind.Site, Id: var site }:
                return Sites.All[site].Name;
            case { Kind: TargetKind.Point, Id: var point }:
                return point >= 0 && point < flight.Mission.PointsOfInterest.Length ? flight.Mission.PointsOfInterest[point].Name : $"point {point}";
            case { Kind: TargetKind.Body, Id: var body }:
                return Bodies.All[body].Name;
            case { Kind: TargetKind.Vessel, Id: var id }:
                foreach (var vessel in flight.WorldVessels)
                    if (vessel.Id == id)
                        return vessel.Name;
                return $"vessel {id}";
            default:
                return null;
        }
    }

    private void Show(string name, string value) => _shown[name] = value;
}
