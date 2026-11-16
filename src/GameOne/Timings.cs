using System.Diagnostics;
using System.Runtime.CompilerServices;
using GameOne.Physics;

namespace GameOne;

/// <summary>The parts of one physics step (one call of the flight's step) that are timed.</summary>
internal enum StepPart
{
    /// <summary>The flight's rules outside physics: vessels' rules, mission progress, paths, encounters.</summary>
    Rules,
    /// <summary>Jolt's own step (<c>_system.Update</c>).</summary>
    Jolt,
    /// <summary>The ground's tiles: laying and taking them up, waking what they mark.</summary>
    Tiles,
    /// <summary>The water and rafts: floating, cushioning, moving the rafts and the floes.</summary>
    Water,
    /// <summary>The game's contact and nearness work: touches, nearness, grounding, freezing, <c>Vessel.CheckContact</c>.</summary>
    Contact,
    /// <summary>The rest of the physics step: gravity and the turning frame's pulls, thrust, steering.</summary>
    Rest,
}

/// <summary>
/// Pieces of the <see cref="StepPart.Tiles"/>, <see cref="StepPart.Contact"/> and <see cref="StepPart.Rules"/> parts, timed on
/// their own (<see cref="Timings.Start"/> and <see cref="Timings.Stop"/>) to see which of their
/// work is the cost. Each is counted within the part it ran in, as well as on its own: so they
/// don't add to the step, and a part's pieces add up to less than it (what is left is the part's
/// own bookkeeping round them). A tile laid as a craft wakes between steps (by its legs, a
/// jettison) runs in the rules' part, so its pieces are there.
/// </summary>
internal enum StepDetail
{
    /// <summary>Tiles: working out a new tile's heights from the terrain (<c>TerrainTiles.Heights</c>).</summary>
    Samples,
    /// <summary>Tiles: making a tile's Jolt shape and body in the step (one made from the terrain there) and adding it to the world.</summary>
    Bodies,
    /// <summary>Tiles: laying a tile from a shape made already (the worker's, or one kept): its body made and added.</summary>
    Adds,
    /// <summary>Tiles: taking tiles out of the world.</summary>
    Removals,
    /// <summary>Contact: finding which crafts are moving (<c>FindMovers</c>).</summary>
    Movers,
    /// <summary>Contact: marking the crafts near a mover or the player, to wake (<c>MarkNear</c>, <c>MarkNearThePlayer</c>).</summary>
    Near,
    /// <summary>Contact: what each craft stands on (<c>FindGrounded</c>).</summary>
    Grounded,
    /// <summary>Contact: freezing crafts come to rest (<c>FreezeResting</c>).</summary>
    Freezing,
    /// <summary>Contact: clearing wrecks and ending parting (<c>ClearWrecks</c>, <c>EndParting</c>).</summary>
    Wrecks,
    /// <summary>Contact: keeping each craft's motion for the step, and reading Jolt's contacts after it (<c>KeepStepMotion</c>, <c>ReadContacts</c>).</summary>
    Touches,
    /// <summary>Contact: each vessel's own touchdown, crash and hit checks (<c>Vessel.CheckContact</c>).</summary>
    Vessels,
    /// <summary>Rules: predicting a witnessed vessel's soonest fast collision ahead (<c>Collisions.Soonest</c>).</summary>
    Collisions,
    /// <summary>Rules: moving the vessels under formula (<c>Unwatched.Advance</c>).</summary>
    Formula,
    /// <summary>Rules: finding the collisions of the vessels under formula with each other, and judging them (<c>Unwatched.FindCollisions</c>).</summary>
    FormulaCollisions,
}

