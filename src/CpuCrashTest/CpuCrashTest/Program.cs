using System.Diagnostics;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

const string workerFlag = "--worker";

if (args.Length >= 2 && args[0] == workerFlag && int.TryParse(args[1], out int coreIndex))
    RunWorker(coreIndex);
else
    RunLauncher();

static void RunLauncher()
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
    Console.WriteLine($"Launching {coreCount} worker(s)...");
    Console.WriteLine();

    string exePath = Process.GetCurrentProcess().MainModule!.FileName;
    var workers = new List<Process>(coreCount);

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
            FileName = exePath,
            Arguments = $"--worker {i}",
            UseShellExecute = true,
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

static void RunWorker(int coreIndex)
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

    string mode = Avx512F.IsSupported ? "AVX-512 FMA"
                : (Fma.IsSupported && Avx2.IsSupported) ? "AVX2+FMA"
                : "SIMD";

    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine("╔══════════════════════════════════╗");
    Console.WriteLine($"║   CPU CRASH TEST  —  Core: {coreIndex,-5} ║");
    Console.WriteLine("╚══════════════════════════════════╝");
    Console.WriteLine();
    Console.WriteLine($"Stressing core {coreIndex} ({mode}). Press Ctrl+C to stop.");
    Console.WriteLine();

    StressLoop(coreIndex, cts.Token);

    Console.WriteLine();
    Console.WriteLine($"[Core {coreIndex}] Stopped.");
}

// Dispatches to the best stress loop the CPU supports at runtime.
static void StressLoop(int coreIndex, CancellationToken ct)
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
    // ~4 MB buffer, power-of-2 length for cheap bitwise modulo.
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
