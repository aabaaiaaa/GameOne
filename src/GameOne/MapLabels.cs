using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace GameOne;

/// <summary>
/// The labels over the view at the path's marks (Ap, Pe, Closest, the ground's crosses, the
/// collisions'), the map's (the sun's) and the markers': gathered as each is drawn
/// (<see cref="Add"/>), laid out together (<see cref="Lay"/>) and drawn last, over them all
/// (<see cref="Draw"/>). At the map's far zooms a body's name sits on its vessels' labels: those
/// under a body's label are faded there, and the body's drawn over them, so it reads. Labels
/// piled on each other are spread apart only while the pointer is over the pile, into a column
/// beside their marks, each with a thin line back to the point it labels, so it is clear which is
/// which; away from it they go back. A marker's label spread is clicked where it is drawn
/// (<see cref="SpreadAt"/>), and a click on any spread label is on it, never off them all
/// (<see cref="OnSpread"/>).
/// </summary>
internal sealed class MapLabels
{
    /// <summary>
    /// A label as it would be drawn: its text's top left and size on the screen, the point it
    /// labels (its dot or mark), whether it is a body's (<paramref name="Over"/>: the sun's, or a
    /// body's marker's, which the others give way to).
    /// </summary>
    internal readonly record struct Placed(Vector2 Min, Vector2 Size, Vector2 Anchor, bool Over = false)
    {
        public Vector2 Max => Min + Size;
    }

    /// <summary>Where a label is drawn, how strongly (a share of its own strength), and whether with a line back to the point it labels.</summary>
    internal readonly record struct Laid(Vector2 Min, float Alpha, bool Line);

    /// <summary>
    /// How close (pixels) the pointer comes to a pile's labels to spread it, how far apart its
    /// labels are spread, and how far to the right of the pile's furthest mark its column goes, so
    /// the lines back to the marks are long enough to see.
    /// </summary>
    internal const float HoverReach = 4f, SpreadGap = 2f, SpreadAside = 28f;

    /// <summary>How near (pixels) a spread column comes to the screen's edges.</summary>
    internal const float ScreenMargin = 4f;

    private readonly List<Placed> _placed = [];
    private readonly List<(string Text, (byte R, byte G, byte B) Color, float Alpha, float Shadow, NavTarget? Target, string? Name)> _looks = [];
    private Laid[] _laid = new Laid[64];
    // Lay's own working, each label's pile and where it would be spread: grown as _laid is, so no
    // count of labels allocates a frame.
    private int[] _pile = new int[64];
    private Vector2[] _spreadTo = new Vector2[64];
    // The labels drawn spread in the last Draw: where each was (its top left and bottom right), and
    // for a marker's, which marker (its target and name). What the next frame's Lay holds a pile
    // spread by, and what the markers' clicks are judged against.
    private readonly List<(Vector2 Min, Vector2 Max, NavTarget? Target, string? Name)> _spread = [];
    private (Vector2 Min, Vector2 Max)[] _held = new (Vector2, Vector2)[16];

    /// <summary>The labels gathered this frame, as they would be drawn. For the tests.</summary>
    internal IReadOnlyList<Placed> Gathered => _placed;

    /// <summary>Starts the frame's labels afresh.</summary>
    public void Clear()
    {
        _placed.Clear();
        _looks.Clear();
    }

    /// <summary>
    /// A label of <paramref name="text"/> with its top left at <paramref name="min"/>, for the point
    /// at <paramref name="anchor"/>, in <paramref name="color"/> at <paramref name="alpha"/> with its
    /// black outline at <paramref name="shadow"/>: drawn by <see cref="Draw"/>. A marker's label
    /// names its marker (<paramref name="target"/>, <paramref name="name"/>), so a click on it
    /// spread is judged where it is drawn.
    /// </summary>
    public void Add(string text, float2 min, float2 anchor, (byte R, byte G, byte B) color, float alpha, float shadow, bool over = false,
        NavTarget? target = null, string? name = null)
    {
        var size = ImGui.CalcTextSize(text);
        Add(new Placed(new Vector2(min.X, min.Y), new Vector2(size.X, size.Y), new Vector2(anchor.X, anchor.Y), over), text, color, alpha, shadow, target, name);
    }

