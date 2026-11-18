using System.Text;
using Xunit;

namespace GameOne.Tests;

/// <summary>The HUD's open controls list (<see cref="Hud.ControlLine"/>): every line short enough to fit a small window.</summary>
public sealed class ControlsListTests
{
    /// <summary>
    /// The longest a line may be, in characters: the list is placed by its widest line, at its
    /// smallest text in a small window (800x600), beside the instruments; the longest line it has
    /// long had (the hold buttons') fits there.
    /// </summary>
    private const int Longest = 88;

    [Fact]
    public void Every_line_of_the_controls_list_fits_a_small_window()
    {
        for (var i = 0; i < Hud.ControlLineCount; i++)
        {
            var line = Encoding.UTF8.GetString(Hud.ControlLine(i));
            Assert.True(line.Length <= Longest, $"line {i} is {line.Length} characters: \"{line}\"");
        }
    }
}
