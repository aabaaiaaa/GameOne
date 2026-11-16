using Brutal.JoltApi;
using GameOne.Physics;
using Xunit;

namespace GameOne.Tests;

/// <summary>
/// The worker that makes the ground's tiles off the step (<see cref="TileWorker"/>), through a
/// world's jobs on it (<see cref="TileWorker.Jobs"/>): soonest wanted first, of every world's; a cell
/// no longer wanted dropped; each world's shapes its own; an exception it hits rethrown on the main
/// thread; and a world's jobs closed cleanly mid-tile. Each test hands it a fake <c>make</c> that records the order it was
/// asked in, and can hold a tile on a gate the test opens, so nothing here waits on timing.
/// </summary>
public sealed class TileWorkerTests
{
    /// <summary>How long any wait on the worker may take before the test fails: far longer than any of them needs.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static TerrainTiles.Cell C(int i) => new(0, i, 0);

    /// <summary>One world's jobs on <paramref name="on"/> (a worker of the test's own, stopped as it ends), made by <paramref name="fake"/>.</summary>
    private static TileWorker.Jobs Jobs(Fake fake, TileWorker on) => on.Open((cell, _) => fake.Make(cell));

    /// <summary>
    /// A fake <c>make</c>: each cell's shape a small sphere (a real Jolt shape, so the worker's
    /// disposing is real), of which it keeps a reference of its own to count what is left undisposed.
    /// </summary>
    private sealed class Fake : IDisposable
    {
        private readonly object _lock = new();
        private readonly List<TerrainTiles.Cell> _made = [];
        private readonly List<Shape> _shapes = [];

        /// <summary>Open, every tile goes straight through; closed, each waits on it.</summary>
        public ManualResetEventSlim Gate { get; } = new(true);

        /// <summary>The cell it throws for, if any.</summary>
        public TerrainTiles.Cell? Throws { get; init; }

        /// <summary>The cells it was asked for, in order (each as it started).</summary>
        public TerrainTiles.Cell[] Made
        {
            get
            {
                lock (_lock)
                    return [.. _made];
            }
        }

        public (Shape Shape, float Centre) Make(TerrainTiles.Cell cell)
        {
            lock (_lock)
            {
                _made.Add(cell);
                Monitor.PulseAll(_lock);
            }
            if (!Gate.Wait(Patience))
                throw new TimeoutException("the test never opened the gate");
            if (cell == Throws)
                throw new ArgumentException($"no tile for {cell}");
            using var settings = SphereShapeSettings.Create(0.5f);
            var shape = ((ShapeSettings)settings).CreateShape();
            shape.AddRef(); // ours, to count by
            lock (_lock)
                _shapes.Add(shape);
            return (shape, cell.I);
        }

        /// <summary>Waits (bounded) until it has been asked for <paramref name="count"/> cells.</summary>
        public void WaitForMade(int count)
        {
            var end = DateTime.UtcNow + Patience;
            lock (_lock)
                while (_made.Count < count)
                    Assert.True(Monitor.Wait(_lock, Clamp(end - DateTime.UtcNow)), $"only {_made.Count} of {count} cells asked for");
        }

        private static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

        /// <summary>How many shapes it has made.</summary>
        public int Shapes
        {
            get
            {
                lock (_lock)
                    return _shapes.Count;
            }
        }

        /// <summary>How many of its shapes someone besides it still holds a reference to.</summary>
        public int Undisposed()
        {
            lock (_lock)
                return _shapes.Count(s => s.GetRefCount() > 1);
        }

        public void Dispose()
        {
            lock (_lock)
                foreach (var shape in _shapes)
                    shape.Dispose();
            Gate.Dispose();
        }
    }

    /// <summary>
    /// Hands <paramref name="wants"/> over as one round, trying again while the worker holds its
    /// lock (the main thread never waits for it: it skips the hand-over), bounded.
    /// </summary>
    private static void Hand(TileWorker.Jobs worker, params (TerrainTiles.Cell, double)[] wants) => HandAt(worker, 0, wants);

    /// <summary><see cref="Hand"/>, its world's clock at <paramref name="now"/> (its dues on that clock).</summary>
    private static void HandAt(TileWorker.Jobs worker, double now, params (TerrainTiles.Cell, double)[] wants) =>
        Assert.True(Waits.Until(() => worker.TryWant([.. wants], now), Patience), "the worker held its lock all along");