    /// <summary>A label already measured (<paramref name="placed"/>), as the measuring Add above would gather it: the tests' way in, with no ImGui.</summary>
    internal void Add(in Placed placed, string text, (byte R, byte G, byte B) color, float alpha, float shadow, NavTarget? target = null, string? name = null)
    {
        _placed.Add(placed);
        _looks.Add((text, color, alpha, shadow, target, name));
    }

    /// <summary>Where the marker's label (by its <paramref name="target"/> and <paramref name="name"/>) was drawn spread in the last <see cref="Draw"/>; none if it wasn't.</summary>
    public (Vector2 Min, Vector2 Max)? SpreadAt(NavTarget? target, string name)
    {
        foreach (var s in _spread)
            if (s.Name is not null && s.Target == target && s.Name == name)
                return (s.Min, s.Max);
        return null;
    }

    /// <summary>Whether <paramref name="p"/> is on a label drawn spread in the last <see cref="Draw"/>: a click there is on the pile, not off every marker.</summary>
    public bool OnSpread(Vector2 p)
    {
        foreach (var s in _spread)
            if (p.X >= s.Min.X && p.X <= s.Max.X && p.Y >= s.Min.Y && p.Y <= s.Max.Y)
                return true;
        return false;
    }

    /// <summary>Forgets the labels drawn spread (a frame with nothing drawn: the HUD hidden).</summary>
    public void Forget() => _spread.Clear();

    /// <summary>
    /// A marker as a click can find it: its shape's middle and size, its label's rectangle (where
    /// it was last drawn: spread with a pile, there), and whether that label was drawn spread.
    /// </summary>
    internal readonly record struct Clickable(Vector2 At, float Size, Vector2 LabelMin, Vector2 LabelMax, bool Spread);

    /// <summary>What a click (or the pointer) at <paramref name="p"/> is on, of <paramref name="markers"/> and the labels drawn spread in the last <see cref="Draw"/>: see <see cref="Hit(ReadOnlySpan{Clickable}, ReadOnlySpan{ValueTuple{Vector2, Vector2}}, Vector2)"/>.</summary>
    public (int Marker, bool OnPile) Hit(ReadOnlySpan<Clickable> markers, Vector2 p)
    {
        if (_spreadRects.Length < _spread.Count)
            Array.Resize(ref _spreadRects, Math.Max(_spread.Count, 2 * _spreadRects.Length));
        for (var i = 0; i < _spread.Count; i++)
            _spreadRects[i] = (_spread[i].Min, _spread[i].Max);
        return Hit(markers, _spreadRects.AsSpan(0, _spread.Count), p);
    }

    private (Vector2 Min, Vector2 Max)[] _spreadRects = new (Vector2, Vector2)[16];

    /// <summary>
    /// What a click at <paramref name="p"/> is on: the index of the marker of
    /// <paramref name="markers"/> it is on (-1 none), and whether it is on a spread pile
    /// (<paramref name="spread"/>: the labels drawn spread), where it is never off every marker.
    /// A spread label wins over anything under it (another marker's shape or its label as it
    /// lay): a marker's spread label is that marker's, and a spread label of the path's is the
    /// pile's, on no marker. Off the spread labels, the first marker (in the order they are
    /// given, the most important first) whose shape (within its size and 5 px) or label holds it.
    /// </summary>
    internal static (int Marker, bool OnPile) Hit(ReadOnlySpan<Clickable> markers, ReadOnlySpan<(Vector2 Min, Vector2 Max)> spread, Vector2 p)
    {
        for (var i = 0; i < markers.Length; i++)
            if (markers[i].Spread && InRect(p, markers[i].LabelMin, markers[i].LabelMax))
                return (i, true);
        foreach (var (min, max) in spread)
            if (InRect(p, min, max))
                return (-1, true);
        for (var i = 0; i < markers.Length; i++)
            if (Vector2.Distance(p, markers[i].At) < markers[i].Size + 5f || InRect(p, markers[i].LabelMin, markers[i].LabelMax))
                return (i, false);
        return (-1, false);
    }

    private static bool InRect(Vector2 p, Vector2 min, Vector2 max) => p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y;

