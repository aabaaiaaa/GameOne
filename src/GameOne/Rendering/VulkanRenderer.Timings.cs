using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace GameOne.Rendering;

// The GPU's time, for the timings panel (see Timings): six timestamps a frame, round the shadow
// pass, the scene, the effects, the small views and the HUD, read back once the frame's fence
// says the GPU is done with them, as the lens flare's occlusion queries are (VulkanRenderer.Flare.cs).
// Only while Timings.Enabled: off, nothing is written or read.
internal sealed unsafe partial class VulkanRenderer
{
    /// <summary>Timestamps a frame: the start, then the end of each <see cref="GpuPass"/>.</summary>
    private const int TimestampsPerFrame = 6;

    /// <summary>Empty where the queue writes no timestamps (its family's <c>TimestampValidBits</c> is 0).</summary>
    private VkQueryPool _timestampPool;
    private bool _timestamps;
    private int _timestampValidBits;
    private float _timestampPeriod;
    // Whether a slot's timestamps were written last time it was recorded: a query never written
    // (timings off then, or a frame that wasn't recorded) is never read, so no stale time is shown.
    private readonly bool[] _timestampsPending = new bool[MaxFramesInFlight];

    /// <param name="validBits">The queue family's <c>TimestampValidBits</c>: 0 if it writes none.</param>
    /// <param name="periodNs">The device's <c>TimestampPeriod</c>: nanoseconds a tick.</param>
    private void CreateTimestampResources(int validBits, float periodNs)
    {
        _timestampValidBits = validBits;
        _timestampPeriod = periodNs;
        _timestamps = validBits != 0;
        if (!_timestamps)
            return;
        _timestampPool = _device.CreateQueryPool(
            new VkQueryPoolCreateInfo { QueryType = VkQueryType.Timestamp, QueryCount = TimestampsPerFrame * MaxFramesInFlight },
            null
        );
    }

    /// <summary>Whether this frame is timed on the GPU: only with timings on, on a queue that has timestamps.</summary>
    private bool TimingGpu => _timestamps && Timings.Enabled;

    /// <summary>Resets this slot's timestamps, outside a render pass, before they are written.</summary>
    private void ResetTimestamps(CommandBuffer cb)
    {
        if (!TimingGpu)
            return;
        cb.ResetQueryPool(_timestampPool, TimestampsPerFrame * _frame, TimestampsPerFrame);
        _timestampsPending[_frame] = true;
    }

    /// <summary>
    /// Writes this slot's timestamp <paramref name="index"/>: 0 at the top of the pipe as the
    /// frame starts, then each pass's end once all the GPU's work before it is done. Only in a
    /// frame whose timestamps were reset (<see cref="ResetTimestamps"/>), so all six or none are.
    /// </summary>
    private void Timestamp(CommandBuffer cb, int index)
    {
        if (!_timestampsPending[_frame])
            return;
        cb.WriteTimestamp(index == 0 ? VkPipelineStageFlags.TopOfPipeBit : VkPipelineStageFlags.BottomOfPipeBit,
            _timestampPool, TimestampsPerFrame * _frame + index);
    }

    /// <summary>
    /// Reads this slot's timestamps from when it was last recorded, into the frame being timed.
    /// Call after its fence wait: the GPU has finished them, so reading can't stall.
    /// </summary>
    private void ReadTimestamps()
    {
        if (!_timestampsPending[_frame])
            return;
        _timestampsPending[_frame] = false;
        if (!Timings.Enabled)
            return; // turned off since: nothing to give them to

        Span<ulong> ticks = stackalloc ulong[TimestampsPerFrame];
        var result = _device.GetQueryPoolResults(
            _timestampPool, TimestampsPerFrame * _frame, TimestampsPerFrame, MemoryMarshal.AsBytes(ticks), ByteSize.Of(sizeof(ulong)), VkQueryResultFlags._64Bit);
        if (result != VkResult.Success)
            return;

        Span<double> passes = stackalloc double[TimestampsPerFrame - 1];
        for (var i = 0; i < passes.Length; i++)
            passes[i] = TimestampMs(ticks[i], ticks[i + 1], _timestampPeriod, _timestampValidBits);
        Timings.Gpu(passes);
    }

    /// <summary>
    /// The milliseconds between two timestamps <paramref name="periodNs"/> nanoseconds a tick,
    /// of which only the low <paramref name="validBits"/> count: the counter wraps there, so the
    /// gap is taken modulo that.
    /// </summary>
    internal static double TimestampMs(ulong from, ulong to, float periodNs, int validBits)
    {
        var mask = validBits >= 64 ? ulong.MaxValue : (1UL << validBits) - 1;
        var ticks = (to - from) & mask;
        return ticks * (double)periodNs / 1e6;
    }

    private void DestroyTimestampResources()
    {
        if (_timestamps)
            _device.DestroyQueryPool(_timestampPool, null);
    }
}
