using Brutal.JoltApi;

namespace GameOne.Physics;

/// <summary>
/// What every physics world shares, set up once for the process and kept for its life: Jolt's own
/// process-wide state (its allocator, factory and registered types), the job system and temporary
/// memory its steps run on, and the one tile worker (<see cref="Tiles"/>) making every world's
/// ground ahead of need. So any number of worlds (a body's each: <see cref="RocketPhysics"/>) can be
/// made, stepped and disposed in any order. Each world is stepped on the main thread, one at a
/// time, which is what lets them share the job system and the temporary memory.
/// </summary>
internal static class JoltShared
{
    // The temporary memory a step takes room from: for its body pairs and contact constraints
    // (see RocketPhysics.MaxBodies), which Jolt fails outright on (it ends the process) if this is
    // too small. 8,192 of each fit easily in 32 MB.
    private const int TempMegabytes = 32;

    private static readonly object Gate = new();
    private static bool _made;
    private static TempAllocatorImpl? _tempAllocator;
    private static JobSystemThreadPool? _jobSystem;
    private static TileWorker? _tiles;

    /// <summary>Sets Jolt up for the process, the first time it is called; after that it does nothing.</summary>
    public static void Ensure()
    {
        lock (Gate)
        {
            if (_made)
                return;
            Jolt.RegisterDefaultAllocator();
            Factory.Instance = Factory.Create();
            Jolt.RegisterTypes();
            _tempAllocator = TempAllocatorImpl.Create(TempMegabytes * 1024 * 1024);
            _jobSystem = JobSystemThreadPool.Create(2048, 8, Math.Max(1, Environment.ProcessorCount - 1));
            _tiles = new TileWorker();
            _made = true;
        }
    }

    /// <summary>The job system every world's step runs on.</summary>
    public static JobSystemThreadPool JobSystem
    {
        get
        {
            Ensure();
            return _jobSystem!.Value;
        }
    }

    /// <summary>The temporary memory every world's step takes its room from.</summary>
    public static TempAllocatorImpl TempAllocator
    {
        get
        {
            Ensure();
            return _tempAllocator!.Value;
        }
    }

    /// <summary>The one tile worker, every world's (each opens its own jobs on it: <see cref="TileWorker.Open"/>).</summary>
    public static TileWorker Tiles
    {
        get
        {
            Ensure();
            return _tiles!;
        }
    }
}
