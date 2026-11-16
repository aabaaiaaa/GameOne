using Brutal.ImGuiApi;
using Brutal.Numerics;
using GameOne;
using Xunit;

namespace GameOne.Tests;

/// <summary>The BRUTAL credits panel: on the screen before play begins, in every menu, and for the first seconds of play.</summary>
public sealed class CreditsTests
{
    [Fact]
    public void The_credits_show_before_play_and_in_every_menu()
    {
        Assert.True(Hud.ShowsCredits(menuOpen: true, playingFor: null));   // the world list the game starts on
        Assert.True(Hud.ShowsCredits(menuOpen: false, playingFor: null));  // the first frame, before the HUD has seen play
        Assert.True(Hud.ShowsCredits(menuOpen: true, playingFor: 600));    // the game menu, long after
    }

    [Fact]
    public void No_menu_screen_covers_the_credits_at_any_size_the_hud_supports()
    {
        // Each menu screen (the mission menu at its widest and narrowest, with a scrollbar's room),
        // padded as the HUD pads its windows, from the smallest window to a wide one: its right edge
        // clear of the credits by the gap, its left on the screen. At 1280x720 every one is centred,
        // as wide as it asks (the mission menu all but its widest), as the driver's click places assume.
        const float padding = 24f, scrollbar = 22f;
        var screens = new (float Widest, float Narrowest, float Extra)[]
        {
            (Hud.GameMenuWidth, Hud.GameMenuWidth, 0f), (Hud.LogWidth, Hud.LogWidth, 0f), (Hud.SettingsWidth, Hud.SettingsWidth, 0f),
            (Hud.WorldListWidth, Hud.WorldListWidth, 0f), (Hud.MenuWidest, Hud.MenuNarrowest, scrollbar),
            (Hud.CarryOnWidth, Hud.CarryOnWidth, scrollbar), (Hud.TryAgainWidth, Hud.TryAgainWidth, 0f),
        };
        foreach (var displayWidth in (ReadOnlySpan<float>)[800f, 1024f, 1280f, 1920f])
            foreach (var (widest, narrowest, extra) in screens)
            {
                var (width, across) = Hud.MenuAcross(displayWidth, padding, widest, narrowest, extra);
                var (left, right) = (across - (width + padding + extra) / 2f, across + (width + padding + extra) / 2f);
                Assert.True(right <= Hud.CreditsLeft(displayWidth) - Hud.CreditsGap + 0.5f, $"{widest} wide at {displayWidth}: its right {right} over the credits' {Hud.CreditsLeft(displayWidth)}");
                Assert.True(left >= 0f, $"{widest} wide at {displayWidth}: its left {left} off the screen");
                Assert.True(width >= Math.Min(narrowest, 400f), $"{widest} wide at {displayWidth}: squeezed to {width}");
                if (displayWidth == 1280f)
                {
                    Assert.Equal(displayWidth / 2f, across);
                    Assert.True(width >= (narrowest == widest ? widest : 730f), $"{widest} wide at 1280: {width}");
                }
            }
    }

    [Fact]
    public void A_long_world_name_keeps_the_menus_clear_of_the_credits_at_800x600()
    {
        // The menus that show the world's name, laid out by ImGui itself (no window or renderer:
        // a context of its own, with the HUD's font and style) at 800x600, the narrowest the HUD
        // supports, with a name as long as the rename box takes (63 bytes), of the widest letter and
        // in words. The game menu is drawn whole, as the game draws it; the mission menu's world
        // line in a window as wide as its missions' buttons make it. (The world list's rows and the
        // log's lines are in regions of a fixed width, which clip them; the rest of every screen's
        // text is the game's own, wrapped to the screen's width.)
        var display = new float2(800f, 600f);
        var clear = Hud.CreditsLeft(display.X) - Hud.CreditsGap;
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO();
            io.IniFilename = default;
            io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures; // (the atlas is made as the text needs it, and never drawn)
            io.DisplaySize = display;
            io.DeltaTime = 1f / 60f;
            var font = Hud.AddFont(io.Fonts);
            Hud.ApplyStyle();
            foreach (var name in (ReadOnlySpan<string>)[new string('W', 63), "The moon trip: a low orbit, the moon targeted, and more to go"])
            {
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(name) <= 63);
                var (game, missions) = (default(float2), default(float2)); // each window's left and right
                for (var frame = 0; frame < 4; frame++) // (a window made to fit what it holds settles in a frame or two)
                {
                    ImString.SharedStorage.Clear();
                    ImGui.NewFrame();
                    Hud.GameMenuWindow(display, font, name, Mission.All[0].Name);
                    game = WindowAcross(Hud.GameMenuName);

                    var style = ImGui.GetStyle();
                    var scrollbar = style.ScrollbarSize + style.ItemSpacing.X;
                    var (width, across) = Hud.MenuAcross(display.X, 2f * style.WindowPadding.X, Hud.MenuWidest, Hud.MenuNarrowest, scrollbar);
                    ImGui.SetNextWindowPos(new float2(across, 300f), ImGuiCond.Always, new float2(0.5f, 0.5f));
                    ImGui.Begin("##missions"u8, ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings);
                    Hud.WorldLine(name, width);
                    ImGui.Button("Mission"u8, new float2(width + scrollbar, 34f));
                    ImGui.End();
                    missions = WindowAcross("##missions"u8);
                    ImGui.EndFrame();
                }
                foreach (var (screen, (left, right)) in (ReadOnlySpan<(string, float2)>)[("game menu", game), ("mission menu", missions)])
                {
                    Assert.True(right <= clear + 0.5f, $"The {screen} with \"{name}\": its right {right} over the credits' {clear + Hud.CreditsGap}");
                    Assert.True(left >= 0f, $"The {screen} with \"{name}\": its left {left} off the screen");
                }
            }
        }
        finally
        {
            ImGui.DestroyContext(context);
        }
    }

    /// <summary>A window's left and right this frame, as it was laid out (appended to, to read it).</summary>
    private static float2 WindowAcross(ReadOnlySpan<byte> name)
    {
        ImGui.Begin(name);
        var (left, width) = (ImGui.GetWindowPos().X, ImGui.GetWindowSize().X);
        ImGui.End();
        return new float2(left, left + width);
    }

    [Fact]
    public void In_flight_the_credits_go_after_their_seconds()
    {
        Assert.True(Hud.ShowsCredits(menuOpen: false, playingFor: 0));
        Assert.True(Hud.ShowsCredits(menuOpen: false, playingFor: Scene.CreditsSeconds - 0.01));
        Assert.False(Hud.ShowsCredits(menuOpen: false, playingFor: Scene.CreditsSeconds));
        Assert.False(Hud.ShowsCredits(menuOpen: false, playingFor: 600));
    }
}
