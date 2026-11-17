using System.Numerics;

namespace GameOne;

/// <summary>
/// What the main camera is on (a <see cref="CameraCentre"/>): the flown vessel, another vessel, a
/// body, a site or one of the flown vessel's mission's points of interest, found in the flight as it
/// is now. Each is at a body and given in that body's own frame; the main view is drawn in the flown
/// vessel's body's frame, so <see cref="Seen"/> changes it across, in double, for the camera to follow.
/// </summary>
internal static class CameraSubject
{
    /// <summary>
    /// What <paramref name="centre"/> names in <paramref name="flight"/>: the body it is at, the
    /// point the camera looks at in that body's frame (a vessel's middle, <see cref="Views.Focus"/>;
    /// a site's or point of interest's top; a body's centre), and whether it is a place (a site or a
    /// point of interest: looked at from beside it, <see cref="ChaseCamera.CentreOn"/>). Null if it is
    /// gone: a vessel lost or removed, or a number naming nothing.
    /// </summary>
    public static (GroundBody Body, Vector3 Focus, bool Place)? Resolve(Flight flight, CameraCentre centre)
    {
        switch (centre.Kind)
        {
            case CentreKind.Own:
                return (flight.Controlled.Body, Views.Focus(flight.Controlled), false);
            case CentreKind.Vessel:
                return VesselOf(flight, centre) is { } vessel ? (vessel.Body, Views.Focus(vessel), false) : null;
            case CentreKind.Body:
                return centre.Id >= 0 && centre.Id < Bodies.All.Count ? (Bodies.All[centre.Id], Bodies.All[centre.Id].Centre, false) : null;
            case CentreKind.Site:
                if (centre.Id < 0 || centre.Id >= Sites.All.Length)
                    return null;
                if (Sites.RaftOf(centre.Id) is { } raft) // a drone ship rides the waves: its deck as they have it now
                {
                    var ship = Rafts.Now(raft);
                    return (Sites.Body, Vector3.Transform(new Vector3(0f, ship.Freeboard, 0f), ship.Current.Model), true);
                }
                return (Sites.Body, Sites.Top(centre.Id), true);
            case CentreKind.Point:
                var points = flight.Mission.PointsOfInterest;
                return centre.Id >= 0 && centre.Id < points.Length ? (points[centre.Id].Body, points[centre.Id].Place, true) : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The vessel <paramref name="centre"/> names, if it names one still in <paramref name="flight"/>'s
    /// world in one piece (a wreck is lost); the flown vessel for <see cref="CentreKind.Own"/>.
    /// </summary>
    public static Vessel? VesselOf(Flight flight, CameraCentre centre)
    {
        if (centre.Kind == CentreKind.Own)
            return flight.Controlled;
        if (centre.Kind != CentreKind.Vessel)
            return null;
        var world = flight.WorldVessels;
        for (var i = 0; i < world.Count; i++) // by index: a foreach over the interface would box its enumerator every frame
            if (world[i].Id == centre.Id)
                return world[i].State != FlightState.Crashed ? world[i] : null;
        return null;
    }

    /// <summary>
    /// What the camera stays on: <paramref name="centre"/>, unless it is gone (<see cref="Resolve"/>),
    /// when it goes back to the flown vessel, as "Reset camera" would; and the flown vessel named by
    /// its id is its own.
    /// </summary>
    public static CameraCentre Kept(Flight flight, CameraCentre centre) =>
        centre.Kind == CentreKind.Vessel && centre.Id == flight.Controlled.Id || Resolve(flight, centre) is null ? CameraCentre.Own : centre;

    /// <summary>What <paramref name="centre"/> names, in words, as the readout says it: "Own", "Vessel 7", "Body Moon", "Site Home pad" or "Point (its name)".</summary>
    public static string Describe(Flight flight, CameraCentre centre) => centre.Kind switch
    {
        CentreKind.Own => "Own",
        CentreKind.Vessel => $"Vessel {centre.Id}",
        CentreKind.Body => centre.Id >= 0 && centre.Id < Bodies.All.Count ? $"Body {Bodies.All[centre.Id].Name}" : "Body",
        CentreKind.Site => centre.Id >= 0 && centre.Id < Sites.All.Length ? $"Site {Sites.All[centre.Id].Name}" : "Site",
        _ => centre.Id >= 0 && centre.Id < flight.Mission.PointsOfInterest.Length ? $"Point {flight.Mission.PointsOfInterest[centre.Id].Name}" : "Point",
    };

    /// <summary>Whether a subject of <paramref name="kind"/> is a place, looked at from beside it rather than chased.</summary>
    public static bool IsPlace(CentreKind kind) => kind is CentreKind.Site or CentreKind.Point;

    /// <summary>
    /// <see cref="Resolve"/>'s subject as the main camera follows it: its focus in the flown vessel's
    /// body's frame (the main view's), changed across from the subject's own body's at the flight's
    /// time in double (so one at the moon, 40 km out, is where it is to the float's step there), and
    /// that change, by which the camera takes its up, ground and pole from the subject's own body.
    /// </summary>
    public static (GroundBody Body, Vector3 Focus, FrameChange Across, bool Place)? Seen(Flight flight, CameraCentre centre)
    {
        if (Resolve(flight, centre) is not { } subject)
            return null;
        var view = flight.Body;
        if (subject.Body == view)
            return (subject.Body, subject.Focus, FrameChange.Identity, subject.Place);
        var across = Bodies.Change(subject.Body, view, flight.WorldTime);
        return (subject.Body, across.Point(subject.Focus).ToSingle(), across, subject.Place);
    }
}
