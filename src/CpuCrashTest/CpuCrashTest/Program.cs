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

    Console.WriteLine("╔══════════════════════════════════╗");
    Console.WriteLine("║       CPU CRASH TEST LAUNCHER    ║");
    Console.WriteLine("╚══════════════════════════════════╝");
    Console.WriteLine();
    Console.WriteLine($"Detected {coreCount} logical CPU(s).");
    Console.WriteLine($"Launching {coreCount} worker(s)...");
    Console.WriteLine();

    string exePath = Process.GetCurrentProcess().MainModule!.FileName;

    for (int i = 0; i < coreCount; i++)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--worker {i}",
            UseShellExecute = true,
            CreateNoWindow = false,
        };
        Process.Start(psi);
        Console.WriteLine($"  Launched worker for core {i}");
    }

    Console.WriteLine();
    Console.WriteLine($"All {coreCount} worker(s) launched. Press any key to exit the launcher.");
    Console.ReadKey(intercept: true);
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
