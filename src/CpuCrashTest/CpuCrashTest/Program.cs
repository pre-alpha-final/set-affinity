using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

const string workerFlag = "--worker";
const string modeFlag   = "--mode";

// Parse --mode from anywhere in args (e.g. --worker 3 --mode int).
string? rawMode = null;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == modeFlag) { rawMode = args[i + 1]; break; }

StressMode mode = ParseMode(rawMode);

if (args.Length >= 2 && args[0] == workerFlag && int.TryParse(args[1], out int coreIndex))
    RunWorker(coreIndex, mode);
else
    RunLauncher(mode);

// ── helpers ────────────────────────────────────────────────────────────────

static StressMode ParseMode(string? s) => s?.ToLowerInvariant() switch
{
    "fma"    => StressMode.Fma,
    "int"    => StressMode.Int,
    "branch" => StressMode.Branch,
    "call"    => StressMode.Call,
    "threads" => StressMode.Threads,
    "mixed"   => StressMode.Mixed,
    "divide"  => StressMode.Divide,
    _         => StressMode.Rotate,
};

static string ModeLabel(StressMode m) => m switch
{
    StressMode.Fma    => Avx512F.IsSupported          ? "AVX-512 FMA"
                       : (Fma.IsSupported && Avx2.IsSupported) ? "AVX2+FMA"
                       : "SIMD",
    StressMode.Int    => "INT",
    StressMode.Branch => "BRANCH",
    StressMode.Call    => "CALL/RET",
    StressMode.Threads => "THREADS",
    StressMode.Mixed   => "MIXED",
    StressMode.Divide => "DIVIDE",
    _                 => "ROTATE",
};

// ── launcher ───────────────────────────────────────────────────────────────

static void RunLauncher(StressMode mode)
{
    // Environment.ProcessorCount respects the current process's affinity mask, so it
    // returns the wrong count if SetAffinity has restricted this process. NUMBER_OF_PROCESSORS
    // is a Windows system env var that always holds the true total logical processor count.
    int coreCount = int.TryParse(Environment.GetEnvironmentVariable("NUMBER_OF_PROCESSORS"), out int envCount)
        ? envCount
        : Environment.ProcessorCount;

    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine("╔══════════════════════════════════╗");
    Console.WriteLine("║       CPU CRASH TEST LAUNCHER    ║");
    Console.WriteLine("╚══════════════════════════════════╝");
    Console.WriteLine();
    Console.WriteLine($"Detected {coreCount} logical CPU(s).");
    Console.WriteLine($"Mode    : {ModeLabel(mode)}");
    Console.WriteLine($"Launching {coreCount} worker(s)...");
    Console.WriteLine();

    string exePath  = Process.GetCurrentProcess().MainModule!.FileName;
    string modeStr  = mode.ToString().ToLowerInvariant();
    var    workers  = new List<Process>(coreCount);

    void KillAll()
    {
        foreach (var w in workers)
        {
            try { if (!w.HasExited) w.Kill(); }
            catch { /* already gone */ }
        }
    }

    // Covers Ctrl+C: cancel the default termination so we can clean up first.
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine();
        Console.WriteLine("Stopping — killing all worker windows...");
        KillAll();
        Environment.Exit(0);
    };

    // Covers closing this window via the X button (CTRL_CLOSE_EVENT gives ~5 s).
    AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAll();

    for (int i = 0; i < coreCount; i++)
    {
        var psi = new ProcessStartInfo
        {
            FileName       = exePath,
            Arguments      = $"--worker {i} {modeFlag} {modeStr}",
            UseShellExecute  = true,
            CreateNoWindow = false,
        };
        var proc = Process.Start(psi);
        if (proc is not null)
        {
            workers.Add(proc);
            int core = i;
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => Console.WriteLine($"  [!] Worker for core {core} has closed.");
        }
        Console.WriteLine($"  Launched worker for core {i}");
    }

    Console.WriteLine();
    Console.WriteLine($"All {coreCount} worker(s) launched.");
    Console.WriteLine("NOTE: closing this window will also close all worker windows.");
    Console.WriteLine("Press any key to stop all workers and exit.");
    Console.ReadKey(intercept: true);

    Console.WriteLine();
    Console.WriteLine("Stopping — killing all worker windows...");
    KillAll();
}