    /// <summary>Takes finished shapes (disposing each) until <paramref name="count"/> have come, bounded.</summary>
    private static List<TerrainTiles.Cell> Take(TileWorker.Jobs worker, int count)
    {
        var taken = new List<TerrainTiles.Cell>();
        var into = new List<(TerrainTiles.Cell Cell, Shape Shape, float Centre)>();
        var end = DateTime.UtcNow + Patience;
        for (var tries = 0; taken.Count < count; tries++)
        {
            Assert.True(DateTime.UtcNow < end, $"only {taken.Count} of {count} shapes came");
            into.Clear();
            worker.TryTake(into);
            foreach (var (cell, shape, _) in into)
            {
                shape.Dispose();
                taken.Add(cell);
            }
            if (into.Count == 0)
                Waits.Pause(tries);
        }
        return taken;
    }

    /// <summary>Takes whatever is finished (disposing each), once: throws what the worker threw.</summary>
    private static void TakeAny(TileWorker.Jobs worker)
    {
        var into = new List<(TerrainTiles.Cell Cell, Shape Shape, float Centre)>();
        worker.TryTake(into);
        foreach (var (_, shape, _) in into)
            shape.Dispose();
    }

    [Fact]
    public void It_makes_the_soonest_wanted_cell_first()
    {
        using var physics = new RocketPhysics(GroundBody.Planet); // Jolt's types registered
        using var fake = new Fake();
        using var tiles = new TileWorker();
        using (var worker = Jobs(fake, tiles))
        {
            fake.Gate.Reset();
            Hand(worker, (C(0), 0));
            fake.WaitForMade(1); // held on the gate: the rest wait their turn
            Hand(worker, (C(1), 3), (C(2), 1), (C(3), 2));
            fake.Gate.Set();
            Take(worker, 4);
            Assert.Equal([C(0), C(2), C(3), C(1)], fake.Made);
        }
        Assert.Equal(0, fake.Undisposed());
    }

    [Fact]
    public void A_cell_no_longer_wanted_is_dropped()
    {
        using var physics = new RocketPhysics(GroundBody.Planet);
        using var fake = new Fake();
        using var tiles = new TileWorker();
        using (var worker = Jobs(fake, tiles))
        {
            fake.Gate.Reset();
            Hand(worker, (C(0), 0));
            fake.WaitForMade(1);
            // c1 sooner than c2: had it been kept, it would be made before c2.
            Hand(worker, (C(1), 1), (C(2), 2));
            Hand(worker, (C(2), 2));
            fake.Gate.Set();
            Take(worker, 2);
            Assert.Equal([C(0), C(2)], fake.Made);
        }
        Assert.Equal(0, fake.Undisposed());
    }

    [Fact]
    public void A_cell_in_hand_or_finished_is_never_made_twice()
    {
        // Each update wants every cell not yet laid, the one the worker has in hand and those it
        // has finished but the step hasn't taken among them: each must still be made once.
        using var physics = new RocketPhysics(GroundBody.Planet);
        using var fake = new Fake();
        using var tiles = new TileWorker();
        using (var worker = Jobs(fake, tiles))
        {
            fake.Gate.Reset();
            Hand(worker, (C(0), 0));
            fake.WaitForMade(1); // c0 in hand, held on the gate
            Hand(worker, (C(0), 0), (C(1), 1));
            fake.Gate.Set();
            Assert.True(Waits.Until(() => worker.Finished == 2 && !worker.Busy, Patience), "c0 and c1 were never finished");
            Hand(worker, (C(0), 0), (C(1), 1)); // both finished, not yet taken
            Assert.False(worker.Busy, "a finished cell was wanted again");
            Assert.Equal([C(0), C(1)], Take(worker, 2));
            Assert.Equal([C(0), C(1)], fake.Made);
        }
        Assert.Equal(0, fake.Undisposed());
    }

    [Fact]
    public void A_worker_never_asked_for_a_cell_disposes_at_once()
    {
        using var fake = new Fake();
        using var tiles = new TileWorker();
        var worker = Jobs(fake, tiles);
        Assert.False(worker.Busy);
        worker.Dispose();
        Assert.True(worker.TryWant([(C(0), 0)], 0)); // after it, nothing (and never refused)
        Assert.Empty(fake.Made);
    }

    [Fact]
    public void An_exception_on_the_worker_is_rethrown_on_the_main_thread()
    {
        using var physics = new RocketPhysics(GroundBody.Planet);
        using var fake = new Fake { Throws = C(1) };
        using var tiles = new TileWorker();
        using (var worker = Jobs(fake, tiles))
        {
            Hand(worker, (C(1), 0));
            var end = DateTime.UtcNow + Patience;
            InvalidOperationException? thrown = null;
            for (var tries = 0; thrown is null; tries++)
            {
                Assert.True(DateTime.UtcNow < end, "the worker's exception never came");
                try
                {
                    TakeAny(worker);
                    Waits.Pause(tries);
                }
                catch (InvalidOperationException e)
                {
                    thrown = e;
                }
            }
            Assert.IsType<ArgumentException>(thrown.InnerException);
            Assert.Contains("no tile for", thrown.InnerException!.Message);
            // It makes no more after one.
            Hand(worker, (C(2), 0));
            Assert.Throws<InvalidOperationException>(() => TakeAny(worker));
        }
        Assert.DoesNotContain(C(2), fake.Made);
    }

