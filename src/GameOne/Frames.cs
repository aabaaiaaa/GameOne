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
/// The last leg of a path round another body (an orbit it stays on there, or a way down onto its
/// ground) is placed by the change from its body's frame to the view's at the leg's start
/// (<see cref="Bodies.Change"/>), where the path comes to that body: so an orbit round the moon is
/// a closed loop round it and a way down a line onto its ground, wherever the view is, and it
/// meets the leg before it at the crossing. A leg with another after it (a pass by the moon, a
/// hyperbola that never closes, or a stretch to a node there) is placed point by point, each by
/// the change at its own time, so it meets the legs either side of it: it bends round the moon as
/// the moon moves on meanwhile. Over the ground it is the track over its body's ground
/// (<see cref="Frames.OnLine"/>), that ground placed where it is then; among the stars, its
/// body's stars as they are then, placed among the view's (as they match the view's ground now).
/// A leg round the view's own body is <see cref="Frames.OnLine"/>'s, unchanged.
/// </summary>
internal readonly struct LegPlace
{
    private readonly GroundBody _body, _view;
    private readonly FrameChange _change; // at the leg's start (unless it is placed point by point)
    private readonly double _now, _startsIn; // the world time now, and the leg's start in seconds from then
    private readonly bool _same, _byPoint;

    /// <summary>
    /// For <paramref name="leg"/>, drawn in <paramref name="view"/>'s frame at <paramref name="now"/>:
    /// <paramref name="last"/> if no leg comes after it (placed whole at its start), otherwise
    /// point by point.
    /// </summary>
    public LegPlace(in PathLeg leg, GroundBody view, double now, bool last)
    {
        (_body, _view, _now, _startsIn, _same) = (leg.Body, view, now, leg.Start - now, leg.Body == view);
        _byPoint = !_same && !last;
        _change = _same || _byPoint ? FrameChange.Identity : Bodies.Change(leg.Body, view, leg.Start);
    }

    /// <summary>Whether the leg is round the view's own body (nothing to change).</summary>
    public bool Same => _same;

    /// <summary>
    /// Where <paramref name="starsPoint"/> (a point of the leg's orbit, <paramref name="secondsFromNow"/>
    /// ahead) is drawn on a line in <paramref name="line"/>, in the view's frame.
    /// </summary>
    public Vector3 Point(Frame line, Vector3 starsPoint, double secondsFromNow)
    {
        if (_same)
            return Frames.OnLine(_body, line, starsPoint, secondsFromNow);
        // On its body's ground then (the leg's start, or the point's own time), across to the
        // view's ground then; among the stars, back among the view's as they match its ground now.
        var then = _byPoint ? secondsFromNow : _startsIn; // when its body's frame is placed
        var change = _byPoint ? Bodies.Change(_body, _view, _now + secondsFromNow) : _change;
        var ground = change.Point(Frames.OnLine(_body, Frame.Surface, starsPoint, line == Frame.Surface ? secondsFromNow : then)).ToSingle();
        return line == Frame.Surface ? ground : _view.Centre + _view.TurnedDirection(ground - _view.Centre, then);
    }

    /// <summary>A place on the leg's body's ground (where a leg there comes down, <paramref name="secondsFromNow"/> ahead), over the view's ground, as the leg is placed.</summary>
    public Vector3 Ground(Vector3 place, double secondsFromNow) =>
        _same ? place : (_byPoint ? Bodies.Change(_body, _view, _now + secondsFromNow) : _change).Point(place).ToSingle();
}