/// <summary>The parts of one frame that are timed, on the CPU.</summary>
internal enum FramePart
{
    /// <summary>The flight's steps, whole (the sum of their totals).</summary>
    Flight,
    /// <summary>Preparing the frame: its input, the terrain patches, the views, sun areas, light sets.</summary>
    Prepare,
    /// <summary>Building the HUD (ImGui).</summary>
    Hud,
    /// <summary>
    /// Waiting for the GPU or the display (the fence, the swapchain), and reading back what the
    /// GPU left in this slot once the fence is passed (the flare's occlusion queries, the
    /// timestamps): a lap between them would be one more clock read a frame, for very little.
    /// </summary>
    Wait,
    /// <summary>Recording the shadow pass; the GPU's side is <see cref="GpuPass.Shadows"/>.</summary>
    Shadows,
    /// <summary>
    /// Recording the rest of the GPU's work, and setting up the frame's uniforms just before it
    /// (written only once the fence is passed, so after the Wait lap).
    /// </summary>
    Record,
    /// <summary>Submitting it and presenting.</summary>
    Submit,
    /// <summary>Whatever is left of the frame, so the parts add up to the whole.</summary>
    Other,
}

/// <summary>The GPU's passes that are timed (from timestamp queries).</summary>
internal enum GpuPass { Shadows, Scene, Effects, SmallViews, Hud }

[InlineArray(6)]
internal struct StepParts
{
    private double _first;
}

[InlineArray(14)]
internal struct StepDetails
{
    private double _first;
}

[InlineArray(8)]
internal struct FrameParts
{
    private double _first;
}

[InlineArray(5)]
internal struct GpuPasses
{
    private double _first;
}

/// <summary>One physics step's times in milliseconds. Its parts are an inline array, so a sample allocates nothing.</summary>
internal struct StepSample
{
    private StepParts _parts;
    private StepDetails _details;

    public double TotalMs { get; set; }
    public int PhysicsSteps { get; set; }
    public int Tiles { get; set; }

    public double this[StepPart part]
    {
        get => _parts[(int)part];
        set => _parts[(int)part] = value;
    }

    /// <summary>A piece of a part (see <see cref="StepDetail"/>): within the parts' times, not beside them.</summary>
    public double this[StepDetail detail]
    {
        get => _details[(int)detail];
        set => _details[(int)detail] = value;
    }
}

/// <summary>
/// One frame's times in milliseconds: the physics step parts summed over the frame's steps, the
/// frame's own parts, and the GPU's (NaN when there is none, as its results come a frame or two
/// late or not at all). The parts of each kind add up to what they are a part of. A hand-made
/// sample has no GPU times only if it says so (NaN): the recorder does.
/// </summary>
internal struct FrameSample
{
    private StepParts _steps;
    private StepDetails _details;
    private FrameParts _frame;
    private GpuPasses _gpu;

    public double TotalMs { get; set; }
    public int Steps { get; set; }
    public int PhysicsSteps { get; set; }
    public float WarpAsked { get; set; }
    public float WarpReached { get; set; }
    public double GpuMs { get; set; }
    public int Awake { get; set; }
    public int Frozen { get; set; }
    public int Afloat { get; set; }
    public int Tiles { get; set; }
    public int UnderFormula { get; set; }

    public double this[StepPart part]
    {
        get => _steps[(int)part];
        set => _steps[(int)part] = value;
    }

    public double this[StepDetail detail]
    {
        get => _details[(int)detail];
        set => _details[(int)detail] = value;
    }

    public double this[FramePart part]
    {
        get => _frame[(int)part];
        set => _frame[(int)part] = value;
    }

    public double this[GpuPass pass]
    {
        get => _gpu[(int)pass];
        set => _gpu[(int)pass] = value;
    }
}

/// <summary>A one-second average of the frames: what the timings panel shows.</summary>
internal sealed record TimingsSummary
{
    private readonly double[] _step = new double[Enum.GetValues<StepPart>().Length];
    private readonly double[] _detail = new double[Enum.GetValues<StepDetail>().Length];
    private readonly double[] _frame = new double[Enum.GetValues<FramePart>().Length];
    private readonly double[] _gpu = new double[Enum.GetValues<GpuPass>().Length];