// ── worker ─────────────────────────────────────────────────────────────────

static void RunWorker(int coreIndex, StressMode mode)
{
    using var cts = new CancellationTokenSource();

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    try
    {
        Process.GetCurrentProcess().ProcessorAffinity = (nint)(1L << coreIndex);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Core {coreIndex}] WARNING: Could not set affinity — {ex.Message}");
    }

    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine("╔══════════════════════════════════╗");
    Console.WriteLine($"║   CPU CRASH TEST  —  Core: {coreIndex,-5} ║");
    Console.WriteLine("╚══════════════════════════════════╝");
    Console.WriteLine();
    Console.WriteLine($"Mode: {ModeLabel(mode)}");
    Console.WriteLine($"Stressing core {coreIndex}. Press Ctrl+C to stop.");
    Console.WriteLine();

    StressDispatcher(coreIndex, mode, cts.Token);

    Console.WriteLine();
    Console.WriteLine($"[Core {coreIndex}] Stopped.");
}

// ── dispatcher ─────────────────────────────────────────────────────────────

// Runs the selected mode, or rotates through all modes every 30 s.
static void StressDispatcher(int coreIndex, StressMode mode, CancellationToken ct)
{
    if (mode != StressMode.Rotate)
    {
        RunMode(coreIndex, mode, ct);
        return;
    }

    // Integer-heavy modes come first — most likely to expose IA-core clock-tree damage.
    StressMode[] rotation = [StressMode.Int, StressMode.Branch, StressMode.Call, StressMode.Mixed, StressMode.Divide, StressMode.Threads, StressMode.Fma];
    int idx = 0;
    while (!ct.IsCancellationRequested)
    {
        StressMode current = rotation[idx % rotation.Length];
        Console.WriteLine($"\n[Core {coreIndex}] → {ModeLabel(current)}");
        using var cts30 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts30.CancelAfter(TimeSpan.FromSeconds(30));
        RunMode(coreIndex, current, cts30.Token);
        idx++;
    }
}

static void RunMode(int coreIndex, StressMode mode, CancellationToken ct)
{
    switch (mode)
    {
        case StressMode.Int:    StressLoopInt(coreIndex, ct);    break;
        case StressMode.Branch: StressLoopBranch(coreIndex, ct); break;
        case StressMode.Call:    StressLoopCall(coreIndex, ct);    break;
        case StressMode.Threads: StressLoopThreads(coreIndex, ct); break;
        case StressMode.Mixed:   StressLoopMixed(coreIndex, ct);   break;
        case StressMode.Divide: StressLoopDivide(coreIndex, ct); break;
        default:                StressLoopFma(coreIndex, ct);    break;
    }
}

// ── mode: INT ──────────────────────────────────────────────────────────────

