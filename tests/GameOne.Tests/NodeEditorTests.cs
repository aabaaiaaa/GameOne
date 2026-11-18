using Xunit;

namespace GameOne.Tests;

/// <summary>The node editor's pure helpers: how fast a handle adds or takes away with the pointer's place, when its handles are shown, and the plan's colours.</summary>
public sealed class NodeEditorTests
{
    private const float Rest = NodeEditor.HandleRest, Pull = NodeEditor.HandlePull, Fastest = NodeEditor.FastestRate;

    [Fact]
    public void A_handle_pulled_out_adds_as_before()
    {
        Assert.Equal(0.3f, NodeEditor.Rate(0f), 1e-4f);
        Assert.Equal(0.3f + Fastest * 0.25f, NodeEditor.Rate(Pull / 2f), 1e-3f);
        Assert.Equal(0.3f + Fastest, NodeEditor.Rate(Pull), 1e-3f);
        Assert.Equal(NodeEditor.Rate(Pull), NodeEditor.Rate(Pull * 7f), 1e-4f);
    }

    [Fact]
    public void A_handle_pushed_in_takes_away_at_a_quarter_of_the_rate_and_goes_past_zero()
    {
        Assert.Equal(0.25f, Scene.NodeHandleInwardShare);
        foreach (var distance in new[] { 10f, Pull / 2f, Pull, Rest })
        {
            Assert.True(NodeEditor.Rate(-distance) < 0f);
            Assert.Equal(-0.25f * NodeEditor.Rate(Math.Min(distance, Pull)), NodeEditor.Rate(-distance), 1e-4f);
        }
    }

    [Fact]
    public void Handles_are_shown_only_while_the_editor_shows_and_the_selected_node_is_on_the_screen()
    {
        bool[] shown = [true, false, true];
        Assert.True(NodeEditor.ShowsHandles(true, 2, shown));
        Assert.False(NodeEditor.ShowsHandles(false, 2, shown));
        Assert.False(NodeEditor.ShowsHandles(true, 1, shown));
        Assert.False(NodeEditor.ShowsHandles(true, -1, shown));
        Assert.False(NodeEditor.ShowsHandles(true, 3, shown));
    }

    [Fact]
    public void A_chain_changed_by_the_flight_not_the_editor_is_told_apart()
    {
        // A grab is let go when the vessel's chain changes from outside the editor (a node gone as
        // it matched, say), so the edit held isn't sent back over it, bringing the node back.
        var two = new ManoeuvreChain(default, [new ManoeuvreNode(0, Prograde: 5f), new ManoeuvreNode(100, Prograde: 2f)]);
        var edited = two.WithNode(1, new ManoeuvreNode(100, Prograde: 3f));
        var gone = new ManoeuvreChain(default, [new ManoeuvreNode(100, Prograde: 2f)]);
        Assert.False(NodeEditor.ChangedElsewhere(two, known: two, sent: null), "as it was");
        Assert.False(NodeEditor.ChangedElsewhere(edited, known: two, sent: edited), "the editor's own change, taken");
        Assert.False(NodeEditor.ChangedElsewhere(null, known: two, sent: ManoeuvreChain.Empty), "the editor's own clearing");
        Assert.True(NodeEditor.ChangedElsewhere(gone, known: two, sent: edited), "a node gone meanwhile");
        Assert.True(NodeEditor.ChangedElsewhere(null, known: two, sent: null), "the last node gone");
    }

    [Fact]
    public void Only_the_next_node_and_the_legs_it_plans_are_green_once_matched()
    {
        var green = Scene.NodeMatchedColor;
        Assert.Equal(green, Paths.NodeColorOf(true, 0));
        Assert.Equal(Paths.PlanColor, Paths.NodeColorOf(true, 1));
        Assert.Equal(Paths.PlanColor, Paths.NodeColorOf(false, 0));
        // The first node's leg is 2, the second's 4: legs 3 and 4 are what the first node plans.
        Assert.Equal(Paths.PlanColor, Paths.LegColorOf(true, 2, 2, 4));
        Assert.Equal(green, Paths.LegColorOf(true, 3, 2, 4));
        Assert.Equal(green, Paths.LegColorOf(true, 4, 2, 4));
        Assert.Equal(Paths.PlanColor, Paths.LegColorOf(true, 5, 2, 4));
        Assert.Equal(green, Paths.LegColorOf(true, 9, 2, int.MaxValue));
        Assert.Equal(Paths.PlanColor, Paths.LegColorOf(false, 3, 2, 4));
    }
}
