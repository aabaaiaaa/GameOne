using Brutal.ImGuiApi;
using Brutal.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The menu screens' lists (the mission menu's missions, the world list's saved worlds): a long one
/// scrolls in a region that keeps the whole window on the screen, a short one doesn't, at every
/// window height the HUD supports (<see cref="Hud.ListRegion"/>, <see cref="Hud.MenuMiddle"/>);
/// and the carry-on screen and the try-again window, laid out by ImGui, on the screen and clear of
/// the credits at every size the HUD supports.
/// </summary>
public sealed class MenuLayoutTests
{
    [Fact]
    public void A_long_list_scrolls_and_its_menu_stays_on_the_screen()
    {
        const float above = 150f, below = 160f, row = 60f, margin = 14f;
        foreach (var displayHeight in (ReadOnlySpan<float>)[600f, 720f, 1080f])
            foreach (var worlds in (ReadOnlySpan<int>)[1, 3, 10, 50, 200])
            {
                var content = worlds * row;
                var (height, scrolls) = Hud.ListRegion(content, displayHeight, above, below, row);
                var window = above + height + below;
                var middle = Hud.MenuMiddle(displayHeight, window);
                var (top, bottom) = (middle - window / 2f, middle + window / 2f);
                Assert.True(top >= margin - 0.5f && bottom <= displayHeight - margin + 0.5f,
                    $"{worlds} worlds at {displayHeight} px high: the window from {top} to {bottom}");
                Assert.Equal(content > displayHeight - 2f * margin - above - below, scrolls);
                Assert.Equal(scrolls ? displayHeight - 2f * margin - above - below : content, height, 3);
            }
    }

    [Fact]
    public void A_short_menu_keeps_its_place_a_little_above_the_middle()
    {
        Assert.Equal(720f * 0.45f, Hud.MenuMiddle(720f, 400f));
        Assert.Equal(600f * 0.45f, Hud.MenuMiddle(600f, 300f));
        // Too tall for that: as high as keeps its top a margin down, never off the top.
        Assert.Equal(14f + 300f, Hud.MenuMiddle(600f, 600f));
    }

    // The sizes the menu tests lay the screens out at: the smallest the HUD supports to a wide one.
    private static readonly float2[] Displays = [new(800f, 600f), new(1024f, 768f), new(1280f, 720f), new(1920f, 1080f)];

    [Fact]
    public void The_carry_on_screen_keeps_clear_of_the_credits()
    {
        // Laid out by ImGui itself (a context of its own, with the HUD's font and style), with one
        // choice, a few and more than fit, each with long text, under a world's name as long as
        // the rename box takes: on the screen, and its right clear of the credits.
        var name = new string('W', 63);
        var rows = new List<CarryOnRow>();
        for (var i = 0; i < 40; i++)
            rows.Add(new CarryOnRow(i, $"Carry on: DEPLOY A SATELLITE \u00b7 flight {100 + i} \u00b7 3 of 6",
                "Upper stage 123: in orbit, 10234 to 10456 m, at the Planet (last flown) \u00b7 2,000 pts so far, and more words to wrap", $"Carry on: {i}", i % 2 == 0));
        foreach (var display in Displays)
            foreach (var count in (ReadOnlySpan<int>)[1, 4, 40])
            {
                var shown = rows.GetRange(0, count);
                var layout = new MenuLayout();
                var rect = Laid(display, Hud.CarryOnName, font => Hud.CarryOnWindow(display, font, name, "3 missions in progress", shown, layout));
                AssertClear(display, rect, $"The carry-on screen with {count} choices at {display.X}x{display.Y}");
            }
    }

    [Fact]
    public void The_try_again_window_keeps_clear_of_the_credits()
    {
        var rewind = Hud.RewindLabel(99 * 60 + 59.9, resumed: true);
        Assert.Equal("Rewind 99 min 59 s, to when this mission was resumed", rewind);
        Assert.Equal("Rewind 12 min 40 s, to when this mission began", Hud.RewindLabel(12 * 60 + 40.2, resumed: false));
        const string start = "A new run of DEPLOY A SATELLITE, its rocket on Northreach's pad. This run is abandoned: its vessels stay where they are.";
        foreach (var display in Displays)
            foreach (var confirming in (ReadOnlySpan<bool>)[false, true])
            {
                var rect = Laid(display, Hud.TryAgainName, font => Hud.TryAgainWindow(display, font, start, rewind, confirming));
                AssertClear(display, rect, $"The try-again window{(confirming ? ", confirming," : "")} at {display.X}x{display.Y}");
            }
    }

    private static void AssertClear(float2 display, (float Left, float Top, float Right, float Bottom) r, string what)
    {
        var clear = Hud.CreditsLeft(display.X) - Hud.CreditsGap;
        Assert.True(r.Right <= clear + 0.5f, $"{what}: its right {r.Right} over the credits' {clear + Hud.CreditsGap}");
        Assert.True(r.Left >= 0f, $"{what}: its left {r.Left} off the screen");
        Assert.True(r.Top >= -0.5f && r.Bottom <= display.Y + 0.5f, $"{what}: from {r.Top} to {r.Bottom}, off the screen");
    }

    /// <summary>
    /// A window drawn by <paramref name="draw"/> in an ImGui context of its own (the HUD's font and
    /// style, no renderer) on a screen <paramref name="display"/> in size, for a few frames (a
    /// window made to fit what it holds settles in a frame or two), and where it was laid out.
    /// </summary>
    private static (float, float, float, float) Laid(float2 display, ReadOnlySpan<byte> name, Action<ImFontPtr> draw)
    {
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO();
            io.IniFilename = default;
            io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
            io.DisplaySize = display;
            io.DeltaTime = 1f / 60f;
            var font = Hud.AddFont(io.Fonts);
            Hud.ApplyStyle();
            var rect = default((float, float, float, float));
            for (var frame = 0; frame < 6; frame++)
            {
                ImString.SharedStorage.Clear();
                ImGui.NewFrame();
                draw(font);
                ImGui.Begin(name); // (appended to, to read it)
                var (pos, size) = (ImGui.GetWindowPos(), ImGui.GetWindowSize());
                ImGui.End();
                rect = (pos.X, pos.Y, pos.X + size.X, pos.Y + size.Y);
                ImGui.EndFrame();
            }
            return rect;
        }
        finally
        {
            ImGui.DestroyContext(context);
        }
    }
}