// 8 × 64-bit LCG chains + rotate-XOR mixing.
// Targets the integer multiply units that Intel confirmed as the Vmin-shift-affected
// clock tree in Raptor Lake 13th/14th gen IA cores.
static void StressLoopInt(int coreIndex, CancellationToken ct)
{
    double[] buf = new double[512 * 1024];
    var  sw = Stopwatch.StartNew();
    long iterations = 0;

    ulong x0 = 0x123456789ABCDEF0UL, x1 = 0x23456789ABCDEF01UL;
    ulong x2 = 0x3456789ABCDEF012UL, x3 = 0x456789ABCDEF0123UL;
    ulong x4 = 0x56789ABCDEF01234UL, x5 = 0x6789ABCDEF012345UL;
    ulong x6 = 0x789ABCDEF0123456UL, x7 = 0x89ABCDEF01234567UL;

    // Knuth 64-bit LCG constants — full-period with every seed.
    const ulong A = 6364136223846793005UL;
    const ulong B = 1442695040888963407UL;

    while (!ct.IsCancellationRequested)
    {
        // LCG multiply + rotate-XOR mixing per chain: IMUL64, ADD64, ROR64, XOR64, SHR64.
        // 8 independent chains fill all integer execution ports without cross-chain stalls.
        x0 = unchecked(x0 * A + B); x0 = BitOperations.RotateLeft(x0, 17) ^ (x0 >> 31);
        x1 = unchecked(x1 * A + B); x1 = BitOperations.RotateLeft(x1, 23) ^ (x1 >> 29);
        x2 = unchecked(x2 * A + B); x2 = BitOperations.RotateLeft(x2, 11) ^ (x2 >> 37);
        x3 = unchecked(x3 * A + B); x3 = BitOperations.RotateLeft(x3, 31) ^ (x3 >> 19);
        x4 = unchecked(x4 * A + B); x4 = BitOperations.RotateLeft(x4, 7)  ^ (x4 >> 43);
        x5 = unchecked(x5 * A + B); x5 = BitOperations.RotateLeft(x5, 41) ^ (x5 >> 13);
        x6 = unchecked(x6 * A + B); x6 = BitOperations.RotateLeft(x6, 53) ^ (x6 >> 7);
        x7 = unchecked(x7 * A + B); x7 = BitOperations.RotateLeft(x7, 37) ^ (x7 >> 23);
        iterations++;

        if ((iterations & 0xFF) == 0)
        {
            // 32-bit IMUL chains — additionally stress the 32-bit integer multiplier path.
            uint u0 = (uint)x0, u1 = (uint)x1, u2 = (uint)x2, u3 = (uint)x3;
            unchecked
            {
                u0 = u0 * 1664525u + 1013904223u;
                u1 = u1 * 1664525u + 1013904223u;
                u2 = u2 * 1664525u + 1013904223u;
                u3 = u3 * 1664525u + 1013904223u;
            }
            // Fold 32-bit results back into x0 to prevent dead-code elimination.
            x0 ^= (ulong)(u0 | u1) | ((ulong)(u2 | u3) << 32);

            buf[(int)((iterations >> 8) & (buf.Length - 1))] += (double)(long)x0;

            if (iterations % 50_000_000 == 0)
                Console.Write($"\r[Core {coreIndex}] INT — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {iterations / 1_000_000}M iterations");
        }
    }
}

// ── mode: BRANCH ───────────────────────────────────────────────────────────

// xorshift128+ PRNG drives a 16-arm switch whose outcome the branch predictor cannot learn.
// Models Hermes opcode-dispatch jump tables and Unity vtable/null-check branch-dense code.
// Stresses the front-end, BTB, and ROB recovery after mispredictions.
static void StressLoopBranch(int coreIndex, CancellationToken ct)
{
    double[] buf = new double[512 * 1024];
    var  sw = Stopwatch.StartNew();
    long iterations = 0;

    ulong s0  = 0xABCDEF0123456789UL;
    ulong s1  = 0xFEDCBA9876543210UL;
    ulong acc = 1UL;

    while (!ct.IsCancellationRequested)
    {
        // xorshift128+ step — 2^128-1 period, defeats branch prediction.
        ulong t = s0;
        s0 = s1;
        t ^= t << 23;
        t ^= t >> 17;
        t ^= s0 ^ (s0 >> 26);
        s1 = t;
        ulong rng = s0 + s1;

        // 16-arm switch on unpredictable data — each arm does a distinct computation
        // to prevent the JIT from collapsing the switch.
        switch (rng & 0xF)
        {
            case  0: acc += rng  * 0xABCDUL;                          break;
            case  1: acc ^= BitOperations.RotateLeft(rng, 13);        break;
            case  2: acc -= rng  >> 7;                                 break;
            case  3: acc |= rng  << 3;                                 break;
            case  4: acc += unchecked(rng * rng);                      break;
            case  5: acc ^= rng  >> 17;                                break;
            case  6: acc &= rng  | 1UL;                                break;
            case  7: acc += (ulong)BitOperations.PopCount(rng);        break;
            case  8: acc ^= rng  * 0x9E3779B97F4A7C15UL;              break;
            case  9: acc -= BitOperations.RotateLeft(rng, 31);         break;
            case 10: acc += rng  ^ (rng >> 32);                        break;
            case 11: acc |= unchecked(rng * 0x517CC1B727220A95UL);    break;
            case 12: acc ^= rng  + (rng << 17);                        break;
            case 13: acc &= rng  ^ (rng >> 5);                         break;
            case 14: acc += rng  * 0x27D4EB2F165667C5UL;              break;
            default: acc ^= BitOperations.RotateLeft(rng, 47);         break;
        }
        iterations++;

        if ((iterations & 0xFF) == 0)
        {
            buf[(int)((iterations >> 8) & (buf.Length - 1))] += (double)(long)acc;

            if (iterations % 50_000_000 == 0)
                Console.Write($"\r[Core {coreIndex}] BRANCH — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {iterations / 1_000_000}M iterations");
        }
    }
}

