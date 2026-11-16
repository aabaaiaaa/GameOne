using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The menu screens' lists (the mission menu's missions, the world list's saved worlds): a long one
/// scrolls in a region that keeps the whole window on the screen, a short one doesn't, at every
/// window height the HUD supports (<see cref="Hud.ListRegion"/>, <see cref="Hud.MenuMiddle"/>).
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
}
