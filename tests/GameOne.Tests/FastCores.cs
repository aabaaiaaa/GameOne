using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameOne.Tests;

/// <summary>
/// For the short tests' runs: the process on the fastest cores, unthrottled, ahead of the
/// machine's other work; all put back after. On a hybrid CPU a windowless test process is
/// otherwise placed as background work, and whole runs landed on the slower cores (
/// the floe band at 1.5 or 2.1 ms a step, by invocation). In play the game's window is in front,
/// so it is on the fast cores too: these are the numbers that match play. Windows only (elsewhere
/// it does nothing), and process-wide, so the Jolt workers each scene's simulation starts inside
/// it are covered too; the tests run one at a time, so nothing else in the process is.
/// </summary>
internal sealed class FastCores : IDisposable
{
    public static IDisposable Hold() => OperatingSystem.IsWindows() ? new FastCores() : NoOp.Instance;

    private readonly ProcessPriorityClass? _priority;
    private readonly bool _unthrottled, _cpuSets;

    private FastCores()
    {
        // Ahead of the machine's other work (a build, an editor): which threads run when they
        // compete, not which kind of core they run on. A locked-down machine may refuse it.
        using (var self = Process.GetCurrentProcess())
        {
            try
            {
                var was = self.PriorityClass;
                self.PriorityClass = ProcessPriorityClass.High;
                _priority = was;
            }
            catch (Win32Exception)
            {
                _priority = null;
            }
        }
        // Not efficiency work: on Windows 11 this (the process's QoS), more than its priority, is
        // what decides whether it is put on the fast cores.
        var high = new PowerThrottling { Version = PowerThrottlingVersion, ControlMask = ExecutionSpeed, StateMask = 0 };
        _unthrottled = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottlingClass, ref high, (uint)Marshal.SizeOf<PowerThrottling>());
        // The fastest cores as the process's default, softly (CPU sets, not an affinity mask): a
        // thread can still spill onto another core when they are all busy with others' work,
        // rather than wait. Where every core is alike there is nothing to choose.
        var fastest = CoreKinds.Fastest();
        _cpuSets = fastest.Length > 0 && SetProcessDefaultCpuSets(GetCurrentProcess(), fastest, (uint)fastest.Length);
    }

    public void Dispose()
    {
        if (_cpuSets)
            SetProcessDefaultCpuSets(GetCurrentProcess(), null, 0);
        if (_unthrottled)
        {
            var system = new PowerThrottling { Version = PowerThrottlingVersion }; // both masks 0: the system's choice again
            SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottlingClass, ref system, (uint)Marshal.SizeOf<PowerThrottling>());
        }
        if (_priority is { } p)
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = p;
        }
    }

    private sealed class NoOp : IDisposable
    {
        public static readonly NoOp Instance = new();
        public void Dispose() { }
    }

    private const int ProcessPowerThrottlingClass = 4; // PROCESS_INFORMATION_CLASS.ProcessPowerThrottling
    private const uint PowerThrottlingVersion = 1;     // PROCESS_POWER_THROTTLING_CURRENT_VERSION
    private const uint ExecutionSpeed = 0x1;           // PROCESS_POWER_THROTTLING_EXECUTION_SPEED

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottling
    {
        public uint Version, ControlMask, StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottling info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessDefaultCpuSets(IntPtr process, uint[]? ids, uint count);
}

/// <summary>
/// The machine's kinds of core (Windows' CPU sets, by <c>EfficiencyClass</c>: higher is faster),
/// and a tally of which kind the main thread was on, step by step: the check that a run was on the
/// fast cores, rather than taking <see cref="FastCores"/> on faith. <see cref="Note"/> is called
/// between steps (outside their laps) and allocates nothing.
/// </summary>
internal static class CoreKinds
{
    // Each logical processor's class, by group × 64 + number (GetCurrentProcessorNumberEx's), and
    // each class's CPU set ids; read once.
    private static readonly Lazy<(byte[] ClassOf, Dictionary<byte, uint[]> Ids)> Machine = new(Read);