// ── mode: MIXED ────────────────────────────────────────────────────────────

// Alternates integer multiply chains with FP ops, bridged by explicit INT↔FP conversions
// (CVTSI2SD / CVTTSD2SI) every iteration.  Stresses the bypass network between the integer
// and FP execution units — the dominant pattern in Unity IL2CPP arithmetic and Hermes
// NaN-boxing (JS values are 64-bit integer bit-patterns interpreted as doubles).
static void StressLoopMixed(int coreIndex, CancellationToken ct)
{
    double[] buf = new double[512 * 1024];
    var  sw = Stopwatch.StartNew();
    long iterations = 0;

    long i0 = 0x123456789ABCDEFL, i1 = 0x23456789ABCDEF0L;
    long i2 = 0x3456789ABCDEF01L, i3 = 0x456789ABCDEF012L;
    long i4 = 0x56789ABCDEF0123L, i5 = 0x6789ABCDEF01234L;
    long i6 = 0x789ABCDEF012345L, i7 = 0x89ABCDEF0123456L;

    double f0 = 1.1, f1 = 2.2, f2 = 3.3, f3 = 4.4;
    double f4 = 5.5, f5 = 6.6, f6 = 7.7, f7 = 8.8;

    const long IA = 6364136223846793005L;
    const long IB = 1442695040888963407L;

    while (!ct.IsCancellationRequested)
    {
        // Integer step: 8 × IMUL64 chains.
        i0 = unchecked(i0 * IA + IB); i1 = unchecked(i1 * IA + IB);
        i2 = unchecked(i2 * IA + IB); i3 = unchecked(i3 * IA + IB);
        i4 = unchecked(i4 * IA + IB); i5 = unchecked(i5 * IA + IB);
        i6 = unchecked(i6 * IA + IB); i7 = unchecked(i7 * IA + IB);

        // INT→FP: CVTSI2SD — data crosses from integer execution unit to FP port.
        f0 += (double)i0 * 1e-19; f1 += (double)i1 * 1e-19;
        f2 += (double)i2 * 1e-19; f3 += (double)i3 * 1e-19;
        f4 += (double)i4 * 1e-19; f5 += (double)i5 * 1e-19;
        f6 += (double)i6 * 1e-19; f7 += (double)i7 * 1e-19;

        // FP multiply-add (scalar double).
        f0 = f0 * 1.0000001 + 0.0000001; f1 = f1 * 1.0000001 + 0.0000001;
        f2 = f2 * 1.0000001 + 0.0000001; f3 = f3 * 1.0000001 + 0.0000001;
        f4 = f4 * 1.0000001 + 0.0000001; f5 = f5 * 1.0000001 + 0.0000001;
        f6 = f6 * 1.0000001 + 0.0000001; f7 = f7 * 1.0000001 + 0.0000001;

        // FP→INT: CVTTSD2SI — data crosses back from FP port to integer execution unit.
        i0 ^= (long)f0; i1 ^= (long)f1;
        i2 ^= (long)f2; i3 ^= (long)f3;
        i4 ^= (long)f4; i5 ^= (long)f5;
        i6 ^= (long)f6; i7 ^= (long)f7;

        iterations++;

        if ((iterations & 0xFF) == 0)
        {
            buf[(int)((iterations >> 8) & (buf.Length - 1))] += f0 + (double)(i0 & 0x7FFFFFFFL) * 1e-20;

            // Reset FP chains before they overflow long on the next CVTTSD2SI.
            if (f0 > 1e15 || f0 < -1e15)
            {
                f0 = 1.1; f1 = 2.2; f2 = 3.3; f3 = 4.4;
                f4 = 5.5; f5 = 6.6; f6 = 7.7; f7 = 8.8;
            }

            if (iterations % 50_000_000 == 0)
                Console.Write($"\r[Core {coreIndex}] MIXED — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {iterations / 1_000_000}M iterations");
        }
    }
}

