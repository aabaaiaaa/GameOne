using Xunit;

// Jolt's allocator, factory and type registry are process-wide, and each Simulation sets them up
// and tears them down, so only one can run at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