    private static readonly int[] Counts = new int[256];
    private static int _unknown;

    /// <summary>The CPU set ids of the fastest class, or none where every core is of one class (or not on Windows).</summary>
    public static uint[] Fastest()
    {
        var ids = Machine.Value.Ids;
        return ids.Count > 1 ? ids[ids.Keys.Max()] : [];
    }

    /// <summary>Starts a new tally.</summary>
    public static void Reset()
    {
        Array.Clear(Counts);
        _unknown = 0;
        _ = Machine.Value; // read now, not inside a run
    }

    /// <summary>Notes which kind of core the calling thread is on now.</summary>
    public static void Note()
    {
        if (!OperatingSystem.IsWindows())
        {
            _unknown++;
            return;
        }
        GetCurrentProcessorNumberEx(out var at);
        var index = at.Group * 64 + at.Number;
        var classOf = Machine.Value.ClassOf;
        if (index < classOf.Length && classOf[index] != Unknown)
            Counts[classOf[index]]++;
        else
            _unknown++;
    }

    /// <summary>The tally so far: the share of steps on the fastest cores, and on each class (fastest first).</summary>
    public static string Say()
    {
        var ids = Machine.Value.Ids;
        var total = Counts.Sum() + _unknown;
        if (total == 0)
            return "no steps noted";
        if (ids.Count <= 1)
            return "every core alike";
        var classes = ids.Keys.OrderDescending().ToArray();
        var byClass = string.Join(", ", classes.Select(c => $"class {c} {Counts[c] / (double)total:0%}"));
        return $"{Counts[classes[0]] / (double)total:0.0%} of steps on the fastest cores ({byClass}" +
            (_unknown > 0 ? $", unknown {_unknown / (double)total:0%})" : ")");
    }

    private const byte Unknown = 255;

    private static (byte[] ClassOf, Dictionary<byte, uint[]> Ids) Read()
    {
        var classOf = new byte[0];
        var ids = new Dictionary<byte, List<uint>>();
        if (OperatingSystem.IsWindows())
        {
            GetSystemCpuSetInformation(null, 0, out var length, IntPtr.Zero, 0);
            var info = new byte[length];
            if (length > 0 && GetSystemCpuSetInformation(info, length, out length, IntPtr.Zero, 0))
            {
                var records = new List<(int Index, byte Class, uint Id)>();
                // SYSTEM_CPU_SET_INFORMATION records, each Size bytes: Size@0 (DWORD), Type@4
                // (0 = CpuSetInformation), then the CpuSet: Id@8 (DWORD), Group@12 (WORD),
                // LogicalProcessorIndex@14, CoreIndex@15, LastLevelCacheIndex@16,
                // NumaNodeIndex@17, EfficiencyClass@18.
                for (var at = 0; at + 19 <= length;)
                {
                    var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(at));
                    if (size <= 0)
                        break;
                    if (BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(at + 4)) == 0)
                    {
                        var id = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(at + 8));
                        var group = BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(at + 12));
                        records.Add((group * 64 + info[at + 14], info[at + 18], id));
                    }
                    at += size;
                }
                classOf = new byte[records.Count == 0 ? 0 : records.Max(r => r.Index) + 1];
                Array.Fill(classOf, Unknown);
                foreach (var (index, kind, id) in records)
                {
                    classOf[index] = kind;
                    if (!ids.TryGetValue(kind, out var list))
                        ids[kind] = list = [];
                    list.Add(id);
                }
            }
        }
        return (classOf, ids.ToDictionary(p => p.Key, p => p.Value.ToArray()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorNumber
    {
        public ushort Group;
        public byte Number, Reserved;
    }

    [DllImport("kernel32.dll")]
    private static extern void GetCurrentProcessorNumberEx(out ProcessorNumber number);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(byte[]? info, uint length, out uint returned, IntPtr process, uint flags);
}
