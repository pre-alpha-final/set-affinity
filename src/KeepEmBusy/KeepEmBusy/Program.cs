using System.Diagnostics;

// Parent: KeepEmBusy.exe 8 9 10 11
//   Spawns one hidden child per core; restarts on exit until Ctrl+C.
// Child: KeepEmBusy.exe child 8  (internal — pins to CPU 8, low duty-cycle load)

const int RestartBackoffMs = 400;
const int DutyPeriodMs = 200;   // wake on this core every period
const int DutyBusyMs = 4;       // ~2% average CPU — enough to hold the core without boosting clocks

if (args.Length >= 1 && args[0].Equals("child", StringComparison.OrdinalIgnoreCase))
{
    RunChild(args);
    return;
}

await RunParentAsync(args);

// ─── child ───────────────────────────────────────────────────────────────────

static void RunChild(string[] args)
{
    if (args.Length < 2
        || !int.TryParse(args[1], out int core)
        || core < 0
        || core >= Environment.ProcessorCount)
    {
        Console.Error.WriteLine("Usage: KeepEmBusy.exe child <logical-cpu>");
        Environment.Exit(1);
        return;
    }

    var self = Process.GetCurrentProcess();
    try
    {
        self.ProcessorAffinity = (nint)(1L << core);
        self.PriorityClass = ProcessPriorityClass.Idle;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"affinity failed (CPU {core}): {ex.Message}");
        Environment.Exit(1);
    }

    int sleepMs = Math.Max(1, DutyPeriodMs - DutyBusyMs);
    while (true)
    {
        long deadline = Environment.TickCount64 + DutyBusyMs;
        long n = 0;
        while (Environment.TickCount64 < deadline)
            n = Interlocked.Increment(ref n);

        Thread.Sleep(sleepMs);
    }
}

// ─── parent ──────────────────────────────────────────────────────────────────

static async Task RunParentAsync(string[] args)
{
    if (!TryParseCores(args, out int[] cores))
    {
        Console.WriteLine("Usage: KeepEmBusy.exe <logical-cpu> [<logical-cpu> ...]");
        Console.WriteLine($"       Logical CPUs on this machine: 0–{Environment.ProcessorCount - 1}");
        Environment.Exit(1);
    }

    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine("╔═══════════════════════════════════════╗");
    Console.WriteLine("║      KEEP EM BUSY  ·  FAULTY CORES    ║");
    Console.WriteLine("╚═══════════════════════════════════════╝");
    Console.WriteLine($"  Logical CPUs : {string.Join(", ", cores.Select(c => $"CPU {c}"))}");
    Console.WriteLine($"  Children     : hidden, ~{DutyBusyMs * 100 / DutyPeriodMs}% duty per core, restarted on exit");
    Console.WriteLine();
    Console.WriteLine("Ctrl+C to stop.");
    Console.WriteLine();

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var children = new Dictionary<int, Process>();
    var monitorTasks = new List<Task>();

    try
    {
        foreach (int core in cores)
        {
            children[core] = StartChild(core);
            Console.WriteLine($"  CPU {core,2}  │ started PID {children[core].Id}");
            monitorTasks.Add(MonitorChildAsync(core, children, cts.Token));
        }

        await Task.WhenAll(monitorTasks);
    }
    catch (OperationCanceledException) { }
    finally
    {
        foreach (var proc in children.Values)
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch { }
            finally { proc.Dispose(); }
        }

        Console.WriteLine();
        Console.WriteLine("Stopped.");
    }
}

static bool TryParseCores(string[] args, out int[] cores)
{
    cores = [];
    if (args.Length == 0)
        return false;

    var list = new List<int>();
    foreach (string arg in args)
    {
        if (!int.TryParse(arg, out int core) || core < 0 || core >= Environment.ProcessorCount)
            return false;
        if (!list.Contains(core))
            list.Add(core);
    }

    cores = list.ToArray();
    return cores.Length > 0;
}

static Process StartChild(int core)
{
    var psi = new ProcessStartInfo
    {
        FileName = Environment.ProcessPath,
        Arguments = $"child {core}",
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    var proc = Process.Start(psi)
        ?? throw new InvalidOperationException($"Failed to start child for CPU {core}");
    return proc;
}

static async Task MonitorChildAsync(
    int core,
    Dictionary<int, Process> children,
    CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        Process proc = children[core];
        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        int exitCode = proc.ExitCode;
        proc.Dispose();

        if (ct.IsCancellationRequested)
            return;

        Console.WriteLine($"  CPU {core,2}  │ exited {exitCode} — restarting in {RestartBackoffMs}ms");
        try
        {
            await Task.Delay(RestartBackoffMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        children[core] = StartChild(core);
        Console.WriteLine($"  CPU {core,2}  │ restarted PID {children[core].Id}");
    }
}
