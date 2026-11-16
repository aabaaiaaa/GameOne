using System.Numerics;
using GameOne.Rendering;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The labels over the view laid out together (<see cref="MapLabels.Lay"/>, a pure function of
/// their rectangles and the points they label): a vessel's labels faded where a body's sits on
/// them; and the lens flare dimmed in the map, not hidden.
/// </summary>
public sealed class MapLabelTests
{
    private static readonly Vector2 Screen = new(1280f, 720f);

    private static MapLabels.Placed At(float x, float y, bool over = false, float width = 80f) =>
        new(new Vector2(x, y), new Vector2(width, 14f), new Vector2(x - 7f, y + 8f), over);

    [Fact]
    public void A_vessels_labels_fade_where_a_bodys_sits_on_them()
    {
        // Ap and Pe at the far zoom, the moon's name on the Pe, a Closest clear of it all.
        MapLabels.Placed[] labels =
        [
            At(100f, 100f),                         // Ap: alone
            At(300f, 200f),                         // Pe: under the moon's name
            At(330f, 205f, over: true), // the moon's marker's label
            At(600f, 400f),                         // Closest: alone
        ];
        var laid = new MapLabels.Laid[labels.Length];
        MapLabels.Lay(labels, null, [], laid, Screen);
        Assert.Equal(1f, laid[0].Alpha);
        Assert.Equal(Scene.MapLabelUnderAlpha, laid[1].Alpha);
        Assert.Equal(1f, laid[2].Alpha); // the body's in full, over it
        Assert.Equal(1f, laid[3].Alpha);
        for (var i = 0; i < labels.Length; i++)
            Assert.Equal(labels[i].Min, laid[i].Min); // none moved
        Assert.All(laid, l => Assert.False(l.Line));

        // The pointer elsewhere: the same.
        var away = new MapLabels.Laid[labels.Length];
        MapLabels.Lay(labels, new Vector2(900f, 600f), [], away, Screen);
        Assert.Equal(laid, away);
    }

    [Fact]
    public void Two_of_a_vessels_labels_on_each_other_neither_fades()
    {
        MapLabels.Placed[] labels = [At(300f, 200f), At(310f, 204f)];
        var laid = new MapLabels.Laid[2];
        MapLabels.Lay(labels, null, [], laid, Screen);
        Assert.All(laid, l => Assert.Equal(1f, l.Alpha));
    }

    [Fact]
    public void A_pile_spreads_only_while_the_pointer_is_over_it_each_with_a_line()
    {
        // "Closest 880 m" over "Pe 28.2 km", the moon's name on both, and a label apart from them.
        MapLabels.Placed[] labels =
        [
            At(300f, 200f),                         // Pe
            At(304f, 203f),                         // Closest, nearly on it
            At(320f, 206f, over: true),             // the moon's
            At(700f, 500f),                         // apart
        ];
        var laid = new MapLabels.Laid[labels.Length];

        // Pointer on the pile: spread into a column beside the marks (clear of the furthest, so
        // the lines back are long enough to see), in the order they lay, none overlapping another,
        // each in full with its line; the one apart untouched.
        var aside = labels[..3].Max(l => l.Anchor.X) + MapLabels.SpreadAside;
        foreach (var pointer in (ReadOnlySpan<Vector2>)[new(310f, 208f), new(395f, 214f)])
        {
            MapLabels.Lay(labels, pointer, [], laid, Screen);
            for (var i = 0; i < 3; i++)
            {
                Assert.True(laid[i].Line);
                Assert.Equal(1f, laid[i].Alpha);
                Assert.Equal(aside, laid[i].Min.X); // the column, beside the marks
                Assert.True(laid[i].Min.X - labels[i].Anchor.X >= MapLabels.SpreadAside, "its line at least that long");
                for (var j = i + 1; j < 3; j++)
                    Assert.False(Overlaps(laid[i].Min, labels[i].Size, laid[j].Min, labels[j].Size), $"{i} and {j} still overlap");
            }
            Assert.True(laid[0].Min.Y < laid[1].Min.Y && laid[1].Min.Y < laid[2].Min.Y, "in the order they lay");
            Assert.Equal(new MapLabels.Laid(labels[3].Min, 1f, false), laid[3]);
        }

        // Spread, it stays spread with the pointer on a label where it has gone, held by where it
        // was drawn last frame; never spread, the pointer only where its column would go spreads nothing.
        MapLabels.Lay(labels, new Vector2(310f, 208f), [], laid, Screen);
        var top = laid[0]; // the top one, moved up and aside off the pile
        var onMoved = top.Min + new Vector2(40f, 2f);
        Assert.False(Within(onMoved, labels[0]) || Within(onMoved, labels[1]) || Within(onMoved, labels[2]), "the moved label is clear of where the pile was");
        (Vector2, Vector2)[] held = [.. Enumerable.Range(0, 3).Select(i => (laid[i].Min, laid[i].Min + labels[i].Size))];
        var again = new MapLabels.Laid[labels.Length];
        MapLabels.Lay(labels, onMoved, held, again, Screen);
        Assert.Equal(laid, again);
        MapLabels.Lay(labels, onMoved, [], again, Screen);
        Assert.All(again, l => Assert.False(l.Line));

        // The pointer away (or on a panel: none): back as they were, the moon's name over the faded labels under it.
        MapLabels.Lay(labels, new Vector2(100f, 600f), held, laid, Screen);
        MapLabels.Lay(labels, null, held, again, Screen);
        Assert.Equal(laid, again);
        for (var i = 0; i < labels.Length; i++)
        {
            Assert.Equal(labels[i].Min, laid[i].Min);
            Assert.False(laid[i].Line);
        }
        Assert.Equal(Scene.MapLabelUnderAlpha, laid[0].Alpha);
        Assert.Equal(Scene.MapLabelUnderAlpha, laid[1].Alpha);
        Assert.Equal(1f, laid[2].Alpha);
    }

    [Fact]
    public void A_pile_by_the_screens_edge_spreads_on_the_screen()
    {
        // A pile of six by the right edge and near the bottom: no room for the column on the right,
        // so it goes as far left of the marks, and it is kept above the bottom.
        var labels = new MapLabels.Placed[6];
        for (var i = 0; i < labels.Length; i++)
            labels[i] = At(1215f + i, 700f + 0.5f * i, width: 60f);
        var laid = new MapLabels.Laid[labels.Length];
        MapLabels.Lay(labels, new Vector2(1230f, 706f), [], laid, Screen);
        var nearest = labels.Min(l => l.Anchor.X);
        foreach (var (l, i) in laid.Select((l, i) => (l, i)))
        {
            Assert.True(l.Line);
            Assert.InRange(l.Min.X, MapLabels.ScreenMargin, Screen.X - MapLabels.ScreenMargin - labels[i].Size.X);
            Assert.InRange(l.Min.Y, MapLabels.ScreenMargin, Screen.Y - MapLabels.ScreenMargin - labels[i].Size.Y);
            Assert.True(l.Min.X + labels[i].Size.X <= nearest - MapLabels.SpreadAside + 1e-3f, "left of the marks, the lines still long enough to see");
        }
        // And by the top left corner: right of the marks, kept below the top.
        for (var i = 0; i < labels.Length; i++)
            labels[i] = At(10f + i, 2f + 0.5f * i, width: 60f);
        MapLabels.Lay(labels, new Vector2(20f, 6f), [], laid, Screen);
        Assert.All(laid, l => Assert.InRange(l.Min.Y, MapLabels.ScreenMargin, Screen.Y));
        Assert.All(laid, l => Assert.True(l.Min.X >= labels.Max(m => m.Anchor.X) + MapLabels.SpreadAside - 1e-3f));
    }

    [Fact]
    public void A_far_zooms_cluster_spreads_into_a_column()
    {
        // Eight labels on one spot (a far zoom's vessels and marks), the pointer on it.
        var labels = new MapLabels.Placed[8];
        for (var i = 0; i < labels.Length; i++)
            labels[i] = At(400f + i, 300f + 0.5f * i, width: 60f + 5f * i);
        var laid = new MapLabels.Laid[labels.Length];
        MapLabels.Lay(labels, new Vector2(420f, 306f), [], laid, Screen);
        for (var i = 0; i < labels.Length; i++)
        {
            Assert.True(laid[i].Line);
            for (var j = i + 1; j < labels.Length; j++)
                Assert.False(Overlaps(laid[i].Min, labels[i].Size, laid[j].Min, labels[j].Size), $"{i} and {j} still overlap");
        }
        // About a column's height, centred on the pile: no further than it needs.
        var (top, bottom) = (laid.Min(l => l.Min.Y), laid.Max(l => l.Min.Y) + 14f);
        Assert.InRange(bottom - top, 8 * 14f, 8 * 14f + 8 * MapLabels.SpreadGap + 1f);
        Assert.InRange((top + bottom) / 2, 295f, 320f);
    }

    [Fact]
    public void A_pile_spread_onto_a_label_beside_it_takes_it_in()
    {
        // Ap, Closest and Pe on one spot under the planet's name, and the moon's name just above
        // them, clear of them as they lie: spread, they would run into it, so it is taken in.
        MapLabels.Placed[] labels =
        [
            At(300f, 330f, over: true), // the moon's, just above
            At(310f, 346f),                         // Ap
            At(312f, 348f),                         // Closest
            At(314f, 350f),                         // Pe
            At(305f, 352f, over: true), // the planet's
        ];
        var laid = new MapLabels.Laid[labels.Length];
        MapLabels.Lay(labels, new Vector2(330f, 354f), [], laid, Screen);
        for (var i = 0; i < labels.Length; i++)
        {
            Assert.True(laid[i].Line, $"{i} spread");
            for (var j = i + 1; j < labels.Length; j++)
                Assert.False(Overlaps(laid[i].Min, labels[i].Size, laid[j].Min, labels[j].Size), $"{i} and {j} still overlap");
        }
    }

    [Fact]
    public void A_spread_labels_marker_is_clicked_where_it_is_drawn_and_a_click_on_the_pile_is_on_it()
    {
        // The moon's marker's label in a pile with Pe and Closest, the moon the target: laid out
        // with the pointer on the pile, then as the next frame finds it.
        var labels = new MapLabels();
        var moon = NavTarget.OfBody(GroundBody.Moon);
        labels.Add(At(300f, 200f), "Pe 3300 m", (255, 255, 255), 1f, 0.8f);
        labels.Add(At(304f, 203f), "Closest 24.2 km", (255, 175, 80), 1f, 0.8f);
        labels.Add(At(320f, 206f, over: true), "Moon  48 km", (225, 232, 255), 1f, 0.8f, moon, "Moon");
        var laid = labels.Layout(new Vector2(310f, 208f), Screen).ToArray();
        Assert.All(laid, l => Assert.True(l.Line));

        // The marker's label is judged where it is drawn now, not where it lay.
        var spread = labels.SpreadAt(moon, "Moon");
        Assert.NotNull(spread);
        Assert.Equal(laid[2].Min, spread.Value.Min);
        Assert.Null(labels.SpreadAt(null, "Planet")); // one not spread: where it lies
        // A click on any label of the pile (the moved Pe over the moon's old place too) is on the
        // pile: never off every marker, so the target isn't let go.
        Assert.True(labels.OnSpread(laid[0].Min + new Vector2(3f, 3f)));
        Assert.True(labels.OnSpread(spread.Value.Min + new Vector2(3f, 3f)));
        Assert.False(labels.OnSpread(new Vector2(900f, 600f)));

        // The next frame, the pointer on the moon's moved label: still spread there.
        labels.Clear();
        labels.Add(At(300f, 200f), "Pe 3300 m", (255, 255, 255), 1f, 0.8f);
        labels.Add(At(304f, 203f), "Closest 24.2 km", (255, 175, 80), 1f, 0.8f);
        labels.Add(At(320f, 206f, over: true), "Moon  48 km", (225, 232, 255), 1f, 0.8f, moon, "Moon");
        Assert.Equal(laid, labels.Layout(spread.Value.Min + new Vector2(30f, 3f), Screen).ToArray());
        // And once the pointer is on a panel, none: nothing spread, nothing to click on.
        labels.Layout(null, Screen);
        Assert.Null(labels.SpreadAt(moon, "Moon"));
        Assert.False(labels.OnSpread(laid[0].Min + new Vector2(3f, 3f)));
    }

    [Fact]
    public void A_crowded_maps_labels_are_laid_out_with_nothing_allocated_a_frame()
    {
        // 600 labels (a crowded map zoomed out: paths' and collisions' marks), in piles of three,
        // the pointer on one pile: laid out frame after frame as the map does, past its first two
        // (which grow the working arrays to the count, and what the spread pile holds), with
        // nothing allocated.
        var labels = new MapLabels();
        void Gather()
        {
            labels.Clear();
            for (var i = 0; i < 600; i++)
                labels.Add(At(20f + (i / 3) % 40 * 30f, 20f + (i / 120) * 40f + (i % 3) * 3f), "label", (255, 255, 255), 1f, 0.8f);
        }
        Gather();
        var first = labels.Layout(new Vector2(25f, 25f), Screen).ToArray();
        Assert.Contains(first, l => l.Line);
        Gather();
        Assert.Equal(first, labels.Layout(new Vector2(25f, 25f), Screen).ToArray());
        Gather();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var again = labels.Layout(new Vector2(25f, 25f), Screen);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(first, again.ToArray());
    }

    [Fact]
    public void A_click_on_a_spread_label_is_on_it_over_anything_under_it()
    {
        // The moon's marker, its label spread up the column, and a vessel's marker whose dot lies
        // under the column, first in the markers' order; Pe's label spread too (the path's).
        var moonLabel = (Min: new Vector2(400f, 180f), Max: new Vector2(480f, 194f));
        var peLabel = (Min: new Vector2(400f, 196f), Max: new Vector2(470f, 210f));
        MapLabels.Clickable[] markers =
        [
            new(new Vector2(420f, 187f), 7f, new Vector2(434f, 180f), new Vector2(500f, 194f), Spread: false), // the vessel's, its dot under the moon's label
            new(new Vector2(360f, 205f), 7f, moonLabel.Min, moonLabel.Max, Spread: true),                     // the moon's
        ];
        (Vector2, Vector2)[] spread = [moonLabel, peLabel];

        // On the moon's spread label, over the vessel's dot and label: the moon's, on the pile.
        Assert.Equal((1, true), MapLabels.Hit(markers, spread, new Vector2(422f, 187f)));
        // On Pe's spread label: the pile's, on no marker (so the target isn't let go).
        Assert.Equal((-1, true), MapLabels.Hit(markers, spread, new Vector2(410f, 200f)));
        // On the moon's dot, off the column: the moon's, not on the pile.
        Assert.Equal((1, false), MapLabels.Hit(markers, spread, new Vector2(362f, 207f)));
        // On the vessel's label past the column's end: the vessel's.
        Assert.Equal((0, false), MapLabels.Hit(markers, spread, new Vector2(490f, 187f)));
        // Off everything: none, off the pile (a click there may let the target go).
        Assert.Equal((-1, false), MapLabels.Hit(markers, spread, new Vector2(900f, 600f)));

        // Nothing spread: the first marker whose dot or label holds the point, in their order.
        markers[1] = markers[1] with { LabelMin = new Vector2(367f, 197f), LabelMax = new Vector2(447f, 211f), Spread = false };
        Assert.Equal((0, false), MapLabels.Hit(markers, [], new Vector2(422f, 187f)));
        Assert.Equal((1, false), MapLabels.Hit(markers, [], new Vector2(440f, 204f)));
    }

    private static bool Overlaps(Vector2 aMin, Vector2 aSize, Vector2 bMin, Vector2 bSize) =>
        aMin.X < bMin.X + bSize.X && bMin.X < aMin.X + aSize.X && aMin.Y < bMin.Y + bSize.Y && bMin.Y < aMin.Y + aSize.Y;

    private static bool Within(Vector2 p, MapLabels.Placed label) =>
        p.X >= label.Min.X - MapLabels.HoverReach && p.X <= label.Max.X + MapLabels.HoverReach
        && p.Y >= label.Min.Y - MapLabels.HoverReach && p.Y <= label.Max.Y + MapLabels.HoverReach;

    [Fact]
    public void The_lens_flare_is_dimmed_in_the_map_not_hidden()
    {
        Assert.Equal(1f, VulkanRenderer.MapFlare(0f));
        Assert.Equal(Scene.MapFlareShare, VulkanRenderer.MapFlare(1f), 5);
        Assert.InRange(Scene.MapFlareShare, 0.05f, 0.5f);
        var camera = new ChaseCamera(null);
        camera.Update(1f / 60, new Vector3(0f, 4000f, 0f), null, GroundBody.Planet, 0);
        Assert.Equal(0f, Paths.MapFade(camera)); // by the vessel: the flare in full
        camera.ToggleZoomOut(Scene.PlanetRadius + 4000f);
        for (var i = 0; i < 120; i++)
            camera.Update(1f / 60, new Vector3(0f, 4000f, 0f), null, GroundBody.Planet, 0);
        Assert.Equal(1f, Paths.MapFade(camera)); // zoomed out: dimmed to its share
    }
}
