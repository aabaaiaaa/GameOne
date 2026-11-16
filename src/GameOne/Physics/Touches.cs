using System.Numerics;

namespace GameOne.Physics;

/// <summary>
/// What kind of solid thing a body is, kept in its Jolt user data so a contact says what touched what
/// (see <see cref="RocketPhysics.TouchesBegun"/>). The pads, the zones, their footings and the ground's
/// tiles are all <see cref="Ground"/>. 0 is no kind (the tiles' placeholders, never in the world).
/// </summary>
internal enum SolidKind : byte { Ground = 1, Tower, Mast, Raft, Craft }

/// <summary>Where on a craft a contact touched it: its feet (legs out), its engine bell, or the rest of it.</summary>
internal enum CraftPart : byte { Foot, Bell, Hull }

/// <summary>
/// A craft touching something, as Jolt's contact began (or, at its feet, as its legs first took
/// the ground or a raft: see <see cref="RocketPhysics.FeetTouch"/>, as Jolt never sees them land):
/// what it touched (<paramref name="With"/>, and the craft or the raft if it was one), where on the
/// craft (<paramref name="Part"/>), and how.
/// </summary>
/// <param name="Point">Where they touched, on the surface of the contact's first body, as Jolt gives it.</param>
/// <param name="Normal">Which way, out of what was touched and into the craft.</param>
/// <param name="Closing">
/// The craft's velocity at <paramref name="Point"/> less the other's there (a raft's, from its
/// waves; nothing for the ground, the tower, a mast or a frozen craft), both from just before the
/// step, in the ground's frame: the whole speed they touched at, any skid along the contact in it.
/// </param>
/// <param name="Fresh">
/// Whether it is a new touch: on the ground or a raft, the craft had no contact with either at
/// that place before it (so sliding from one tile onto the next is no new touch), its feet and
/// bell being one place and its hull another. Always true for a craft, the tower and a mast.
/// </param>
internal readonly record struct Touch(
    SolidKind With, Craft? Other, Raft? Raft, CraftPart Part,
    Vector3 Point, Vector3 Normal, Vector3 Closing, bool Fresh)
{
    /// <summary>How fast they closed: <see cref="Closing"/>'s length.</summary>
    public float ClosingSpeed => Closing.Length();
}
