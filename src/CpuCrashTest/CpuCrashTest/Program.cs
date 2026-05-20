using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    "fma"          => StressMode.Fma,
    "int"          => StressMode.Int,
    "branch"       => StressMode.Branch,
    "call"         => StressMode.Call,
    "threads"      => StressMode.Threads,
    "mixed"        => StressMode.Mixed,
    "divide"       => StressMode.Divide,
    "pointerchase" => StressMode.PointerChase,
    "ptrchase"     => StressMode.PointerChase,
    "dispatch"     => StressMode.Dispatch,
    "bursty"       => StressMode.Bursty,
    "verify"       => StressMode.Verify,
    "hermes"       => StressMode.Hermes,
    "atomic"       => StressMode.Atomic,
    "memcpy"       => StressMode.Memcpy,
    "bmi"          => StressMode.Bmi,
    "idxchase"     => StressMode.IdxChase,
    "idx"          => StressMode.IdxChase,
    "idxcall"      => StressMode.IdxCall,
    _              => StressMode.Rotate,
};

static string ModeLabel(StressMode m) => m switch
{
    StressMode.Fma    => Avx512F.IsSupported          ? "AVX-512 FMA"
                       : (Fma.IsSupported && Avx2.IsSupported) ? "AVX2+FMA"
                       : "SIMD",
    StressMode.Int          => "INT",
    StressMode.Branch       => "BRANCH",
    StressMode.Call         => "CALL/RET",
    StressMode.Threads      => "THREADS",
    StressMode.Mixed        => "MIXED",
    StressMode.Divide       => "DIVIDE",
    StressMode.PointerChase => "POINTER-CHASE",
    StressMode.Dispatch     => "DISPATCH",
    StressMode.Bursty       => "BURSTY",
    StressMode.Verify       => "VERIFY",
    StressMode.Hermes       => "HERMES",
    StressMode.Atomic       => "ATOMIC",
    StressMode.Memcpy       => "MEMCPY",
    StressMode.Bmi          => "BMI",
    StressMode.IdxChase     => "IDX-CHASE",
    StressMode.IdxCall      => "IDX-CALL",
    _                       => "ROTATE",
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

    // Pointer-dereferencing and transition-heavy modes come first — most likely to convert
    // silent ALU corruption on a faulty core into an observable AV (0xC0000005), matching the
    // hermes.dll / Unity crash signature.  Integer-heavy modes follow.  FMA last.
    StressMode[] rotation = [
        StressMode.IdxCall, StressMode.IdxChase, StressMode.Hermes, StressMode.Atomic,
        StressMode.PointerChase, StressMode.Dispatch, StressMode.Bmi, StressMode.Memcpy,
        StressMode.Bursty, StressMode.Verify, StressMode.Int, StressMode.Branch,
        StressMode.Call, StressMode.Mixed, StressMode.Divide, StressMode.Threads, StressMode.Fma,
    ];
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
        case StressMode.Int:          StressLoopInt(coreIndex, ct);          break;
        case StressMode.Branch:       StressLoopBranch(coreIndex, ct);       break;
        case StressMode.Call:         StressLoopCall(coreIndex, ct);         break;
        case StressMode.Threads:      StressLoopThreads(coreIndex, ct);      break;
        case StressMode.Mixed:        StressLoopMixed(coreIndex, ct);        break;
        case StressMode.Divide:       StressLoopDivide(coreIndex, ct);       break;
        case StressMode.PointerChase: StressLoopPointerChase(coreIndex, ct); break;
        case StressMode.Dispatch:     StressLoopDispatch(coreIndex, ct);     break;
        case StressMode.Bursty:       StressLoopBursty(coreIndex, ct);       break;
        case StressMode.Verify:       StressLoopVerify(coreIndex, ct);       break;
        case StressMode.Hermes:       StressLoopHermes(coreIndex, ct);       break;
        case StressMode.Atomic:       StressLoopAtomic(coreIndex, ct);       break;
        case StressMode.Memcpy:       StressLoopMemcpy(coreIndex, ct);       break;
        case StressMode.Bmi:          StressLoopBmi(coreIndex, ct);          break;
        case StressMode.IdxChase:     StressLoopIdxChase(coreIndex, ct);     break;
        case StressMode.IdxCall:      StressLoopIdxCall(coreIndex, ct);      break;
        default:                      StressLoopFma(coreIndex, ct);          break;
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

// ── mode: POINTER-CHASE ────────────────────────────────────────────────────

// Mimics the hermes.dll NaN-box decode pattern and Unity GC-root walks: an integer ALU
// chain produces a value that is immediately used as a load address.  Healthy core: the
// ALU steps cancel each other and the result equals the loaded pointer, so the chase
// stays in-bounds.  Faulty core: any silent bit-flip in the ALU chain produces a wild
// pointer and the next dereference faults with AV (0xC0000005) — the same crash signature
// users see in hermes.dll and UnityPlayer.dll on degraded Raptor Lake cores.
static unsafe void StressLoopPointerChase(int coreIndex, CancellationToken ct)
{
    const int N = 1 << 21;                                  // 2M slots × 8 B = 16 MiB > L2
    byte* region = (byte*)NativeMemory.AlignedAlloc((nuint)N * 8, 64);
    try
    {
        long* slots = (long*)region;

        // Sattolo shuffle — produces a single cycle of length N through the array,
        // guaranteeing the chase visits every slot before repeating.
        var rng = new Random(unchecked(0x0C0FFEE + coreIndex * 7919));
        int[] perm = new int[N];
        for (int i = 0; i < N; i++) perm[i] = i;
        for (int i = N - 1; i > 0; i--)
        {
            int j = rng.Next(i);                            // j < i — defining feature of Sattolo
            (perm[i], perm[j]) = (perm[j], perm[i]);
        }

        // Each slot stores the absolute address of the next slot in the cycle.
        for (int i = 0; i < N; i++)
            slots[i] = (long)(region + (long)perm[i] * 8L);

        // NaN-box tag constants.  TAG_HI mimics the 13-bit NaN exponent + sign bits that
        // Hermes uses; TAG_LO mimics the 3 low-bit pointer-type tag.  All region pointers
        // are 8-byte aligned, so their low 3 bits are zero — the AND/OR pair below is a
        // no-op on healthy silicon and pinpoints any single-bit ALU corruption.
        const long TAG_HI = unchecked((long)0xFFFE_0000_0000_0000UL);
        const long TAG_LO = 0x0000_0000_0000_0007L;

        var sw = Stopwatch.StartNew();
        long iterations = 0;
        double[] buf = new double[512 * 1024];
        long p = (long)slots;                               // start at slot[0]

        while (!ct.IsCancellationRequested)
        {
            // 8× unrolled: load → tag (XOR + OR) → untag (AND + XOR) → dereference.
            // On a healthy core, v after the ALU chain equals *(long*)p bit-for-bit, so the
            // chase follows the Sattolo cycle.  On a faulty core, any glitched ALU output
            // produces a wild pointer; the next iteration's `*(long*)p` faults.
            long v;
            v = *(long*)p; v = ((v ^ TAG_HI) | TAG_LO); v = ((v & ~TAG_LO) ^ TAG_HI); p = v;
            v = *(long*)p; v = ((v ^ TAG_HI) | TAG_LO); v = ((v & ~TAG_LO) ^ TAG_HI); p = v;
            v = *(long*)p; v = ((v ^ TAG_HI) | TAG_LO); v = ((v & ~TAG_LO) ^ TAG_HI); p = v;
            v = *(long*)p; v = ((v ^ TAG_HI) | TAG_LO); v = ((v & ~TAG_LO) ^ TAG_HI); p = v;
            v = *(long*)p; v = ((v ^ TAG_HI) | TAG_LO); v = ((v & ~TAG_LO) ^ TAG_HI); p = v;
            v = *(long*)p; v = ((v ^ TAG_HI) | TAG_LO); v = ((v & ~TAG_LO) ^ TAG_HI); p = v;
            v = *(long*)p; v = ((v ^ TAG_HI) | TAG_LO); v = ((v & ~TAG_LO) ^ TAG_HI); p = v;
            v = *(long*)p; v = ((v ^ TAG_HI) | TAG_LO); v = ((v & ~TAG_LO) ^ TAG_HI); p = v;
            iterations++;

            if ((iterations & 0xFF) == 0)
            {
                buf[(int)((iterations >> 8) & (buf.Length - 1))] += (double)(p & 0x7FFFFFFFL);

                if (iterations % 5_000_000 == 0)
                    Console.Write($"\r[Core {coreIndex}] POINTER-CHASE — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {iterations * 8 / 1_000_000}M chases");
            }
        }
    }
    finally
    {
        NativeMemory.AlignedFree(region);
    }
}

// ── mode: DISPATCH ─────────────────────────────────────────────────────────

// Threaded-interpreter / vtable analogue.  A table of 256 function pointers is dispatched
// via `state = table[state & 0xFF](state)` — the indirect-call target depends on the
// previous handler's return value, so the BTB cannot stably predict it.  Models the
// hermes.dll opcode-dispatch loop (computed JMP through a 256-entry handler table) and
// Unity vtable calls.  A corrupted return address or a corrupted dispatch index produces
// an indirect call to a bogus address and faults with AV (0xC0000005).
static unsafe void StressLoopDispatch(int coreIndex, CancellationToken ct)
{
    const int TableSize = 256;
    var table = (delegate*<long, long>*)NativeMemory.Alloc((nuint)TableSize * (nuint)sizeof(nint));
    try
    {
        // 8 distinct handlers tile the table.  Different handlers per index slot keep
        // the BTB from settling on a single hot target.
        for (int i = 0; i < TableSize; i++)
        {
            table[i] = (i & 7) switch
            {
                0 => &H_Add,
                1 => &H_Xor,
                2 => &H_Rol,
                3 => &H_Mul,
                4 => &H_Shl,
                5 => &H_Sub,
                6 => &H_Or,
                _ => &H_Mix,
            };
        }

        var sw = Stopwatch.StartNew();
        long iterations = 0;
        double[] buf = new double[512 * 1024];
        long state = unchecked(0x123456789ABCDEF0L + (long)coreIndex * 7919L);

        while (!ct.IsCancellationRequested)
        {
            // 8× unrolled.  Each call's target depends on the previous call's result,
            // forcing a true data-dependent indirect-branch chain — the textbook pattern
            // that exercises front-end prediction, RSB, and ROB recovery.
            state = table[(int)(state & 0xFF)](state);
            state = table[(int)(state & 0xFF)](state);
            state = table[(int)(state & 0xFF)](state);
            state = table[(int)(state & 0xFF)](state);
            state = table[(int)(state & 0xFF)](state);
            state = table[(int)(state & 0xFF)](state);
            state = table[(int)(state & 0xFF)](state);
            state = table[(int)(state & 0xFF)](state);
            iterations++;

            if ((iterations & 0xFF) == 0)
            {
                buf[(int)((iterations >> 8) & (buf.Length - 1))] += (double)(state & 0x7FFFFFFFL);

                if (iterations % 5_000_000 == 0)
                    Console.Write($"\r[Core {coreIndex}] DISPATCH — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {iterations * 8 / 1_000_000}M dispatches");
            }
        }
    }
    finally
    {
        NativeMemory.Free(table);
    }
}

// NoInlining keeps each handler as a real CALL/RET pair the JIT cannot fold into the
// loop body — without this, RyuJIT would devirtualise the function-pointer table once
// it proves the targets are statically known.
[MethodImpl(MethodImplOptions.NoInlining)] static long H_Add(long s) => unchecked(s + (long)0x9E37_79B9_7F4A_7C15UL);
[MethodImpl(MethodImplOptions.NoInlining)] static long H_Xor(long s) => s ^ unchecked((long)0xDEAD_BEEF_CAFE_BABEUL);
[MethodImpl(MethodImplOptions.NoInlining)] static long H_Rol(long s) => (long)BitOperations.RotateLeft((ulong)s, 17);
[MethodImpl(MethodImplOptions.NoInlining)] static long H_Mul(long s) => unchecked(s * 6364136223846793005L);
[MethodImpl(MethodImplOptions.NoInlining)] static long H_Shl(long s) => unchecked((s << 7) ^ (s >> 3));
[MethodImpl(MethodImplOptions.NoInlining)] static long H_Sub(long s) => unchecked(s - 0x1234_5678_9ABC_DEF0L);
[MethodImpl(MethodImplOptions.NoInlining)] static long H_Or (long s) => s | 0x42L;
[MethodImpl(MethodImplOptions.NoInlining)] static long H_Mix(long s) => unchecked(s ^ (s >> 13) ^ (s << 7));

// ── mode: BURSTY ───────────────────────────────────────────────────────────

// Vmin-transition stressor.  Raptor Lake clock-tree degradation is reportedly most
// aggressive during rapid voltage/frequency transitions, not steady 100 % load.  This
// mode alternates ~10 ms of tight INT+FP work (which spins up the core to max P-state)
// with ~2 ms of Thread.Sleep (which lets the core drop back to a low P-state).  Each
// transition crosses the Vmin range many times per second.
static void StressLoopBursty(int coreIndex, CancellationToken ct)
{
    var sw = Stopwatch.StartNew();
    long bursts = 0;
    double[] buf = new double[512 * 1024];

    ulong x0 = 0x123456789ABCDEF0UL, x1 = 0x23456789ABCDEF01UL;
    ulong x2 = 0x3456789ABCDEF012UL, x3 = 0x456789ABCDEF0123UL;
    double f0 = 1.1, f1 = 2.2, f2 = 3.3, f3 = 4.4;

    const ulong A = 6364136223846793005UL;
    const ulong B = 1442695040888963407UL;

    var burstSw = new Stopwatch();
    while (!ct.IsCancellationRequested)
    {
        burstSw.Restart();
        // ~10 ms tight INT+FP burst — no progress checks, no branching surprises;
        // pure issue-port pressure so the core ramps to its peak P-state quickly.
        while (burstSw.ElapsedMilliseconds < 10)
        {
            for (int i = 0; i < 4096; i++)
            {
                x0 = unchecked(x0 * A + B); x1 = unchecked(x1 * A + B);
                x2 = unchecked(x2 * A + B); x3 = unchecked(x3 * A + B);
                f0 = f0 * 1.0000001 + 0.0000001; f1 = f1 * 1.0000001 + 0.0000001;
                f2 = f2 * 1.0000001 + 0.0000001; f3 = f3 * 1.0000001 + 0.0000001;
            }
        }

        // Reset FP chains before they overflow.
        if (f0 > 1e15) { f0 = 1.1; f1 = 2.2; f2 = 3.3; f3 = 4.4; }

        // ~2 ms park — long enough for the kernel to drop the core's frequency request
        // and for the package C-state controller to step the voltage down.
        Thread.Sleep(2);
        bursts++;

        if ((bursts & 0xFF) == 0)
        {
            buf[(int)((bursts >> 8) & (buf.Length - 1))] += f0 + (double)(x0 & 0x7FFFFFFFUL);

            if (bursts % 1000 == 0)
                Console.Write($"\r[Core {coreIndex}] BURSTY — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {bursts} bursts");
        }
    }
}

// ── mode: VERIFY ───────────────────────────────────────────────────────────

// Silent-error detector.  Runs four LCG+mix shadow chains from the same seed with the
// same operations.  On a healthy core, all four chains stay bit-identical forever.  On
// a faulty core, a single transient port glitch desynchronises one chain from the other
// three — and from that point on, the divergence persists, so any glitch detected once
// is loudly visible on every subsequent block.  Does NOT crash; logs mismatches.  This
// is the only mode that catches Raptor Lake degradation when it manifests as silent
// miscalculation instead of an immediate AV.
static void StressLoopVerify(int coreIndex, CancellationToken ct)
{
    var sw = Stopwatch.StartNew();
    long blocks = 0;
    long mismatches = 0;
    double[] buf = new double[512 * 1024];

    const ulong K    = 6364136223846793005UL;
    const ulong C    = 1442695040888963407UL;
    const ulong SEED = 0x123456789ABCDEF0UL;

    while (!ct.IsCancellationRequested)
    {
        // Re-seed every block so a one-time desync doesn't silently mask later glitches.
        ulong a = SEED, b = SEED, c = SEED, d = SEED;

        // 1024 identical LCG+rotate-XOR steps on four independent register sets.
        // The JIT keeps the four chains on distinct physical registers and dispatches
        // their IMUL/ROL/XOR uops in parallel across the integer ports, so a port-local
        // glitch typically affects only one chain.
        for (int i = 0; i < 1024; i++)
        {
            a = unchecked(a * K + C); a = BitOperations.RotateLeft(a, 17) ^ (a >> 31);
            b = unchecked(b * K + C); b = BitOperations.RotateLeft(b, 17) ^ (b >> 31);
            c = unchecked(c * K + C); c = BitOperations.RotateLeft(c, 17) ^ (c >> 31);
            d = unchecked(d * K + C); d = BitOperations.RotateLeft(d, 17) ^ (d >> 31);
        }
        blocks++;

        if (a != b || a != c || a != d)
        {
            mismatches++;
            Console.WriteLine();
            Console.WriteLine($"[Core {coreIndex}] *** SILENT MISMATCH #{mismatches} @ block {blocks} ***");
            Console.WriteLine($"    a=0x{a:X16}  b=0x{b:X16}");
            Console.WriteLine($"    c=0x{c:X16}  d=0x{d:X16}");
        }

        if ((blocks & 0x3F) == 0)
        {
            buf[(int)((blocks >> 6) & (buf.Length - 1))] += (double)(a & 0x7FFFFFFFUL);

            if (blocks % 1000 == 0)
                Console.Write($"\r[Core {coreIndex}] VERIFY — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {blocks} blocks, {mismatches} mismatches");
        }
    }
}

// ── mode: HERMES ───────────────────────────────────────────────────────────

// Direct simulation of the hermes.dll interpreter inner loop.  Combines the four
// instruction patterns Hermes hammers per opcode dispatch, in the exact order Hermes
// emits them, on a single dependency chain:
//
//   1. LOAD     — load a 64-bit NaN-boxed value from the JS value stack (`mov rax,[rcx]`).
//   2. MOVQ↔    — bitcast the value GPR→XMM and back (`vmovq xmm,rax; vmovq rax,xmm`).
//                 Hermes does this on every value-as-double test.  XMM↔GPR bypass is the
//                 dominant pattern not exercised by any prior mode (Mixed uses CVTSI2SD,
//                 which is conversion — different uops, different ports).
//   3. AND      — mask off the NaN-box tag (`and rax, PTR_MASK`).  This is where Hermes
//                 extracts the 48-bit pointer.  A glitch here produces a noncanonical
//                 address — and on x86, dereferencing a noncanonical address is #GP, which
//                 surfaces as AV (0xC0000005) just like in real Hermes crashes.
//   4. CALL[r]  — indirect call through the opcode-handler table (`call qword ptr [rdx+rax*8]`).
//   5. LOAD     — chase the extracted pointer (`mov rcx,[rax]`).
//
// All five happen on one dependency chain in a tight unrolled loop, so the core is forced
// to issue them back-to-back — no ILP relief and no port slack.
static unsafe void StressLoopHermes(int coreIndex, CancellationToken ct)
{
    const int N = 1 << 18;                                  // 256 K slots × 8 B = 2 MiB (fits in L2)
    byte* region = (byte*)NativeMemory.AlignedAlloc((nuint)N * 8, 64);

    // Reuse the same 8 handlers as Dispatch; here we tile only the low 4 indices.
    const int HandlerCount = 4;
    var handlers = (delegate*<long, long>*)NativeMemory.Alloc((nuint)HandlerCount * (nuint)sizeof(nint));
    handlers[0] = &H_Add;
    handlers[1] = &H_Xor;
    handlers[2] = &H_Mul;
    handlers[3] = &H_Mix;

    try
    {
        long* slots = (long*)region;

        var rng = new Random(unchecked(0x0ABBA + coreIndex * 7919));
        int[] perm = new int[N];
        for (int i = 0; i < N; i++) perm[i] = i;
        for (int i = N - 1; i > 0; i--)
        {
            int j = rng.Next(i);
            (perm[i], perm[j]) = (perm[j], perm[i]);
        }

        // QNAN_TAG mimics the Hermes encoding: bits 49-63 set ⇒ "this is a tagged box".
        // PTR_MASK gives the 48-bit user-space pointer payload.
        const long QNAN_TAG = unchecked((long)0xFFFE_0000_0000_0000UL);
        const long PTR_MASK =          0x0000_FFFF_FFFF_FFFFL;

        // Each slot is the next slot's address NaN-boxed as a Hermes pointer.
        for (int i = 0; i < N; i++)
            slots[i] = (long)(region + (long)perm[i] * 8L) | QNAN_TAG;

        long state = (long)slots;                           // current slot pointer
        long acc   = 0;
        var sw = Stopwatch.StartNew();
        long iterations = 0;
        double[] buf = new double[512 * 1024];

        while (!ct.IsCancellationRequested)
        {
            // 4× unrolled.  Each unrolled body is one full Hermes interpreter step shape.
            for (int u = 0; u < 4; u++)
            {
                long raw = *(long*)state;                           // LOAD: tagged value

                // MOVQ bounce: bits cross GPR→XMM→GPR.  This is the Hermes NaN test path
                // (load value into XMM, test if it's a finite double).  Round-trip is
                // bit-preserving by IEEE-754, so `bits == raw` on healthy silicon.
                double dv = BitConverter.Int64BitsToDouble(raw);
                long bits = BitConverter.DoubleToInt64Bits(dv);

                // Extract the 48-bit pointer payload by AND-masking the NaN tag.
                // A glitch in the AND on bits 12-47 produces a wild pointer → next *(long*)state faults.
                long ptr = bits & PTR_MASK;

                // Indirect dispatch on a few low bits — Hermes opcode jump-table.
                // The selector also depends on `bits`, so the indirect target is data-dependent.
                acc = handlers[(int)(bits & (HandlerCount - 1))](acc ^ ptr);

                state = ptr;                                        // CHASE
            }
            iterations++;

            if ((iterations & 0xFF) == 0)
            {
                buf[(int)((iterations >> 8) & (buf.Length - 1))] += (double)(acc & 0x7FFFFFFFL);

                if (iterations % 5_000_000 == 0)
                    Console.Write($"\r[Core {coreIndex}] HERMES — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {iterations * 4 / 1_000_000}M ops");
            }
        }
    }
    finally
    {
        NativeMemory.Free(handlers);
        NativeMemory.AlignedFree(region);
    }
}

// ── mode: ATOMIC ───────────────────────────────────────────────────────────

// LOCK-prefixed atomic operations.  Every Hermes GC safepoint and every Unity Mono
// reference-count bump emits `LOCK CMPXCHG` / `LOCK XADD` / `XCHG` — a uniquely
// microcoded path that bridges the load/store unit with the cache-coherence protocol.
// LOCK atomics historically expose the most subtle Errata in degraded x86 silicon
// because their uop sequence touches the entire memory subsystem in a single retire.
static unsafe void StressLoopAtomic(int coreIndex, CancellationToken ct)
{
    // 8 longs, one per 64-byte cache line — uncontended within a single worker,
    // but each Interlocked op still asserts LOCK on the bus / coherence fabric.
    const int LineCount = 8;
    long* counters = (long*)NativeMemory.AlignedAlloc((nuint)(LineCount * 64), 64);
    for (int i = 0; i < LineCount; i++) counters[i * 8] = 0;

    try
    {
        var sw = Stopwatch.StartNew();
        long iterations = 0;
        double[] buf = new double[512 * 1024];

        while (!ct.IsCancellationRequested)
        {
            // 8 LOCK-prefixed ops per iteration spread across 8 distinct cache lines.
            // Mix of XADD (Increment, Add), XCHG (Exchange), and CMPXCHG (CompareExchange)
            // to exercise every variant of the atomic microcode sequencer.
            long v0 = Interlocked.Increment(ref *(counters + 0 * 8));
            long v1 = Interlocked.Add(ref *(counters + 1 * 8), 7);
            long v2 = Interlocked.Exchange(ref *(counters + 2 * 8), v0);
            long v3 = Interlocked.CompareExchange(ref *(counters + 3 * 8), v1, v2);
            long v4 = Interlocked.Increment(ref *(counters + 4 * 8));
            long v5 = Interlocked.Add(ref *(counters + 5 * 8), 11);
            long v6 = Interlocked.Exchange(ref *(counters + 6 * 8), v4);
            long v7 = Interlocked.CompareExchange(ref *(counters + 7 * 8), v5, v6);

            iterations++;
            if ((iterations & 0xFF) == 0)
            {
                buf[(int)((iterations >> 8) & (buf.Length - 1))] += (double)((v7 ^ v3) & 0x7FFFFFFFL);

                if (iterations % 5_000_000 == 0)
                    Console.Write($"\r[Core {coreIndex}] ATOMIC — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {iterations * 8 / 1_000_000}M atomics");
            }
        }
    }
    finally
    {
        NativeMemory.AlignedFree(counters);
    }
}

// ── mode: MEMCPY ───────────────────────────────────────────────────────────

// REP MOVSB / SIMD-vector / scalar memcpy paths.  Buffer.MemoryCopy dispatches on size:
// small copies inline as scalar+SIMD; medium copies emit AVX vector loops; large copies
// (with ERMSB) emit pure `REP MOVSB`.  All three paths share the load/store unit but
// stress the microcode sequencer (`REP MOVSB`), the AVX vector pipes, and the unaligned
// load fast-path differently.  Hermes string ops and Unity managed-array copies are the
// dominant emitters of these paths in real applications.
static unsafe void StressLoopMemcpy(int coreIndex, CancellationToken ct)
{
    const int BufSize = 64 * 1024;                          // L1d-resident — pure copy bandwidth, no L2 stall
    byte* src = (byte*)NativeMemory.AlignedAlloc(BufSize, 64);
    byte* dst = (byte*)NativeMemory.AlignedAlloc(BufSize, 64);

    for (int i = 0; i < BufSize; i++)
    {
        src[i] = (byte)(i ^ coreIndex);
        dst[i] = 0;
    }

    // Sizes chosen to hit every internal copy path:
    //   7-63    : scalar/SSE tail
    //   97-257  : AVX vector loop
    //   511-4093: AVX + REP MOVSB tail
    //   8191+   : pure REP MOVSB (ERMSB) on modern x86.
    int[] sizes   = [ 7, 13, 23, 31, 47, 63, 97, 127, 193, 257, 511, 1023, 2047, 4093, 8191, 16383 ];
    int[] offsets = [ 0, 1, 2, 3, 5, 7, 11, 13 ];           // unaligned src/dst — split-line stress

    try
    {
        var sw = Stopwatch.StartNew();
        long iterations = 0;
        int sizeIdx = 0, offIdx = 0;

        while (!ct.IsCancellationRequested)
        {
            // 256 copies per progress check — keep the load/store unit saturated.
            for (int batch = 0; batch < 256; batch++)
            {
                int sz = sizes[sizeIdx & 15];
                int oS = offsets[offIdx & 7];
                int oD = offsets[(offIdx + 3) & 7];
                Buffer.MemoryCopy(src + oS, dst + oD, BufSize - oD, sz);
                Buffer.MemoryCopy(dst + oD, src + oS, BufSize - oS, sz);
                sizeIdx++; offIdx++;
            }
            iterations++;

            if (iterations % 100_000 == 0)
                Console.Write($"\r[Core {coreIndex}] MEMCPY — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {iterations / 1_000}K batches");
        }
    }
    finally
    {
        NativeMemory.AlignedFree(src);
        NativeMemory.AlignedFree(dst);
    }
}

// ── mode: BMI ──────────────────────────────────────────────────────────────

// BMI1 / BMI2 / LZCNT instructions exercise a distinct execution unit (the "bit
// manipulation port", typically port 1 on Intel) with microcode entirely separate from
// regular ALU ops.  PDEP/PEXT in particular are notoriously slow microcoded sequences
// on older silicon and known to fail timing on degraded Raptor Lake cores.  Falls back
// to INT mode if the CPU lacks BMI support (pre-Haswell).
static void StressLoopBmi(int coreIndex, CancellationToken ct)
{
    if (!Bmi1.X64.IsSupported || !Bmi2.X64.IsSupported || !Lzcnt.X64.IsSupported)
    {
        Console.WriteLine($"[Core {coreIndex}] BMI1/BMI2/LZCNT not supported — falling back to INT.");
        StressLoopInt(coreIndex, ct);
        return;
    }

    var sw = Stopwatch.StartNew();
    long iterations = 0;
    double[] buf = new double[512 * 1024];

    ulong x0 = 0x123456789ABCDEF0UL, x1 = 0x23456789ABCDEF01UL;
    ulong x2 = 0x3456789ABCDEF012UL, x3 = 0x456789ABCDEF0123UL;
    ulong x4 = 0x56789ABCDEF01234UL, x5 = 0x6789ABCDEF012345UL;
    ulong x6 = 0x789ABCDEF0123456UL, x7 = 0x89ABCDEF01234567UL;

    const ulong MASK = 0xDEAD_BEEF_CAFE_BABEUL;

    while (!ct.IsCancellationRequested)
    {
        // 8 chains, each hitting a different BMI/LZCNT instruction every iteration.
        x0 = Bmi2.X64.ParallelBitDeposit(x0, MASK);                     // PDEP   — microcoded scatter
        x1 = Bmi2.X64.ParallelBitExtract(x1, MASK);                     // PEXT   — microcoded gather
        x2 = Bmi1.X64.AndNot(x2, MASK);                                 // ANDN   — single uop
        x3 = Bmi1.X64.TrailingZeroCount(x3 | 1UL);                      // TZCNT
        x4 = Bmi1.X64.ExtractLowestSetBit(x4 | 1UL);                    // BLSI
        x5 = Bmi2.X64.MultiplyNoFlags(x5, 6364136223846793005UL);       // MULX   — multiply, no flags
        x6 = BitOperations.RotateRight(x6, 17);                          // RORX   (JIT-emitted under BMI2)
        x7 = Lzcnt.X64.LeadingZeroCount(x7 | 1UL);                      // LZCNT

        // Cross-pollinate to keep the chains data-dependent and prevent the JIT from
        // schedulling them all in parallel on the same port cycle.
        x0 ^= x1; x1 ^= x2; x2 ^= x3; x3 ^= x4;
        x4 ^= x5; x5 ^= x6; x6 ^= x7; x7 ^= x0;

        iterations++;
        if ((iterations & 0xFF) == 0)
        {
            buf[(int)((iterations >> 8) & (buf.Length - 1))] += (double)(x0 & 0x7FFFFFFFUL);

            if (iterations % 50_000_000 == 0)
                Console.Write($"\r[Core {coreIndex}] BMI — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {iterations / 1_000_000}M iterations");
        }
    }
}

// ── mode: IDX-CHASE ────────────────────────────────────────────────────────

// Direct replica of the actual hermes.dll faulting instruction observed in real-world
// crashes on this faulty CPU:
//
//     mov  ecx, dword ptr [rdx + rcx*4 + 8]      ; 8B 4C 8A 08
//
// Three things distinguish this from every other mode and from real Hermes code:
//
//   1. 32-bit indexed load at SIB scale 4 — `MOV r32, [base + r32*4 + disp]`.
//      The C# JIT emits exactly this opcode for `uint result = uintPtr[idx]`.
//   2. Tiny 4 KiB table backed by VirtualAlloc with reservation rounded to 64 KiB
//      granularity.  The page after the table is reserved-but-uncommitted — a single
//      read past `[0, 1024)` faults immediately with AV (0xC0000005), the exact
//      Hermes crash shape.
//   3. No intermediate ALU between chases — pure load→index chain.  If the load
//      itself glitches OR the AGU's `base + idx*4 + disp` computation glitches OR
//      the previous chase's destination zero-extension corrupts the upper 32 bits
//      of RCX, the next iteration's instruction faults instantly.
//
// On a healthy core the Sattolo cycle keeps `idx` in [0, 1024) forever; on a faulty
// core, any single-bit corruption in bits 10-31 of the index → AV.
static unsafe void StressLoopIdxChase(int coreIndex, CancellationToken ct)
{
    const int  N             = 1024;                        // 1024 × 4 B = 4 KiB = 1 page
    const uint MEM_COMMIT    = 0x1000;
    const uint MEM_RESERVE   = 0x2000;
    const uint MEM_RELEASE   = 0x8000;
    const uint PAGE_READWRITE = 0x04;

    // VirtualAlloc reserves 64 KiB (granularity) and commits only 4 KiB.  The
    // remaining 60 KiB is reserved-but-uncommitted ⇒ reading it faults.  This is
    // what makes a small index corruption into a guaranteed AV.
    uint* table = (uint*)VirtualAlloc(null, (nuint)N * 4, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (table == null)
        throw new InvalidOperationException($"VirtualAlloc failed: Win32 error {Marshal.GetLastWin32Error()}");

    try
    {
        // Sattolo shuffle — produces a single cycle of length N through the table.
        // Each cell stores the index of the next cell in the cycle.
        var rng = new Random(unchecked(0x0CAFE + coreIndex * 7919));
        int[] perm = new int[N];
        for (int i = 0; i < N; i++) perm[i] = i;
        for (int i = N - 1; i > 0; i--)
        {
            int j = rng.Next(i);
            (perm[i], perm[j]) = (perm[j], perm[i]);
        }
        for (int i = 0; i < N; i++) table[i] = (uint)perm[i];

        uint idx = 0;
        var sw = Stopwatch.StartNew();
        long iterations = 0;
        double[] buf = new double[512 * 1024];

        while (!ct.IsCancellationRequested)
        {
            // 8× unrolled.  Each statement compiles to the exact 4-byte opcode
            //     8B 0C 8A    mov ecx, dword ptr [rdx + rcx*4]
            // (matching the hermes.dll fault — minus the +8 disp, which doesn't matter
            // because the SIB calculation pipeline is identical).
            idx = table[idx];
            idx = table[idx];
            idx = table[idx];
            idx = table[idx];
            idx = table[idx];
            idx = table[idx];
            idx = table[idx];
            idx = table[idx];
            iterations++;

            if ((iterations & 0xFF) == 0)
            {
                buf[(int)((iterations >> 8) & (buf.Length - 1))] += (double)idx;

                if (iterations % 5_000_000 == 0)
                    Console.Write($"\r[Core {coreIndex}] IDX-CHASE — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {iterations * 8 / 1_000_000}M chases");
            }
        }
    }
    finally
    {
        VirtualFree(table, 0, MEM_RELEASE);
    }
}

// VirtualAlloc gives us a region whose adjacent (reserved-but-uncommitted) pages
// fault on any read — essential for catching small-magnitude index corruption.
// NativeMemory.AlignedAlloc backs onto the C heap, whose adjacent pages may be
// mapped (and thus would silently absorb the corruption instead of crashing).
[DllImport("kernel32.dll", SetLastError = true)]
static extern unsafe void* VirtualAlloc(void* lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern unsafe bool VirtualFree(void* lpAddress, nuint dwSize, uint dwFreeType);

// ── mode: IDX-CALL ─────────────────────────────────────────────────────────

// Combines the IDX-CHASE pattern with deep CALL/RET recursion — the precise shape
// observed in the real hermes.dll crash dump's call stack.  The dump showed:
//
//   * Crash at `mov ecx, [rdx + rcx*4 + 8]` — scale-4 indexed load (covered by IdxChase).
//   * ~200 nested frames of the same handler recursing into itself — Hermes interprets
//     bytecode via genuine C++ function recursion, not threaded dispatch, so every JS
//     call/return is a real CALL/RET pair on the native stack.
//   * The recursion repeatedly hit a small ring of return addresses (~ 6 distinct sites),
//     and each frame did an indexed load *both before* recursing into the child and
//     *after* the child returned.
//
// This mode replicates that exact shape: depth-128 NoInlining recursion, each level doing
// one scale-4 indexed load on the way down and one on the way back up.  The recursion
// depth (128) comfortably exceeds the Raptor Lake RSB (16-24 entries), so every RET on
// the way up is a mispredict that must be recovered via the indirect predictor — the
// front-end is under sustained pressure for the entire chain, simultaneously with the
// load/store unit doing the scale-4 chases.
static unsafe void StressLoopIdxCall(int coreIndex, CancellationToken ct)
{
    const int  N             = 1024;                        // 1024 × 4 B = 4 KiB = 1 page
    const uint MEM_COMMIT    = 0x1000;
    const uint MEM_RESERVE   = 0x2000;
    const uint MEM_RELEASE   = 0x8000;
    const uint PAGE_READWRITE = 0x04;

    uint* table = (uint*)VirtualAlloc(null, (nuint)N * 4, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (table == null)
        throw new InvalidOperationException($"VirtualAlloc failed: Win32 error {Marshal.GetLastWin32Error()}");

    try
    {
        var rng = new Random(unchecked(0x0DEAD + coreIndex * 7919));
        int[] perm = new int[N];
        for (int i = 0; i < N; i++) perm[i] = i;
        for (int i = N - 1; i > 0; i--)
        {
            int j = rng.Next(i);
            (perm[i], perm[j]) = (perm[j], perm[i]);
        }
        for (int i = 0; i < N; i++) table[i] = (uint)perm[i];

        // 128 frames ≫ RSB depth (16-24).  Real Hermes recursion at crash time was ~200
        // deep, but our C# frames are smaller; 128 is enough to fully exhaust the RSB and
        // saturate the indirect-target predictor on the way back up.
        const int CallDepth = 128;

        var sw = Stopwatch.StartNew();
        long iterations = 0;
        double[] buf = new double[512 * 1024];
        uint idx = 0;

        while (!ct.IsCancellationRequested)
        {
            idx = RecurseIdxCall(table, idx, CallDepth);
            iterations++;

            if ((iterations & 0xFF) == 0)
            {
                buf[(int)((iterations >> 8) & (buf.Length - 1))] += (double)idx;

                if (iterations % 1_000_000 == 0)
                    Console.Write($"\r[Core {coreIndex}] IDX-CALL — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {iterations * CallDepth * 2 / 1_000_000}M loads ({iterations / 1_000_000}M depth-{CallDepth} recursions)");
            }
        }
    }
    finally
    {
        VirtualFree(table, 0, MEM_RELEASE);
    }
}

// NoInlining forces real CALL/RET pairs at every level (the JIT cannot collapse the
// chain into a loop).  The post-call `table[inner]` ensures the recursive call is not
// in tail position, blocking tail-call optimisation and keeping the full return chain
// intact — every frame must perform a CALL on the way down and a RET on the way up.
[MethodImpl(MethodImplOptions.NoInlining)]
static unsafe uint RecurseIdxCall(uint* table, uint idx, int depth)
{
    // CALL-down side: scale-4 indexed load.  C# JIT emits `mov ecx, [rax + rcx*4]`,
    // matching the hermes.dll faulting instruction's pattern exactly.
    idx = table[idx];

    if (depth == 0)
        return idx;

    uint inner = RecurseIdxCall(table, idx, depth - 1);

    // RET-up side: second scale-4 indexed load whose index comes from the child's
    // return value (in EAX after the RET).  If anything between this frame's CALL and
    // its child's matching RET corrupts the propagated value, this load AVs immediately.
    return table[inner];
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

enum StressMode { Fma, Int, Branch, Call, Threads, Mixed, Divide, PointerChase, Dispatch, Bursty, Verify, Hermes, Atomic, Memcpy, Bmi, IdxChase, IdxCall, Rotate }
