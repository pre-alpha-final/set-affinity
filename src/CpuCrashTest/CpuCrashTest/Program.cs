using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

const string workerFlag = "--worker";
const string modeFlag   = "--mode";

// Parse --mode from anywhere in args (e.g. --worker 3 --mode deepidle).
StressMode mode = ParseMode(GetArg(args, modeFlag));

if (args.Length >= 2 && args[0] == workerFlag && int.TryParse(args[1], out int coreIndex))
{
    RunWorker(coreIndex, mode);
}
else if (mode == StressMode.External)
{
    RunExternalSweep(args);
}
else
{
    RunLauncher(mode);
}

static string? GetArg(string[] args, string flag)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == flag) return args[i + 1];
    return null;
}

// ── helpers ────────────────────────────────────────────────────────────────

static StressMode ParseMode(string? s) => s?.ToLowerInvariant() switch
{
    "idxcall"      => StressMode.IdxCall,
    "deepidle"     => StressMode.DeepIdle,
    "idle"         => StressMode.DeepIdle,
    "external"     => StressMode.External,
    "app"          => StressMode.External,
    "rotate"       => StressMode.Rotate,
    _              => StressMode.Rotate,
};

static string ModeLabel(StressMode m) => m switch
{
    StressMode.IdxCall      => "IDX-CALL",
    StressMode.DeepIdle     => "DEEP-IDLE",
    StressMode.External     => "EXTERNAL-APP",
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

// ── external-app launcher ──────────────────────────────────────────────────

// Mode `external` spawns a *real* binary (whatever the user supplies via --exec) pinned
// to each candidate core and watches whether it crashes with 0xC0000005 within --timeout
// seconds.  Bypasses the impossible task of guessing what microarchitectural state our
// synthetic loops are missing — uses the actual failure symptom directly.
//
// Usage:
//   CpuCrashTest.exe --mode external --exec node.exe --args "-e \"while(true){var x={};for(var i=0;i<1000;i++)x['p'+i]=i;}\"" --cores 4,6 --timeout 30
//
// Each candidate is spawned in parallel (one process per core, each pinned to its own
// LP via SetProcessAffinityMask), and we wait up to --timeout per core.  Any process
// that exits with a Windows status code in 0xC0000000..0xCFFFFFFF is flagged as a
// crash on that core.  A process that survives the timeout is killed and the core is
// reported as healthy *for this workload*.
static void RunExternalSweep(string[] args)
{
    string? exec       = GetArg(args, "--exec");
    string  argList    = GetArg(args, "--args") ?? "";
    string? coresArg   = GetArg(args, "--cores");
    int     timeoutSec = int.TryParse(GetArg(args, "--timeout"), out int t) ? t : 30;

    if (string.IsNullOrWhiteSpace(exec))
    {
        Console.WriteLine("ERROR: --mode external requires --exec <path-to-binary>");
        Console.WriteLine();
        Console.WriteLine("Example:");
        Console.WriteLine("  CpuCrashTest.exe --mode external --exec node.exe \\");
        Console.WriteLine("    --args \"-e \\\"while(true){var x={};for(var i=0;i<1000;i++)x['p'+i]=i;}\\\"\" \\");
        Console.WriteLine("    --cores 4,6 --timeout 30");
        Console.WriteLine();
        Console.WriteLine("Optional flags:");
        Console.WriteLine("  --cores N[,N...]   subset of logical CPUs to test (default: all)");
        Console.WriteLine("  --timeout SECONDS  per-core timeout (default: 30)");
        return;
    }

    int coreCount = int.TryParse(Environment.GetEnvironmentVariable("NUMBER_OF_PROCESSORS"), out int envCount)
        ? envCount
        : Environment.ProcessorCount;

    int[] cores;
    if (string.IsNullOrWhiteSpace(coresArg))
    {
        cores = Enumerable.Range(0, coreCount).ToArray();
    }
    else
    {
        try
        {
            cores = coresArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(int.Parse)
                            .ToArray();
        }
        catch
        {
            Console.WriteLine($"ERROR: --cores must be a comma-separated list of integers (got: {coresArg})");
            return;
        }
    }

    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine("╔══════════════════════════════════╗");
    Console.WriteLine("║    EXTERNAL-APP CRASH SWEEP      ║");
    Console.WriteLine("╚══════════════════════════════════╝");
    Console.WriteLine();
    Console.WriteLine($"  exec    : {exec}");
    Console.WriteLine($"  args    : {(string.IsNullOrEmpty(argList) ? "(none)" : argList)}");
    Console.WriteLine($"  timeout : {timeoutSec}s per core");
    Console.WriteLine($"  cores   : {string.Join(", ", cores)}");
    Console.WriteLine();
    Console.WriteLine("Launching one process per core in parallel...");
    Console.WriteLine();

    var results = new (int core, string result)[cores.Length];
    var tasks = new Task[cores.Length];
    for (int i = 0; i < cores.Length; i++)
    {
        int slot = i;
        int core = cores[i];
        tasks[i] = Task.Run(() => results[slot] = (core, TestCoreWithExternal(exec, argList, core, timeoutSec)));
    }
    Task.WaitAll(tasks);

    Console.WriteLine();
    Console.WriteLine("══════════════════════════════════════════════════");
    Console.WriteLine("Results (cores reporting CRASH are suspect):");
    Console.WriteLine("══════════════════════════════════════════════════");
    foreach (var r in results.OrderBy(r => r.core))
        Console.WriteLine($"  Core {r.core,3}:  {r.result}");
}

// Spawns the external binary, pins it to `core`, waits up to `timeoutSec` for it to
// exit.  Returns a human-readable result line.  Treats any Windows status code with
// the high bit set (0x80000000+ — STATUS_SEVERITY_WARNING/ERROR/FATAL) as a crash.
static string TestCoreWithExternal(string exec, string argList, int core, int timeoutSec)
{
    var psi = new ProcessStartInfo
    {
        FileName               = exec,
        Arguments              = argList,
        UseShellExecute        = false,
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        CreateNoWindow         = true,
    };

    Process? proc;
    try
    {
        proc = Process.Start(psi);
        if (proc is null) return "FAILED to start process";
    }
    catch (Exception ex)
    {
        return $"FAILED to start: {ex.Message}";
    }

    // Drain stdout/stderr in background to avoid the pipe filling up and blocking the
    // child.  We don't actually use the output — we only care about exit code.
    _ = Task.Run(() => { try { proc.StandardOutput.ReadToEnd(); } catch { } });
    _ = Task.Run(() => { try { proc.StandardError .ReadToEnd(); } catch { } });

    try
    {
        proc.ProcessorAffinity = (nint)(1L << core);
    }
    catch (Exception ex)
    {
        try { proc.Kill(); } catch { }
        return $"FAILED to set affinity to core {core}: {ex.Message}";
    }

    var sw = Stopwatch.StartNew();
    bool exited = proc.WaitForExit(timeoutSec * 1000);
    sw.Stop();

    if (!exited)
    {
        try { proc.Kill(entireProcessTree: true); proc.WaitForExit(2000); } catch { }
        return $"OK  — survived {timeoutSec}s, killed";
    }

    int  code   = proc.ExitCode;
    uint ucode  = unchecked((uint)code);
    proc.Dispose();

    // NT status codes: high bit set = error/warning.  0xC0000005 = STATUS_ACCESS_VIOLATION
    // is the headline; we also call out a few other common silicon-fault signatures.
    string sigName = ucode switch
    {
        0xC0000005 => "ACCESS_VIOLATION",
        0xC000001D => "ILLEGAL_INSTRUCTION",
        0xC0000094 => "INTEGER_DIVIDE_BY_ZERO",
        0xC0000096 => "PRIVILEGED_INSTRUCTION",
        0xC00000FD => "STACK_OVERFLOW",
        0xC0000409 => "STACK_BUFFER_OVERRUN",
        0xC0000417 => "INVALID_CRUNTIME_PARAMETER",
        _          => "",
    };

    if ((ucode & 0x80000000) != 0)
    {
        string sig = sigName.Length > 0 ? $" [{sigName}]" : "";
        return $"*** CRASH 0x{ucode:X8}{sig} after {sw.Elapsed.TotalSeconds:F1}s — CORE SUSPECT";
    }
    if (code != 0)
    {
        return $"exited with non-zero code {code} after {sw.Elapsed.TotalSeconds:F1}s";
    }
    return $"clean exit (code 0) after {sw.Elapsed.TotalSeconds:F1}s — workload finished before timeout, use a longer-running --args";
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
        // Process affinity applies retroactively to all existing and future threads in
        // the process (GC, JIT, finalizer, etc.) — so a single assignment here is enough.
        Process.GetCurrentProcess().ProcessorAffinity = (nint)(1L << coreIndex);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Core {coreIndex}] WARNING: Could not set affinity — {ex.Message}");
    }

    // Verify the pinning actually took effect.  Task Manager's "CPU N" numbering should
    // match GetCurrentProcessorNumber on the same logical CPU, but we sample a few times
    // across short sleeps because the very first sample can race with the affinity
    // assignment above (the thread may run one quantum on its previous CPU before being
    // migrated).  If any later sample disagrees with coreIndex, something is wrong:
    //   * On systems with >64 logical CPUs split across processor groups, ProcessorAffinity
    //     only addresses the current group — bits beyond group 0 are silently ignored.
    //   * `1L << coreIndex` wraps modulo 64 for coreIndex >= 64, silently selecting the
    //     wrong logical CPU.
    //   * Some virtualisation / security software hooks SetProcessAffinityMask and
    //     turns it into a no-op.
    Thread.Sleep(1);
    int observed = -1;
    for (int i = 0; i < 16; i++)
    {
        observed = (int)GetCurrentProcessorNumber();
        if (observed == coreIndex) break;
        Thread.Sleep(2);
    }

    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine("╔══════════════════════════════════╗");
    Console.WriteLine($"║   CPU CRASH TEST  —  Core: {coreIndex,-5} ║");
    Console.WriteLine("╚══════════════════════════════════╝");
    Console.WriteLine();
    Console.WriteLine($"Mode: {ModeLabel(mode)}");
    if (observed == coreIndex)
    {
        Console.WriteLine($"Affinity OK: running on logical CPU {observed} as requested.");
    }
    else
    {
        Console.WriteLine($"*** AFFINITY MISMATCH ***  requested CPU {coreIndex}, observed CPU {observed}");
        Console.WriteLine($"    Task Manager's 'CPU {coreIndex}' may not be the same logical CPU the kernel sees.");
        Console.WriteLine($"    Check: processor groups, >64 LP systems, or affinity-blocking software.");
    }
    Console.WriteLine($"Stressing core {coreIndex}. Press Ctrl+C to stop.");
    Console.WriteLine();

    StressDispatcher(coreIndex, mode, cts.Token);

    Console.WriteLine();
    Console.WriteLine($"[Core {coreIndex}] Stopped.");
}