    public int Frames { get; init; }
    public double FrameMs { get; init; }
    public double Fps { get; init; }
    public float WarpAsked { get; init; }
    public float WarpReached { get; init; }
    /// <summary>Physics steps per frame.</summary>
    public double PhysicsSteps { get; init; }
    /// <summary>The mean over the frames that had GPU times; NaN if none did.</summary>
    public double GpuMs { get; init; } = double.NaN;
    /// <summary>The last frame's world.</summary>
    public int Awake { get; init; }
    public int Frozen { get; init; }
    public int Afloat { get; init; }
    public int Tiles { get; init; }
    /// <summary>The last frame's vessels out of the physics, moved by formula (see <see cref="Unwatched"/>); those in it are the awake and the frozen.</summary>
    public int UnderFormula { get; init; }

    /// <summary>Mean ms per frame.</summary>
    public double this[StepPart part] => _step[(int)part];
    /// <summary>Mean ms per frame, within its part's (see <see cref="StepDetail"/>).</summary>
    public double this[StepDetail detail] => _detail[(int)detail];
    public double this[FramePart part] => _frame[(int)part];
    /// <summary>The mean over the frames that had GPU times; NaN if none did.</summary>
    public double this[GpuPass pass] => _gpu[(int)pass];

    public static TimingsSummary Of(ReadOnlySpan<FrameSample> frames)
    {
        var steps = new double[Enum.GetValues<StepPart>().Length];
        var details = new double[Enum.GetValues<StepDetail>().Length];
        var parts = new double[Enum.GetValues<FramePart>().Length];
        var passes = new double[Enum.GetValues<GpuPass>().Length];
        var passCounts = new int[passes.Length];
        double totalMs = 0, physicsSteps = 0, gpuMs = 0, warpAsked = 0, warpReached = 0;
        int gpuFrames = 0;
        foreach (var f in frames)
        {
            totalMs += f.TotalMs;
            physicsSteps += f.PhysicsSteps;
            warpAsked += f.WarpAsked;
            warpReached += f.WarpReached;
            for (int i = 0; i < steps.Length; i++)
            {
                steps[i] += f[(StepPart)i];
            }
            for (int i = 0; i < details.Length; i++)
            {
                details[i] += f[(StepDetail)i];
            }
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] += f[(FramePart)i];
            }
            if (!double.IsNaN(f.GpuMs))
            {
                gpuMs += f.GpuMs;
                gpuFrames++;
            }
            for (int i = 0; i < passes.Length; i++)
            {
                double v = f[(GpuPass)i];
                if (!double.IsNaN(v))
                {
                    passes[i] += v;
                    passCounts[i]++;
                }
            }
        }

        int n = frames.Length;
        double mean = n > 0 ? 1.0 / n : 0.0;
        for (int i = 0; i < steps.Length; i++)
        {
            steps[i] *= mean;
        }
        for (int i = 0; i < details.Length; i++)
        {
            details[i] *= mean;
        }
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] *= mean;
        }
        for (int i = 0; i < passes.Length; i++)
        {
            passes[i] = passCounts[i] > 0 ? passes[i] / passCounts[i] : double.NaN;
        }
        double frameMs = totalMs * mean;
        var last = n > 0 ? frames[n - 1] : default;
        var summary = new TimingsSummary
        {
            Frames = n,
            FrameMs = frameMs,
            Fps = frameMs > 0 ? 1000.0 / frameMs : 0.0,
            WarpAsked = (float)(warpAsked * mean),
            WarpReached = (float)(warpReached * mean),
            PhysicsSteps = physicsSteps * mean,
            GpuMs = gpuFrames > 0 ? gpuMs / gpuFrames : double.NaN,
            Awake = last.Awake,
            Frozen = last.Frozen,
            Afloat = last.Afloat,
            Tiles = last.Tiles,
            UnderFormula = last.UnderFormula,
        };
        steps.CopyTo(summary._step, 0);
        details.CopyTo(summary._detail, 0);
        parts.CopyTo(summary._frame, 0);
        passes.CopyTo(summary._gpu, 0);
        return summary;
    }
}