// ── mode: DIVIDE ───────────────────────────────────────────────────────────

// 8 chains of 64-bit integer division.  IDIV is a variable-latency microcode sequence
// (20–100+ cycles) that exercises a distinct execution path untouched by all other modes.
static void StressLoopDivide(int coreIndex, CancellationToken ct)
{
    double[] buf = new double[512 * 1024];
    var  sw = Stopwatch.StartNew();
    long iterations = 0;

    long d0 = 0x7FEDCBA987654321L, d1 = 0x7EDCBA9876543210L;
    long d2 = 0x7DCBA98765432101L, d3 = 0x7CBA987654321012L;
    long d4 = 0x7BA9876543210123L, d5 = 0x7A98765432101234L;
    long d6 = 0x798765432101234AL, d7 = 0x6987654321012345L;

    // Array load prevents the JIT from substituting compile-time multiplicative inverses,
    // ensuring actual IDIV instructions are emitted for each chain.
    long[] divisors = [7L, 11L, 13L, 17L, 19L, 23L, 29L, 31L];
    int divIdx = 0;

    while (!ct.IsCancellationRequested)
    {
        long div = divisors[divIdx & 7];

        // `x / div + x % div * 97` compiles to one IDIV (quotient + remainder together).
        d0 = d0 / div + d0 % div * 97L;
        d1 = d1 / div + d1 % div * 97L;
        d2 = d2 / div + d2 % div * 97L;
        d3 = d3 / div + d3 % div * 97L;
        d4 = d4 / div + d4 % div * 97L;
        d5 = d5 / div + d5 % div * 97L;
        d6 = d6 / div + d6 % div * 97L;
        d7 = d7 / div + d7 % div * 97L;
        divIdx++;
        iterations++;

        if ((iterations & 0xFF) == 0)
        {
            buf[(int)((iterations >> 8) & (buf.Length - 1))] += (double)d0;

            // Refill chains that have converged to zero through repeated division.
            if (d0 <= 0) d0 = 0x7FEDCBA987654321L;
            if (d1 <= 0) d1 = 0x7EDCBA9876543210L;
            if (d2 <= 0) d2 = 0x7DCBA98765432101L;
            if (d3 <= 0) d3 = 0x7CBA987654321012L;
            if (d4 <= 0) d4 = 0x7BA9876543210123L;
            if (d5 <= 0) d5 = 0x7A98765432101234L;
            if (d6 <= 0) d6 = 0x798765432101234AL;
            if (d7 <= 0) d7 = 0x6987654321012345L;

            if (iterations % 50_000_000 == 0)
                Console.Write($"\r[Core {coreIndex}] DIVIDE — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {iterations / 1_000_000}M iterations");
        }
    }
}

// ── mode: CALL/RET ────────────────────────────────────────────────────────