// ── dispatcher ─────────────────────────────────────────────────────────────

// Runs the selected mode, or rotates through DeepIdle and IdxCall every 30 s.
static void StressDispatcher(int coreIndex, StressMode mode, CancellationToken ct)
{
    if (mode != StressMode.Rotate)
    {
        RunMode(coreIndex, mode, ct);
        return;
    }

    // Earlier modes (FMA / INT / BRANCH / POINTERCHASE / DISPATCH / BURSTY / VERIFY /
    // HERMES / ATOMIC / MEMCPY / BMI / CALL / MIXED / DIVIDE / THREADS / IDXCHASE) were
    // removed because none of them reproduced any AVs in overnight runs on cores
    // independently confirmed as faulty via the EXTERNAL-APP mode.  Only the two
    // surviving modes appear in the rotation:
    //   * DEEP-IDLE — exercises the C-state wake-up Vmin transition, the canonical
    //     Raptor Lake silicon-degradation failure path.
    //   * IDX-CALL  — mimics the exact faulting instruction observed in real
    //     hermes.dll crashes, inside the deep CALL/RET chain seen in the crash dump.
    StressMode[] rotation = [
        StressMode.DeepIdle, StressMode.IdxCall,
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
        case StressMode.IdxCall:  StressLoopIdxCall(coreIndex, ct);  break;
        case StressMode.DeepIdle: StressLoopDeepIdle(coreIndex, ct); break;
        default:                  StressLoopDeepIdle(coreIndex, ct); break;
    }
}