/// <summary>
/// The game's own timings: where each physics step's and each frame's time goes. Where a part runs
/// it laps the clock into this, only while <see cref="Enabled"/>; off, each call is one bool read
/// and returns, with no stopwatch read. The timings panel shows <see cref="Summary"/>, and the
/// performance tests read the raw <see cref="Steps"/> and <see cref="Frames"/>.
/// </summary>
/// <remarks>
/// A step is laid out in <see cref="StepPart"/>s: Rules (the flight's rules outside physics, the
/// rest of the step's time once the laps are taken), Jolt (its own step), Tiles (the ground's
/// tiles), Water (floating, rafts, floes), Contact (the game's contact and nearness work) and Rest
/// (gravity, thrust, steering). A Jolt query added later (a ray cast, say) is charged to its
/// caller's part. A frame is laid out in <see cref="FramePart"/>s: Flight (the steps, whole),
/// Prepare, Hud, Wait, Shadows (recording the shadow pass), Record, Submit and Other (what is left
/// of the frame, so the parts add up to its whole). A frame runs from the last frame's end to its
/// own, so it takes in the steps ended since. The GPU's passes come from timestamp queries a frame
/// or two late and fill the frame being built, which a one-second average doesn't notice.
/// It is static (there is one game), so tests turn it off after themselves.
/// </remarks>
internal static class Timings
{
    public const int StepCapacity = 32768;
    public const int FrameCapacity = 4096;

    // Looked up once: Enum.GetValues allocates, and a step must not.
    private static readonly StepPart[] AllStepParts = Enum.GetValues<StepPart>();
    private static readonly StepDetail[] AllStepDetails = Enum.GetValues<StepDetail>();
    private static readonly FramePart[] AllFrameParts = Enum.GetValues<FramePart>();
    private static readonly GpuPass[] AllGpuPasses = Enum.GetValues<GpuPass>();

    private static bool _enabled;
    // True only while a step (or a frame) is open, and opening one needs Enabled: so a switch
    // between a Begin and its End drops that step, never records half of it.
    private static bool _stepOpen;
    private static bool _frameOpen;

    private static Ring<StepSample>? _steps;
    private static Ring<FrameSample>? _frames;
    private static FrameSample[]? _scratch;

    private static StepSample _step;
    private static long _stepMark;

    // What the frame being built has taken from its steps and laps.
    private static StepSample _stepsInFrame;
    private static FrameSample _frame;
    private static double _flightMs;
    private static int _stepCount, _physicsStepCount;
    private static long _frameMark;
    private static long _lastFrameEnd;

    private static long _summaryMark;
    private static int _framesSinceSummary;

    public static TimingsSummary? Summary { get; private set; }

    public static IReadOnlyList<StepSample> Steps => _steps ?? (IReadOnlyList<StepSample>)Array.Empty<StepSample>();

    public static IReadOnlyList<FrameSample> Frames => _frames ?? (IReadOnlyList<FrameSample>)Array.Empty<FrameSample>();

    /// <summary>The part a piece is of (see <see cref="StepDetail"/>).</summary>
    public static StepPart PartOf(StepDetail detail) => detail switch
    {
        <= StepDetail.Removals => StepPart.Tiles,
        StepDetail.Collisions or StepDetail.Formula or StepDetail.FormulaCollisions => StepPart.Rules,
        _ => StepPart.Contact,
    };