    /// <summary>
    /// Lays out the labels gathered (<see cref="Lay"/>, the pointer at <paramref name="pointer"/>:
    /// none while it is on a panel or a menu is up, so nothing spreads under them; the piles spread
    /// last frame held by where they were drawn) and draws them: a spread pile's lines first, then
    /// the labels not a body's, then the bodies' over them.
    /// </summary>
    public void Draw(ImDrawListPtr draw, Vector2? pointer, Vector2 screen)
    {
        var count = _placed.Count;
        var laid = Layout(pointer, screen);
        var placed = CollectionsMarshal.AsSpan(_placed);
        for (var i = 0; i < count; i++)
        {
            if (!laid[i].Line)
                continue;
            var (_, color, alpha, _, _, _) = _looks[i];
            var anchor = placed[i].Anchor;
            var end = Vector2.Clamp(anchor, laid[i].Min, laid[i].Min + placed[i].Size); // the label's nearest point
            draw.AddLine(new float2(anchor.X, anchor.Y), new float2(end.X, end.Y), Color(color, 0.7f * alpha), 1f);
        }
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < count; i++)
            {
                if (placed[i].Over != (pass == 1))
                    continue;
                var (text, color, alpha, shadow, _, _) = _looks[i];
                var at = new float2(laid[i].Min.X, laid[i].Min.Y);
                var outline = Color((0, 0, 0), shadow * laid[i].Alpha);
                foreach (var offset in (ReadOnlySpan<float2>)[new(-1, 0), new(1, 0), new(0, -1), new(0, 1)])
                    draw.AddText(at + offset, outline, text);
                draw.AddText(at, Color(color, alpha * laid[i].Alpha), text);
            }
        }
    }

    /// <summary>
    /// Lays out the labels gathered (<see cref="Lay"/>, the pointer at <paramref name="pointer"/>,
    /// held by the last layout's spread piles), and keeps which were spread and where, for the
    /// next (<see cref="SpreadAt"/>, <see cref="OnSpread"/>): where each is drawn, on a screen
    /// <paramref name="screen"/> pixels across.
    /// </summary>
    internal ReadOnlySpan<Laid> Layout(Vector2? pointer, Vector2 screen)
    {
        var count = _placed.Count;
        if (_laid.Length < count)
            Array.Resize(ref _laid, Math.Max(count, 2 * _laid.Length));
        if (_pile.Length < count)
            (_pile, _spreadTo) = (new int[Math.Max(count, 2 * _pile.Length)], new Vector2[Math.Max(count, 2 * _pile.Length)]);
        if (_held.Length < _spread.Count)
            Array.Resize(ref _held, Math.Max(_spread.Count, 2 * _held.Length));
        for (var i = 0; i < _spread.Count; i++)
            _held[i] = (_spread[i].Min, _spread[i].Max);
        var laid = _laid.AsSpan(0, count);
        var placed = CollectionsMarshal.AsSpan(_placed);
        Lay(placed, pointer, _held.AsSpan(0, _spread.Count), laid, screen, _pile, _spreadTo);
        _spread.Clear();
        for (var i = 0; i < count; i++)
            if (laid[i].Line)
                _spread.Add((laid[i].Min, laid[i].Min + placed[i].Size, _looks[i].Target, _looks[i].Name));
        return laid;
    }

    /// <summary>
    /// Where each of <paramref name="labels"/> is drawn, into <paramref name="laid"/>, the pointer at
    /// <paramref name="pointer"/> (none: on a panel, or off the view). Labels that overlap, one on
    /// another, make a pile. Left alone, each is where it was put, in full, but a label not a body's
    /// that a body's sits on, faded to <see cref="Scene.MapLabelUnderAlpha"/> so the body's, drawn
    /// over it, reads. A pile the pointer is over (within <see cref="HoverReach"/> of a label of it as
    /// it lies) is spread: its labels stacked into a column beside the pile's marks
    /// (<see cref="SpreadAside"/> right of the furthest, or left of the nearest where the screen,
    /// <paramref name="screen"/> pixels across, has no room on the right), in their order down the
    /// screen, about where they lay but kept on the screen (a label it would be spread onto taken
    /// into it), each in full and with a line back to the point it labels. A pile spread last frame (<paramref name="held"/>: where each of
    /// its labels was drawn) stays spread while the pointer is on one of those labels, so it follows
    /// a label it has moved; a pile not spread doesn't spread from the pointer being only where its
    /// column would go.
    /// </summary>
    internal static void Lay(ReadOnlySpan<Placed> labels, Vector2? pointer, ReadOnlySpan<(Vector2 Min, Vector2 Max)> held, Span<Laid> laid, Vector2 screen) =>
        Lay(labels, pointer, held, laid, screen, new int[labels.Length], new Vector2[labels.Length]);

    /// <summary>
    /// <see cref="Lay(ReadOnlySpan{Placed}, Vector2?, ReadOnlySpan{ValueTuple{Vector2, Vector2}}, Span{Laid}, Vector2)"/>
    /// with its working kept by the caller (<paramref name="pile"/> and <paramref name="spread"/>,
    /// each at least as long as <paramref name="labels"/>), so a frame's layout allocates nothing
    /// however many labels it has.
    /// </summary>
    internal static void Lay(ReadOnlySpan<Placed> labels, Vector2? pointer, ReadOnlySpan<(Vector2 Min, Vector2 Max)> held, Span<Laid> laid, Vector2 screen,
        Span<int> pile, Span<Vector2> spread)
    {
        var n = labels.Length;
        // Each label's pile, by the least index in it (joined by overlapping, as far as that reaches).
        pile = pile[..n];
        spread = spread[..n];
        for (var i = 0; i < n; i++)
            pile[i] = i;
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
                if (Overlap(labels[i].Min, labels[i].Max, labels[j].Min, labels[j].Max, 0f))
                    Join(pile, i, j);
        for (var i = 0; i < n; i++)
            pile[i] = Root(pile, i);

        for (var i = 0; i < n; i++)
        {
            var under = false;
            if (!labels[i].Over)
                for (var j = 0; j < n && !under; j++)
                    under = labels[j].Over && Overlap(labels[i].Min, labels[i].Max, labels[j].Min, labels[j].Max, 0f);
            laid[i] = new Laid(labels[i].Min, under ? Scene.MapLabelUnderAlpha : 1f, false);
        }
        if (pointer is not { } p)
            return;

        // Each pile of more than one spread; one spread onto another label (or another pile spread)
        // takes it in, and is spread again, until none spread overlaps a label of another.
        for (var pass = 0; pass < n; pass++)
        {
            for (var i = 0; i < n; i++)
                spread[i] = labels[i].Min;
            for (var root = 0; root < n; root++)
                if (pile[root] == root && Members(pile, root) > 1)
                    Spread(labels, pile, root, spread, screen);
            var joined = false;
            for (var i = 0; i < n; i++)
                for (var j = i + 1; j < n; j++)
                    if (pile[i] != pile[j] && Overlap(spread[i], spread[i] + labels[i].Size, spread[j], spread[j] + labels[j].Size, 0f))
                    {
                        Join(pile, i, j);
                        joined = true;
                    }
            if (!joined)
                break;
            for (var i = 0; i < n; i++)
                pile[i] = Root(pile, i);
        }

        // And each drawn spread if the pointer is over it.
        for (var root = 0; root < n; root++)
        {
            if (pile[root] != root || Members(pile, root) < 2)
                continue;
            var over = false;
            for (var i = root; i < n && !over; i++)
                over = pile[i] == root
                    && (Within(p, labels[i].Min, labels[i].Max) || HeldUnder(p, spread[i], spread[i] + labels[i].Size, held));
            if (!over)
                continue;
            for (var i = root; i < n; i++)
                if (pile[i] == root)
                    laid[i] = new Laid(spread[i], 1f, true);
        }
    }

    /// <summary>
    /// Whether <paramref name="p"/> is on a label spread to <paramref name="min"/>..<paramref name="max"/>
    /// that was drawn there last frame (one of <paramref name="held"/>, to within a pixel).
    /// </summary>
    private static bool HeldUnder(Vector2 p, Vector2 min, Vector2 max, ReadOnlySpan<(Vector2 Min, Vector2 Max)> held)
    {
        if (!Within(p, min, max))
            return false;
        foreach (var (hMin, hMax) in held)
            if (Vector2.DistanceSquared(hMin, min) < 1f && Vector2.DistanceSquared(hMax, max) < 1f)
                return true;
        return false;
    }

    /// <summary>
    /// Spreads the labels of the pile <paramref name="root"/> into a column, into
    /// <paramref name="spread"/> (their top lefts): beside the pile's marks, <see cref="SpreadAside"/>
    /// right of the furthest, stacked one under another <see cref="SpreadGap"/> apart in the order
    /// they lay from top to bottom (the earlier first when level), the column centred where their
    /// middles were on average. Where it would run off the right of <paramref name="screen"/>, it
    /// goes as far left of the pile's nearest mark instead; and it is kept within
    /// <see cref="ScreenMargin"/> of the screen's edges either way.
    /// </summary>
    private static void Spread(ReadOnlySpan<Placed> labels, ReadOnlySpan<int> pile, int root, Span<Vector2> spread, Vector2 screen)
    {
        var n = labels.Length;
        var (height, middles, count, right, left, width) = (0f, 0f, 0, float.NegativeInfinity, float.PositiveInfinity, 0f);
        for (var i = root; i < n; i++)
        {
            if (pile[i] != root)
                continue;
            height += labels[i].Size.Y + (count > 0 ? SpreadGap : 0f);
            middles += labels[i].Min.Y + labels[i].Size.Y / 2;
            right = MathF.Max(right, labels[i].Anchor.X + SpreadAside);
            left = MathF.Min(left, labels[i].Anchor.X - SpreadAside);
            width = MathF.Max(width, labels[i].Size.X);
            count++;
        }
        var aside = right + width <= screen.X - ScreenMargin ? right : left - width;
        aside = Math.Clamp(aside, ScreenMargin, MathF.Max(ScreenMargin, screen.X - ScreenMargin - width));
        var top = Math.Clamp(middles / count - height / 2, ScreenMargin, MathF.Max(ScreenMargin, screen.Y - ScreenMargin - height));
        // In order down the screen: each time the highest not yet placed (no list kept: piles are small).
        var last = (Y: float.NegativeInfinity, Index: -1);
        for (var k = 0; k < count; k++)
        {
            var next = -1;
            for (var i = root; i < n; i++)
            {
                if (pile[i] != root)
                    continue;
                var y = labels[i].Min.Y + labels[i].Size.Y / 2;
                var after = y > last.Y || (y == last.Y && i > last.Index);
                if (after && (next < 0 || y < labels[next].Min.Y + labels[next].Size.Y / 2))
                    next = i;
            }
            spread[next] = new Vector2(aside, top);
            top += labels[next].Size.Y + SpreadGap;
            last = (labels[next].Min.Y + labels[next].Size.Y / 2, next);
        }
    }

    /// <summary>How many labels are in the pile <paramref name="root"/> (every one after it: its root is its least index).</summary>
    private static int Members(ReadOnlySpan<int> pile, int root)
    {
        var count = 0;
        for (var i = root; i < pile.Length; i++)
            if (pile[i] == root)
                count++;
        return count;
    }

    private static int Root(Span<int> pile, int i)
    {
        while (pile[i] != i)
            i = pile[i] = pile[pile[i]];
        return i;
    }

    private static void Join(Span<int> pile, int a, int b)
    {
        var (ra, rb) = (Root(pile, a), Root(pile, b));
        if (ra != rb)
            pile[Math.Max(ra, rb)] = Math.Min(ra, rb); // the least index stays the root
    }

    /// <summary>Whether two rectangles overlap, or come within <paramref name="gap"/> of it.</summary>
    private static bool Overlap(Vector2 aMin, Vector2 aMax, Vector2 bMin, Vector2 bMax, float gap) =>
        aMin.X < bMax.X + gap && bMin.X < aMax.X + gap && aMin.Y < bMax.Y + gap && bMin.Y < aMax.Y + gap;

    /// <summary>Whether <paramref name="p"/> is on a label from <paramref name="min"/> to <paramref name="max"/>, or within <see cref="HoverReach"/> of it.</summary>
    private static bool Within(Vector2 p, Vector2 min, Vector2 max) =>
        p.X >= min.X - HoverReach && p.X <= max.X + HoverReach && p.Y >= min.Y - HoverReach && p.Y <= max.Y + HoverReach;

    private static ImColor8 Color((byte R, byte G, byte B) color, float alpha) =>
        new(color.R, color.G, color.B, (byte)(255 * Math.Clamp(alpha, 0f, 1f)));
}
