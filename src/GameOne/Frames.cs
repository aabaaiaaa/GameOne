using System.Numerics;

namespace GameOne;

/// <summary>What a velocity is measured against: the ground, the stars, or a target.</summary>
public enum Frame { Surface, Orbital, Target }

/// <summary>Which way the steering keeps the nose pointed: along the velocity in the frame in use, against it, along the burn left, or at the angles a click on the steering rings set (see <see cref="Vessel.RingAim"/>).</summary>
public enum HoldMode { None, Prograde, Retrograde, Manoeuvre, Rings }

/// <summary>What the player picks for a hold or the readout: a frame, or Auto (the vessel's own).</summary>
public enum FrameChoice { Auto, Surface, Orbit, Target }

/// <summary>
/// The frames of reference and their rules. Positions and velocities in the game are over the
/// ground (the Jolt world's frame); the orbital frame is the stars', where an orbit is an ellipse.
/// </summary>
public static class Frames
{
    /// <summary>
    /// A vessel's own frame at <paramref name="body"/>: the orbital one once its orbit clears the
    /// body's orbit floor (<see cref="GroundBody.OrbitFloor"/>: the top of its air, or with no air
    /// its highest ground) by <see cref="Scene.FrameOrbitMargin"/>, the surface's again as it dips
    /// under the floor. The two thresholds differ so that an orbit grazing the floor doesn't
    /// flicker between frames; <paramref name="current"/> is its last Auto frame. On the ground or
    /// afloat it is always the surface's, whatever its orbit would be were the ground not there. A
    /// periapsis that isn't a finite number (a state gone bad) is the surface's too: an orbit whose
    /// lowest point isn't known can't be said to clear the floor, and a NaN would otherwise fail
    /// every comparison and stick in whichever frame it found.
    /// </summary>
    internal static Frame Auto(GroundBody body, Frame current, float periapsis, bool onTheGround)
    {
        if (onTheGround || !float.IsFinite(periapsis))
            return Frame.Surface;
        var floor = body.OrbitFloor;
        if (current == Frame.Orbital)
            return periapsis < floor ? Frame.Surface : Frame.Orbital;
        return periapsis > floor + Scene.FrameOrbitMargin ? Frame.Orbital : Frame.Surface;
    }

    /// <summary>
    /// A velocity over <paramref name="body"/>'s ground (<paramref name="groundVelocity"/>) at
    /// <paramref name="position"/> in its frame, in the surface's or the orbital frame (its stars').
    /// A target's frame isn't one of these: see <see cref="Relative"/>.
    /// </summary>
    internal static Vector3 VelocityIn(GroundBody body, Frame frame, Vector3 position, Vector3 groundVelocity) => frame switch
    {
        Frame.Surface => groundVelocity,
        Frame.Orbital => body.AmongStars(position, groundVelocity),
        _ => throw new ArgumentException("A target's frame is Relative, not VelocityIn", nameof(frame)),
    };

    /// <summary>The vessel's velocity in <paramref name="baseFrame"/> less the target's, in the same frame (both at <paramref name="body"/>).</summary>
    internal static Vector3 Relative(GroundBody body, Frame baseFrame, Vector3 position, Vector3 groundVelocity, Vector3 targetPosition, Vector3 targetGroundVelocity) =>
        VelocityIn(body, baseFrame, position, groundVelocity) - VelocityIn(body, baseFrame, targetPosition, targetGroundVelocity);

    /// <summary>
    /// Where a predicted point is drawn on a line in <paramref name="line"/>: <paramref name="starsPoint"/>
    /// is a point of an <see cref="Orbit"/> round <paramref name="body"/> (among its stars, in the
    /// frame that matches its ground now), <paramref name="secondsFromNow"/> ahead. Among the stars
    /// it is itself, so an orbit is its ellipse. Over the ground it is the place under it on that
    /// body's ground by then, at its height, turned back by that body's turn: the ground turns on
    /// meanwhile, so the line is the real track over the turning ground, ending where it comes down.
    /// </summary>
    internal static Vector3 OnLine(GroundBody body, Frame line, Vector3 starsPoint, double secondsFromNow) => line switch
    {
        Frame.Surface => body.Centre + body.TurnedDirection(starsPoint - body.Centre, -secondsFromNow),
        Frame.Orbital => starsPoint,
        _ => throw new ArgumentException("A line is drawn over the ground or among the stars: a target's frame draws it in the vessel's own (Flight.LineFrame)", nameof(line)),
    };
}

/// <summary>
/// Where the points of one leg of a patched path (<see cref="PathLeg"/>: an orbit round its body,
/// among that body's stars in the frame that matches its ground at <c>now</c>) are drawn on a
/// line in another body's frame (the view's: the flown vessel's body's), as its own body sees it.
/// A leg round another body is placed whole, every point of it, by the change from its body's
/// frame to the view's at <c>now</c> (<see cref="Bodies.Change"/>): round that body as it is now,
/// where it is drawn, not where it will be when the path comes to it, so a pass by the moon is a
/// hyperbola round the moon's drawn place, an orbit round it a closed loop round it, and a way
/// down a line onto its ground, wherever the view is. Among the stars that is its orbit, rigid,
/// round its body; over the ground the track over its body's ground (<see cref="Frames.OnLine"/>),
/// that ground as it is now. The legs either side of a crossing then don't meet (the body moves
/// on meanwhile): the line breaks there (<see cref="Paths.LineStarts"/>). A leg round the view's
/// own body is <see cref="Frames.OnLine"/>'s, unchanged.
/// </summary>
internal readonly struct LegPlace
{
    private readonly GroundBody _body;
    private readonly FrameChange _change; // its body's frame into the view's, now
    private readonly bool _same;

    /// <summary>For <paramref name="leg"/>, drawn in <paramref name="view"/>'s frame at <paramref name="now"/>.</summary>
    public LegPlace(in PathLeg leg, GroundBody view, double now)
    {
        (_body, _same) = (leg.Body, leg.Body == view);
        _change = _same ? FrameChange.Identity : Bodies.Change(leg.Body, view, now);
    }

    /// <summary>Whether the leg is round the view's own body (nothing to change).</summary>
    public bool Same => _same;

    /// <summary>
    /// Where <paramref name="starsPoint"/> (a point of the leg's orbit, <paramref name="secondsFromNow"/>
    /// ahead) is drawn on a line in <paramref name="line"/>, in the view's frame.
    /// </summary>
    public Vector3 Point(Frame line, Vector3 starsPoint, double secondsFromNow)
    {
        var own = Frames.OnLine(_body, line, starsPoint, secondsFromNow);
        return _same ? own : _change.Point(own).ToSingle();
    }

    /// <summary>A place on the leg's body's ground (where a leg there comes down), over the view's ground, as the leg is placed.</summary>
    public Vector3 Ground(Vector3 place) => _same ? place : _change.Point(place).ToSingle();
}