    public static bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled)
            {
                return;
            }
            _enabled = value;
            _stepOpen = false;
            _frameOpen = false;
            if (!value)
            {
                return;
            }

            _steps ??= new Ring<StepSample>(StepCapacity);
            _frames ??= new Ring<FrameSample>(FrameCapacity);
            _scratch ??= new FrameSample[FrameCapacity];
            _steps.Clear();
            _frames.Clear();
            Summary = null;
            ResetFrame();
            long now = Stopwatch.GetTimestamp();
            _lastFrameEnd = now;
            _summaryMark = now;
            _framesSinceSummary = 0;
        }
    }

    public static void BeginStep()
    {
        if (!_enabled)
        {
            return;
        }
        _step = default;
        _stepMark = Stopwatch.GetTimestamp();
        _stepOpen = true;
        _stepThread = Environment.CurrentManagedThreadId;
    }

    /// <summary>The thread the open step runs on: a lap from any other (a body's physics made ahead on the tile worker, rebuilding a pooled world) is no part of it, and touches nothing here.</summary>
    private static int _stepThread;

    /// <summary>Whether a step is open and this is its thread (see <see cref="_stepThread"/>).</summary>
    private static bool OnTheStep => _stepOpen && Environment.CurrentManagedThreadId == _stepThread;

    /// <summary>Charges the time since the last lap (or the step's start) to a part.</summary>
    public static void Lap(StepPart part)
    {
        if (!OnTheStep)
        {
            return;
        }
        long now = Stopwatch.GetTimestamp();
        _step[part] += Ms(now - _stepMark);
        _stepMark = now;
    }

    /// <summary>
    /// The clock, to time a piece of a part from (<see cref="Stop"/>), or 0 when no step is open:
    /// so off, it is one bool read, like a lap. It doesn't lap: the part round it is charged as ever.
    /// </summary>
    public static long Start() => OnTheStep ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Charges the time since <paramref name="started"/> (from <see cref="Start"/>) to <paramref name="detail"/>, within the part it ran in.</summary>
    public static void Stop(StepDetail detail, long started)
    {
        if (!OnTheStep || started == 0)
        {
            return; // off, another thread's, or the step opened (timings turned on) after it started
        }
        _step[detail] += Ms(Stopwatch.GetTimestamp() - started);
    }

    public static void CountPhysicsStep()
    {
        if (!_stepOpen)
        {
            return;
        }
        _step.PhysicsSteps++;
    }

    /// <summary>Ends the step, charging the rest since the last lap to <see cref="StepPart.Rules"/>.</summary>
    public static void EndStep(int tiles)
    {
        if (!_stepOpen)
        {
            return;
        }
        Lap(StepPart.Rules);
        _stepOpen = false;

        double total = 0;
        foreach (var part in AllStepParts)
        {
            total += _step[part];
            _stepsInFrame[part] += _step[part];
        }
        foreach (var detail in AllStepDetails)
        {
            _stepsInFrame[detail] += _step[detail];
        }
        _step.TotalMs = total;
        _step.Tiles = tiles;
        _steps!.Add(_step);

        _flightMs += total;
        _stepCount++;
        _physicsStepCount += _step.PhysicsSteps;
    }

    public static void BeginFrame()
    {
        if (!_enabled)
        {
            return;
        }
        _frameMark = Stopwatch.GetTimestamp();
        _frameOpen = true;
    }

    public static void FrameLap(FramePart part)
    {
        if (!_frameOpen)
        {
            return;
        }
        long now = Stopwatch.GetTimestamp();
        _frame[part] += Ms(now - _frameMark);
        _frameMark = now;
    }

    /// <summary>The GPU's pass times (milliseconds, in <see cref="GpuPass"/> order) for the frame being built.</summary>
    public static void Gpu(ReadOnlySpan<double> passMs)
    {
        if (!_frameOpen)
        {
            return;
        }
        double sum = 0;
        foreach (var pass in AllGpuPasses)
        {
            double v = (int)pass < passMs.Length ? passMs[(int)pass] : 0.0;
            _frame[pass] = v;
            sum += v;
        }
        _frame.GpuMs = sum;
    }

    /// <summary>
    /// Counts the world's vessels and ground tiles for the frame being built: the crafts in every
    /// body's physics, and <paramref name="underFormula"/> out of it, moved by formula (the flight's count,
    /// <see cref="Unwatched.UnderFormula"/>: the physics knows only the crafts it holds).
    /// </summary>
    public static void CountWorld(Worlds worlds, int underFormula)
    {
        if (!_frameOpen)
        {
            return;
        }
        int awake = 0, frozen = 0, afloat = 0;
        // Indexed: a foreach over the IReadOnlyList would box its enumerator, every frame.
        var inUse = worlds.All;
        for (var w = 0; w < inUse.Count; w++)
        {
            var crafts = inUse[w].Crafts;
            for (var i = 0; i < crafts.Count; i++)
            {
                var craft = crafts[i];
                if (craft.Removed)
                {
                    continue;
                }
                if (craft.Frozen)
                {
                    frozen++;
                }
                else
                {
                    awake++;
                }
                if (craft.Submerged > 0f)
                {
                    afloat++;
                }
            }
        }
        _frame.Awake = awake;
        _frame.Frozen = frozen;
        _frame.Afloat = afloat;
        _frame.Tiles = worlds.TileCount;
        _frame.UnderFormula = underFormula;
    }

    public static void EndFrame(float warpAsked, float warpReached)
    {
        if (!_frameOpen)
        {
            return;
        }
        long now = Stopwatch.GetTimestamp();
        _frameOpen = false;

        var f = _frame;
        f.TotalMs = Ms(now - _lastFrameEnd);
        f.Steps = _stepCount;
        f.PhysicsSteps = _physicsStepCount;
        f.WarpAsked = warpAsked;
        f.WarpReached = warpReached;
        foreach (var part in AllStepParts)
        {
            f[part] = _stepsInFrame[part];
        }
        foreach (var detail in AllStepDetails)
        {
            f[detail] = _stepsInFrame[detail];
        }
        f[FramePart.Flight] = _flightMs;
        double known = 0;
        foreach (var part in AllFrameParts)
        {
            if (part != FramePart.Other)
            {
                known += f[part];
            }
        }
        f[FramePart.Other] = Math.Max(0.0, f.TotalMs - known);
        _frames!.Add(f);

        _lastFrameEnd = now;
        ResetFrame();
        _framesSinceSummary++;
        if (now - _summaryMark >= Stopwatch.Frequency)
        {
            int n = Math.Min(_framesSinceSummary, _frames.Count);
            _frames.CopyLast(_scratch!, n);
            Summary = TimingsSummary.Of(_scratch.AsSpan(0, n));
            _summaryMark = now;
            _framesSinceSummary = 0;
        }
    }

    private static void ResetFrame()
    {
        _stepsInFrame = default;
        _frame = default;
        // No GPU times yet: NaN says so, where 0 would read as a free GPU.
        _frame.GpuMs = double.NaN;
        foreach (var pass in AllGpuPasses)
        {
            _frame[pass] = double.NaN;
        }
        _flightMs = 0;
        _stepCount = 0;
        _physicsStepCount = 0;
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>A fixed-size buffer that keeps the latest items, oldest first, and allocates only when made.</summary>
    private sealed class Ring<T>(int capacity) : IReadOnlyList<T>
    {
        private readonly T[] _items = new T[capacity];
        private int _start;

        public int Count { get; private set; }

        public T this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
                return _items[(_start + index) % _items.Length];
            }
        }

        public void Add(T item)
        {
            if (Count < _items.Length)
            {
                _items[(_start + Count) % _items.Length] = item;
                Count++;
            }
            else
            {
                _items[_start] = item;
                _start = (_start + 1) % _items.Length;
            }
        }

        public void Clear()
        {
            _start = 0;
            Count = 0;
        }

        /// <summary>Copies the latest <paramref name="n"/> items, oldest first, to the start of <paramref name="into"/>.</summary>
        public void CopyLast(T[] into, int n)
        {
            for (int i = 0; i < n; i++)
            {
                into[i] = this[Count - n + i];
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
