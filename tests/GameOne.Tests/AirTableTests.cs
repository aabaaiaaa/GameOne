using System.Numerics;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The air's tables (<see cref="AirTables"/>, what the shaders read for the sun's light and the sky's):
/// the lookup the shaders make, repeated in C#, stays within 3% (or 0.002) of <see cref="Air"/>, the
/// reference, across heights and sun angles, and the tables are the size the GPU texture is.
/// </summary>
public sealed class AirTableTests
{
    private static readonly Half[] Table = AirTables.Build();

    private static Vector3 Model(int layer, float aboveSea, float sunUp)
    {
        if (layer == 1)
            return Air.SkyLight(aboveSea, sunUp);
        var up = Planet.Up(Vector3.Zero);
        var sun = up * sunUp + Planet.East(Vector3.Zero) * MathF.Sqrt(MathF.Max(1f - sunUp * sunUp, 0f));
        return Air.Transmittance(Planet.Centre + up * (Planet.SeaRadius + aboveSea), sun);
    }

    private static void Match(int layer, float aboveSea, float sunUp, ref float worst)
    {
        var want = Model(layer, aboveSea, sunUp);
        var got = AirTables.Look(layer, aboveSea, sunUp);
        foreach (var (g, w) in new[] { (got.X, want.X), (got.Y, want.Y), (got.Z, want.Z) })
        {
            var error = MathF.Abs(g - w);
            worst = MathF.Max(worst, error / MathF.Max(MathF.Abs(w) * 0.03f, 0.002f));
            Assert.True(error <= MathF.Max(MathF.Abs(w) * 0.03f, 0.002f), $"layer {layer} at {aboveSea} m, sun {sunUp}: table {got}, model {want}");
        }
    }

    [Fact]
    public void The_tables_match_the_model_across_their_range()
    {
        float[] heights = [0, 10, 700, 2999, 3000];
        float[] suns = [-1, -0.1f, 0, 0.02f, 0.5f, 1];
        var worst = 0f; // the biggest error, as a part of what is allowed (Match asserts each is within it)
        foreach (var layer in new[] { 0, 1 })
        {
            // Near the sea, with the sun between the height's horizon and the level: where the low columns do the work.
            foreach (var h in new[] { 2f, 10f, 29f, 60f })
                foreach (var part in new[] { 0.1f, 0.5f, 0.9f })
                    Match(layer, h, AirTables.Horizon(h) * part, ref worst);
            // Above the air: the sun's ray is seen through the air only round its closest approach, as from the top row.
            foreach (var h in new[] { 4000f, 6000f, 10000f })
                foreach (var part in new[] { 0.1f, 0.5f, 0.9f })
                    Match(layer, h, AirTables.Horizon(h) * part, ref worst);
            foreach (var h in heights)
                foreach (var s in suns)
                    Match(layer, h, s, ref worst);
            var random = new Random(7);
            for (var i = 0; i < 500; i++)
                Match(layer, random.NextSingle() * Scene.AtmosphereHeight, random.NextSingle() * 2 - 1, ref worst);
        }
    }

    [Fact]
    public void The_tables_are_built_in_half_floats_of_the_right_size()
    {
        Assert.Equal(2 * AirTables.Heights * AirTables.SunAngles * 4, Table.Length);
        Assert.All(Table, t => Assert.True(Half.IsFinite(t)));
    }
}