// Hammers CALL and RET instructions by recursing to a depth that exceeds the RSB
// (Return Stack Buffer, typically 16–24 entries on Raptor Lake P-cores).  Once the RSB
// is exhausted, every RET must speculate the return address via the indirect predictor —
// mis-speculations flush the pipeline and exercise the front-end recovery path.
// This models the function-call-heavy patterns in Hermes (JS interpreter frames) and
// Unity Mono (managed-to-native call stubs, GC barriers).
static void StressLoopCall(int coreIndex, CancellationToken ct)
{
    double[] buf = new double[512 * 1024];
    var  sw = Stopwatch.StartNew();
    long iterations = 0;
    long acc = 0x123456789ABCDEFL;

    // 48 levels: comfortably exceeds the RSB depth on all Raptor Lake variants,
    // ensuring a mix of correct RSB hits (shallow frames) and mis-speculating RETs
    // (deep frames beyond RSB capacity).
    const int CallDepth = 48;

    while (!ct.IsCancellationRequested)
    {
        acc = Recurse(acc, CallDepth);
        iterations++;

        if ((iterations & 0xFF) == 0)
        {
            buf[(int)((iterations >> 8) & (buf.Length - 1))] += (double)acc;

            if (iterations % 5_000_000 == 0)
                Console.Write($"\r[Core {coreIndex}] CALL/RET — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {iterations / 1_000_000}M iterations");
        }
    }
}

// NoInlining forces a real CALL/RET pair at every level (the JIT cannot collapse the
// chain into a loop).  The post-call XOR ensures the frame is not in tail position,
// blocking tail-call optimisation and keeping the full return chain intact.
[MethodImpl(MethodImplOptions.NoInlining)]
static long Recurse(long v, int depth)
{
    if (depth == 0)
        return unchecked(v * 6364136223846793005L + 1442695040888963407L);
    long child = Recurse(unchecked(v * 6364136223846793005L + 1442695040888963407L), depth - 1);
    return unchecked(child ^ (v >> 3));
}

// ── mode: THREADS ─────────────────────────────────────────────────────────

// Runs 4 threads on the same core (3 companions + the calling thread).  Each thread
// does a burst of integer+FP work to fill registers with live state, then calls
// Thread.Yield() to hand the core to one of its siblings.  The kernel must then execute
// XSAVE (saves GP, SSE, and AVX register state) and XRSTOR (restores the next thread's
// state) on every switch — multi-cycle microcode sequences absent in every other mode.
// Also triggers SYSCALL/SYSRET and the RSB flush applied on ring-3→0 transitions.
static void StressLoopThreads(int coreIndex, CancellationToken ct)
{
    double[] buf = new double[512 * 1024];
    var  sw = Stopwatch.StartNew();
    long yields = 0;

    // Companion threads inherit the process's single-core affinity automatically,
    // so all 4 threads compete for the same logical CPU and switch rapidly.
    const int CompanionCount = 3;
    var companions = new Thread[CompanionCount];

    for (int t = 0; t < CompanionCount; t++)
    {
        int tid = t;
        companions[t] = new Thread(() =>
        {
            ulong x = unchecked((ulong)(tid + 2) * 0x9E3779B97F4A7C15UL);
            const ulong CA = 6364136223846793005UL;
            const ulong CB = 1442695040888963407UL;

            while (!ct.IsCancellationRequested)
            {
                // Fill GPRs with live data before the switch so XSAVE has state worth saving.
                for (int i = 0; i < 32; i++)
                {
                    x = unchecked(x * CA + CB);
                    x ^= BitOperations.RotateLeft(x, 17);
                }
                // Dirty XMM/YMM state so XSAVE also captures the full FP/AVX context.
                double d = (double)x * 1e-10;
                d = d * 1.0000001 + 0.0000001;
                if (d > 1e15) x ^= (ulong)d;

                Thread.Yield();
            }
        }) { IsBackground = true };
        companions[t].Start();
    }

    ulong acc = 0x123456789ABCDEF0UL;
    const ulong A = 6364136223846793005UL;
    const ulong B = 1442695040888963407UL;
    double fd = 1.0;

    while (!ct.IsCancellationRequested)
    {
        for (int i = 0; i < 32; i++)
        {
            acc = unchecked(acc * A + B);
            acc ^= BitOperations.RotateLeft(acc, 17);
        }
        fd = (double)acc * 1e-10;
        fd = fd * 1.0000001 + 0.0000001;
        if (fd > 1e15) acc ^= (ulong)fd;

        Thread.Yield();
        yields++;

        if ((yields & 0xFF) == 0)
        {
            buf[(int)((yields >> 8) & (buf.Length - 1))] += fd;

            if (yields % 100_000 == 0)
                Console.Write($"\r[Core {coreIndex}] THREADS — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {yields / 1_000}K yields");
        }
    }

    foreach (var th in companions)
        th.Join(1000);
}