    [Fact]
    public void Two_worlds_on_one_worker_are_made_soonest_first_and_each_takes_its_own()
    {
        using var physics = new RocketPhysics(GroundBody.Planet);
        using var fake = new Fake();
        using var tiles = new TileWorker();
        using var a = Jobs(fake, tiles);
        using var b = Jobs(fake, tiles);
        fake.Gate.Reset();
        Hand(a, (C(0), 0));
        fake.WaitForMade(1); // held: the rest wait their turn, across both worlds
        Hand(a, (C(1), 3), (C(2), 1));
        Hand(b, (C(5), 2), (C(6), 0.5));
        fake.Gate.Set();
        Assert.Equal([C(0), C(2), C(1)], Take(a, 3));
        Assert.Equal([C(6), C(5)], Take(b, 2));
        Assert.Equal([C(0), C(6), C(2), C(5), C(1)], fake.Made);
    }

    [Fact]
    public void Worlds_on_clocks_far_apart_are_made_by_the_time_each_cell_has_to_go()
    {
        // A world made long after another has a clock far behind it: ranked by their dues as they
        // stand, the newer's would always come first. By time to go: 1 s, 3 s, 5 s.
        using var physics = new RocketPhysics(GroundBody.Planet);
        using var fake = new Fake();
        using var tiles = new TileWorker();
        using var old = Jobs(fake, tiles);
        using var young = Jobs(fake, tiles);
        fake.Gate.Reset();
        HandAt(old, 1000, (C(0), 1000));
        fake.WaitForMade(1); // held: the rest wait their turn
        HandAt(old, 1000, (C(1), 1003));
        HandAt(young, 0, (C(5), 1), (C(6), 5));
        fake.Gate.Set();
        Assert.Equal([C(0), C(1)], Take(old, 2));
        Assert.Equal([C(5), C(6)], Take(young, 2));
        Assert.Equal([C(0), C(5), C(1), C(6)], fake.Made);
    }

    [Fact]
    public void Stopping_the_worker_waits_for_its_tile_and_disposes_what_is_left()
    {
        using var physics = new RocketPhysics(GroundBody.Planet);
        using var fake = new Fake();
        var tiles = new TileWorker();
        var worker = Jobs(fake, tiles);
        Hand(worker, (C(1), 0));
        Assert.True(Waits.Until(() => worker.Finished == 1, Patience), "c1 was never finished");
        fake.Gate.Reset();
        Hand(worker, (C(2), 0));
        fake.WaitForMade(2);
        var stopping = new Thread(tiles.Dispose);
        stopping.Start();
        fake.Gate.Set();
        Assert.True(stopping.Join(TimeSpan.FromSeconds(2)), "stopping didn't return within 2 s of the tile finishing");
        Assert.Equal(0, fake.Undisposed()); // the one in hand finished and, with the one never taken, disposed
        Assert.False(worker.Busy);
        Hand(worker, (C(3), 0)); // stopped: nothing more is made
        worker.Dispose();
        tiles.Dispose(); // again: nothing
        Assert.Equal([C(1), C(2)], fake.Made);
    }

    [Fact]
    public void Closing_a_worlds_jobs_mid_tile_waits_for_it()
    {
        using var physics = new RocketPhysics(GroundBody.Planet);
        using var fake = new Fake();
        using var tiles = new TileWorker();
        var worker = Jobs(fake, tiles);
        // One finished and never taken, and one held mid-tile on the gate as it is disposed.
        Hand(worker, (C(1), 0));
        Assert.True(Waits.Until(() => worker.Finished == 1, Patience), "c1 was never finished");
        fake.Gate.Reset();
        Hand(worker, (C(2), 0));
        fake.WaitForMade(2);
        var disposing = new Thread(worker.Dispose);
        disposing.Start();
        fake.Gate.Set();
        Assert.True(disposing.Join(TimeSpan.FromSeconds(2)), "closing didn't return within 2 s of the tile finishing");
        Assert.Equal([C(1), C(2)], fake.Made);
        Assert.Equal(2, fake.Shapes); // waited for: the tile in hand was finished before it returned ...
        Assert.Equal(0, fake.Undisposed()); // ... and disposed, as was the one never taken
        worker.Dispose(); // again: nothing
    }
}