// ── kernel32 P/Invokes ─────────────────────────────────────────────────────

// VirtualAlloc gives us a region whose adjacent (reserved-but-uncommitted) pages
// fault on any read — essential for catching small-magnitude index corruption.
// NativeMemory.AlignedAlloc backs onto the C heap, whose adjacent pages may be
// mapped (and thus would silently absorb the corruption instead of crashing).
[DllImport("kernel32.dll", SetLastError = true)]
static extern unsafe void* VirtualAlloc(void* lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern unsafe bool VirtualFree(void* lpAddress, nuint dwSize, uint dwFreeType);

// Returns the *logical* CPU number (0..63 within the current processor group) the calling
// thread is currently running on.  Used to verify ProcessorAffinity actually pinned us.
[DllImport("kernel32.dll")]
static extern uint GetCurrentProcessorNumber();

// ── mode: IDX-CALL ─────────────────────────────────────────────────────────

// Replicates the exact shape of the real hermes.dll crash dump's call stack:
//
//   * Crash at `mov ecx, [rdx + rcx*4 + 8]` — scale-4 indexed load.  The C# JIT emits
//     `mov ecx, [rax + rcx*4]` for `uint result = uintPtr[idx]`, matching the SIB
//     calculation pipeline of the hermes.dll fault exactly.
//   * ~200 nested frames of the same handler recursing into itself — Hermes interprets
//     bytecode via genuine C++ function recursion, not threaded dispatch, so every JS
//     call/return is a real CALL/RET pair on the native stack.
//   * The recursion repeatedly hit a small ring of return addresses (~ 6 distinct sites),
//     and each frame did an indexed load *both before* recursing into the child and
//     *after* the child returned.
//
// Depth-128 NoInlining recursion exceeds the Raptor Lake RSB (16-24 entries), so every
// RET on the way up is a mispredict recovered via the indirect predictor — the front-end
// is under sustained pressure for the entire chain, simultaneously with the load/store
// unit doing the scale-4 chases.  The table is 4 KiB committed within a 64 KiB
// reservation, so any small-magnitude index corruption AVs immediately rather than
// silently absorbing into adjacent heap pages.
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
        // Sattolo shuffle — produces a single cycle of length N through the table.
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

// ── mode: DEEP-IDLE ────────────────────────────────────────────────────────

// Targets the *Raptor Lake Vmin-shift transition* directly rather than steady-state
// stress.  Hypothesis: degraded RPL silicon doesn't fail under sustained load — it fails
// in the first ~µs after waking from C6/C7 idle, when the voltage applied is too low for
// the degraded transistors and the very first instructions produce wrong results.
//
// Real apps (XboxPcApp, animations, browsers) are mostly idle: they wake at 60 FPS
// to render a frame, then sleep ~15 ms.  Previous synthetic modes kept the core 100%
// busy at nominal voltage and never exercised the transition — none of them reproduced
// any AV in overnight runs on cores known-bad by EXTERNAL-APP testing, which is why
// they were removed.  This mode reproduces the real duty cycle: long Sleep (Windows
// demotes the LP to C6 after ~70 ms idle on the default power plan) followed by an
// immediate IdxCall burst, so the suspect scale-4 indexed load lands within
// microseconds of wake-up.
static unsafe void StressLoopDeepIdle(int coreIndex, CancellationToken ct)
{
    const int  N             = 1024;
    const uint MEM_COMMIT    = 0x1000;
    const uint MEM_RESERVE   = 0x2000;
    const uint MEM_RELEASE   = 0x8000;
    const uint PAGE_READWRITE = 0x04;

    uint* table = (uint*)VirtualAlloc(null, (nuint)N * 4, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (table == null)
        throw new InvalidOperationException($"VirtualAlloc failed: Win32 error {Marshal.GetLastWin32Error()}");

    try
    {
        var rng = new Random(unchecked(0x1DEAD + coreIndex * 7919));
        int[] perm = new int[N];
        for (int i = 0; i < N; i++) perm[i] = i;
        for (int i = N - 1; i > 0; i--)
        {
            int j = rng.Next(i);
            (perm[i], perm[j]) = (perm[j], perm[i]);
        }
        for (int i = 0; i < N; i++) table[i] = (uint)perm[i];

        const int IdleMs    = 100;   // Windows demotes to C6 after ~70 ms; 100 ms is safe.
        const int BurstMs   = 2;     // Mimics one ~16 ms-frame's worth of JS work at 60 FPS.
        const int CallDepth = 128;

        var sw       = Stopwatch.StartNew();
        var burstSw  = new Stopwatch();
        long cycles  = 0;
        long bursts  = 0;
        uint idx     = 0;
        double[] sink = new double[8 * 1024];

        while (!ct.IsCancellationRequested)
        {
            // Deep idle — releases the logical CPU back to the kernel scheduler.  With no
            // other work pinned to this LP, the core can drop to C6, the L1/L2 caches
            // power down, and the voltage regulator transitions to Vmin.
            Thread.Sleep(IdleMs);
            if (ct.IsCancellationRequested) break;

            // Wake-up burst.  The first thing the core does after returning from the
            // kernel wait is dispatch RecurseIdxCall — which does scale-4 indexed loads
            // and deep CALL/RET recursion *immediately*, with no warm-up.  If the
            // degraded silicon needs even a hundred microseconds at the new voltage
            // before its loads stabilise, those first loads come out wrong and the
            // guard-page-backed chase AVs.
            burstSw.Restart();
            do
            {
                idx = RecurseIdxCall(table, idx, CallDepth);
                cycles++;
            } while (burstSw.ElapsedMilliseconds < BurstMs);

            bursts++;
            if ((bursts & 0x7F) == 0)
            {
                sink[(int)(bursts & (sink.Length - 1))] += idx;
                Console.Write($"\r[Core {coreIndex}] DEEP-IDLE — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {bursts} wake-bursts, {cycles / 1000}K recursions");
            }
        }
    }
    finally
    {
        VirtualFree(table, 0, MEM_RELEASE);
    }
}

// ── types ──────────────────────────────────────────────────────────────────

enum StressMode { IdxCall, DeepIdle, External, Rotate }