// ── mode: FMA (original, unchanged) ───────────────────────────────────────

// Dispatches to the widest SIMD FMA path the CPU supports at runtime.
static void StressLoopFma(int coreIndex, CancellationToken ct)
{
    if (Avx512F.IsSupported)
        StressLoop512(coreIndex, ct);
    else if (Fma.IsSupported && Avx2.IsSupported)
        StressLoop256(coreIndex, ct);
    else
        StressLoopVector(coreIndex, ct);
}

// AVX-512 path: 8 × Vector512<double> FMA chains — 64 doubles in-flight per iteration.
static void StressLoop512(int coreIndex, CancellationToken ct)
{
    double[] buf = new double[512 * 1024];
    var sw = Stopwatch.StartNew();
    long iterations = 0;

    var mul = Vector512.Create(1.0000001);
    var add = Vector512.Create(0.0000001);

    // 8 independent accumulators — no inter-chain dependency, fills all FP execution ports.
    var a0 = Vector512.Create(1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.7, 1.8);
    var a1 = Vector512.Create(2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 2.7, 2.8);
    var a2 = Vector512.Create(3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7, 3.8);
    var a3 = Vector512.Create(4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 4.7, 4.8);
    var a4 = Vector512.Create(5.1, 5.2, 5.3, 5.4, 5.5, 5.6, 5.7, 5.8);
    var a5 = Vector512.Create(6.1, 6.2, 6.3, 6.4, 6.5, 6.6, 6.7, 6.8);
    var a6 = Vector512.Create(7.1, 7.2, 7.3, 7.4, 7.5, 7.6, 7.7, 7.8);
    var a7 = Vector512.Create(8.1, 8.2, 8.3, 8.4, 8.5, 8.6, 8.7, 8.8);

    // Keep initial seeds for the periodic overflow reset.
    var s0 = a0; var s1 = a1; var s2 = a2; var s3 = a3;
    var s4 = a4; var s5 = a5; var s6 = a6; var s7 = a7;

    while (!ct.IsCancellationRequested)
    {
        a0 = Avx512F.FusedMultiplyAdd(a0, mul, add);
        a1 = Avx512F.FusedMultiplyAdd(a1, mul, add);
        a2 = Avx512F.FusedMultiplyAdd(a2, mul, add);
        a3 = Avx512F.FusedMultiplyAdd(a3, mul, add);
        a4 = Avx512F.FusedMultiplyAdd(a4, mul, add);
        a5 = Avx512F.FusedMultiplyAdd(a5, mul, add);
        a6 = Avx512F.FusedMultiplyAdd(a6, mul, add);
        a7 = Avx512F.FusedMultiplyAdd(a7, mul, add);
        iterations++;

        if ((iterations & 0xFF) == 0)
        {
            // Strided cache write using a live accumulator — stresses load/store units
            // and L2/L3 cache without stealing CPU time from the FMA chain.
            buf[(int)((iterations >> 8) & (buf.Length - 1))] += a0.GetElement(0);

            // Values grow exponentially with mul > 1; reset before overflow.
            if (a0.GetElement(0) > 1e15)
            {
                a0 = s0; a1 = s1; a2 = s2; a3 = s3;
                a4 = s4; a5 = s5; a6 = s6; a7 = s7;
            }

            if (iterations % 50_000_000 == 0)
                Console.Write($"\r[Core {coreIndex}] AVX-512 FMA — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {iterations / 1_000_000}M iterations");
        }
    }
}

