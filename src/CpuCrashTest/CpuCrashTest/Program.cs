using System.Diagnostics;

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

    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine("╔══════════════════════════════════╗");
    Console.WriteLine($"║   CPU CRASH TEST  —  Core: {coreIndex,-5} ║");
    Console.WriteLine("╚══════════════════════════════════╝");
    Console.WriteLine();
    Console.WriteLine($"Stressing core {coreIndex}. Press Ctrl+C to stop.");
    Console.WriteLine();

    StressLoop(coreIndex, cts.Token);

    Console.WriteLine();
    Console.WriteLine($"[Core {coreIndex}] Stopped.");
}

static void StressLoop(int coreIndex, CancellationToken ct)
{
    double acc = 1.0;
    long iterations = 0;
    var sw = Stopwatch.StartNew();

    while (!ct.IsCancellationRequested)
    {
        acc = Math.Sqrt(acc + Math.Sin(acc) + Math.Cos(acc) + 1.0);
        if (acc > 1e15) acc = 1.0;
        iterations++;

        if (iterations % 50_000_000 == 0)
        {
            Console.Write($"\r[Core {coreIndex}] Running — {sw.Elapsed:hh\\:mm\\:ss} elapsed, {iterations / 1_000_000}M iterations");
        }
    }
}
