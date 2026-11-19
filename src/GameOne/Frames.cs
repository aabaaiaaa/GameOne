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
    /// A vessel's own frame at <paramref name="body"/>, by its <paramref name="height"/> (its centre
    /// of mass's over the body's datum), whatever its orbit: the orbital one once it is above the
    /// body's orbit floor (<see cref="GroundBody.OrbitFloor"/>: the top of its air, or with no air
    /// its highest ground) by <see cref="Scene.FrameOrbitMargin"/>, the surface's again as it comes
    /// down under the floor. So through the ascent above the air and the circularising burn the
    /// line is the ellipse among the stars, and low down and landing it is the track over the
    /// ground. The two thresholds differ so that a vessel at the floor doesn't flicker between
    /// frames; <paramref name="current"/> is its last Auto frame. On the ground or afloat it is
    /// always the surface's. A height that isn't a finite number (a state gone bad) is the
    /// surface's too: a NaN would otherwise fail every comparison and stick in whichever frame it
    /// found.
    /// </summary>
    internal static Frame Auto(GroundBody body, Frame current, float height, bool onTheGround)
    {
        if (onTheGround || !float.IsFinite(height))
            return Frame.Surface;
        var floor = body.OrbitFloor;
        if (current == Frame.Orbital)
            return height < floor ? Frame.Surface : Frame.Orbital;
        return height > floor + Scene.FrameOrbitMargin ? Frame.Orbital : Frame.Surface;
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
}

/// <summary>
/// Where the points of one leg of a patched path (<see cref="PathLeg"/>: an orbit round its body,
/// among that body's stars in the frame that matches its ground at <c>now</c>) are drawn, in
/// another body's frame (the view's: the flown vessel's body's), as its own body sees it. Every
/// path is drawn among the stars round the body it is at, whatever the frame in use (which sets
/// only the speeds, the holds and the travel markers): an orbit is its own ellipse (or
/// hyperbola), closed on itself, never the track over its turning ground. A leg round another
/// body is placed whole, every point of it, by the change from its body's frame to the view's at
/// <c>now</c> (<see cref="Bodies.Change"/>): round that body as it is now, where it is drawn, not
/// where it will be when the path comes to it, so a pass by the moon is a hyperbola round the
/// moon's drawn place, an orbit round it a closed loop round it, and a way down a line onto its
/// ground, wherever the view is. The legs either side of a crossing then don't meet (the body
/// moves on meanwhile): the line breaks there (<see cref="Paths.LineStarts"/>). A leg round the
/// view's own body is drawn as it is.
/// </summary>
internal readonly struct LegPlace
{
    private readonly FrameChange _change; // its body's frame into the view's, now
    private readonly bool _same;

    /// <summary>For <paramref name="leg"/>, drawn in <paramref name="view"/>'s frame at <paramref name="now"/>.</summary>
    public LegPlace(in PathLeg leg, GroundBody view, double now)
    {
        _same = leg.Body == view;
        _change = _same ? FrameChange.Identity : Bodies.Change(leg.Body, view, now);
    }

    /// <summary>
    /// Where <paramref name="p"/>, in the leg's body's frame now (a point of its orbit among its
    /// stars, or a place on its ground as it is now), is drawn in the view's frame.
    /// </summary>
    public Vector3 Point(Vector3 p) => _same ? p : _change.Point(p).ToSingle();
}