// AVX2+FMA path: 8 × Vector256<double> FMA chains — 32 doubles in-flight per iteration.
static void StressLoop256(int coreIndex, CancellationToken ct)
{
    double[] buf = new double[512 * 1024];
    var sw = Stopwatch.StartNew();
    long iterations = 0;

    var mul = Vector256.Create(1.0000001);
    var add = Vector256.Create(0.0000001);

    var a0 = Vector256.Create(1.1, 1.2, 1.3, 1.4);
    var a1 = Vector256.Create(2.1, 2.2, 2.3, 2.4);
    var a2 = Vector256.Create(3.1, 3.2, 3.3, 3.4);
    var a3 = Vector256.Create(4.1, 4.2, 4.3, 4.4);
    var a4 = Vector256.Create(5.1, 5.2, 5.3, 5.4);
    var a5 = Vector256.Create(6.1, 6.2, 6.3, 6.4);
    var a6 = Vector256.Create(7.1, 7.2, 7.3, 7.4);
    var a7 = Vector256.Create(8.1, 8.2, 8.3, 8.4);

    var s0 = a0; var s1 = a1; var s2 = a2; var s3 = a3;
    var s4 = a4; var s5 = a5; var s6 = a6; var s7 = a7;

    while (!ct.IsCancellationRequested)
    {
        a0 = Fma.MultiplyAdd(a0, mul, add);
        a1 = Fma.MultiplyAdd(a1, mul, add);
        a2 = Fma.MultiplyAdd(a2, mul, add);
        a3 = Fma.MultiplyAdd(a3, mul, add);
        a4 = Fma.MultiplyAdd(a4, mul, add);
        a5 = Fma.MultiplyAdd(a5, mul, add);
        a6 = Fma.MultiplyAdd(a6, mul, add);
        a7 = Fma.MultiplyAdd(a7, mul, add);
        iterations++;

        if ((iterations & 0xFF) == 0)
        {
            buf[(int)((iterations >> 8) & (buf.Length - 1))] += a0.GetElement(0);

            if (a0.GetElement(0) > 1e15)
            {
                a0 = s0; a1 = s1; a2 = s2; a3 = s3;
                a4 = s4; a5 = s5; a6 = s6; a7 = s7;
            }

            if (iterations % 50_000_000 == 0)
                Console.Write($"\r[Core {coreIndex}] AVX2+FMA — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {iterations / 1_000_000}M iterations");
        }
    }
}

// Fallback path: 8 × Vector<double> chains — auto-selects the widest SIMD the JIT offers.
static void StressLoopVector(int coreIndex, CancellationToken ct)
{
    double[] buf = new double[512 * 1024];
    var sw = Stopwatch.StartNew();
    long iterations = 0;

    var mul = new Vector<double>(1.0000001);
    var add = new Vector<double>(0.0000001);

    var a0 = new Vector<double>(1.1);
    var a1 = new Vector<double>(2.1);
    var a2 = new Vector<double>(3.1);
    var a3 = new Vector<double>(4.1);
    var a4 = new Vector<double>(5.1);
    var a5 = new Vector<double>(6.1);
    var a6 = new Vector<double>(7.1);
    var a7 = new Vector<double>(8.1);

    var s0 = a0; var s1 = a1; var s2 = a2; var s3 = a3;
    var s4 = a4; var s5 = a5; var s6 = a6; var s7 = a7;

    while (!ct.IsCancellationRequested)
    {
        a0 = a0 * mul + add;
        a1 = a1 * mul + add;
        a2 = a2 * mul + add;
        a3 = a3 * mul + add;
        a4 = a4 * mul + add;
        a5 = a5 * mul + add;
        a6 = a6 * mul + add;
        a7 = a7 * mul + add;
        iterations++;

        if ((iterations & 0xFF) == 0)
        {
            buf[(int)((iterations >> 8) & (buf.Length - 1))] += a0[0];

            if (a0[0] > 1e15)
            {
                a0 = s0; a1 = s1; a2 = s2; a3 = s3;
                a4 = s4; a5 = s5; a6 = s6; a7 = s7;
            }

            if (iterations % 50_000_000 == 0)
                Console.Write($"\r[Core {coreIndex}] SIMD — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {iterations / 1_000_000}M iterations");
        }
    }
}

// ── types ──────────────────────────────────────────────────────────────────

enum StressMode { Fma, Int, Branch, Call, Threads, Mixed, Divide, Rotate }
