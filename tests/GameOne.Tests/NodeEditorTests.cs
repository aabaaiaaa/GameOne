using Xunit;

namespace GameOne.Tests;

/// <summary>The node editor's pure helpers: how fast a handle adds or takes away with the pointer's place.</summary>
public sealed class NodeEditorTests
{
    [Fact]
    public void A_handle_pulled_out_adds_as_before()
    {
        Assert.Equal(0.3f, NodeEditor.Rate(0f), 1e-4f);
        Assert.Equal(0.3f + 40f * 0.25f, NodeEditor.Rate(35f), 1e-3f);
        Assert.Equal(0.3f + 40f, NodeEditor.Rate(70f), 1e-3f);
        Assert.Equal(NodeEditor.Rate(70f), NodeEditor.Rate(500f), 1e-4f);
    }

    [Fact]
    public void A_handle_pushed_in_takes_away_at_a_quarter_of_the_rate_and_goes_past_zero()
    {
        Assert.Equal(0.25f, Scene.NodeHandleInwardShare);
        foreach (var distance in new[] { 10f, 35f, 70f })
        {
            Assert.True(NodeEditor.Rate(-distance) < 0f);
            Assert.Equal(-0.25f * NodeEditor.Rate(distance), NodeEditor.Rate(-distance), 1e-4f);
        }
    }
}
