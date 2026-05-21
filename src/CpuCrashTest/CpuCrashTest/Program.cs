using System.Diagnostics;

// Launches the Xbox PC app, immediately pins it to one logical CPU, waits up to
// TestMs for it to crash (exit with an NT error status), then kills it and moves to
// the next core.  Sweeps all cores repeatedly until Ctrl+C.
//
// The Xbox app is a Microsoft Store / UWP package, so it can't be started with
// Process.Start directly.  We activate it through the Windows shell:
//   explorer.exe shell:appsFolder\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App
// Explorer performs the package activation and exits immediately; the real app
// appears as XboxPcApp.exe a moment later.

const string ShellTarget = @"shell:appsFolder\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App";
const string ProcessName = "XboxPcApp";   // Process.ProcessName — no .exe suffix
const int    TestMs      = 15_000;         // time pinned to each core before declaring healthy
const int    LaunchMs    = 5_000;          // max time to wait for XboxPcApp.exe to appear after activation
const int    GapMs       = 1_500;          // cooldown between kill and next launch

int coreCount = int.TryParse(Environment.GetEnvironmentVariable("NUMBER_OF_PROCESSORS"), out int n)
    ? n : Environment.ProcessorCount;
int pairCount = coreCount / 2;   // each pair = one physical core (two SMT threads)

// Log file sits next to the executable, named with the start timestamp so repeated
// runs don't overwrite each other.
string logPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
    $"crash-log-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");
using var log = new StreamWriter(logPath, append: false, System.Text.Encoding.UTF8) { AutoFlush = true };

void Log(string line)
{
    string stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
    Console.WriteLine(stamped);
    log.WriteLine(stamped);
}

void LogWrite(string text)
{
    // Write to console without newline; log gets a placeholder that will be
    // completed by the next Log() call which provides the full line.
    Console.Write(text);
    log.Write(text);
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
string header =
    $"╔═══════════════════════════════════════╗\n" +
    $"║   XBOX APP  ·  CORE CRASH IDENTIFIER  ║\n" +
    $"╚═══════════════════════════════════════╝\n" +
    $"  Logical CPUs : {coreCount}  ({pairCount} physical core pair{(pairCount == 1 ? "" : "s")})\n" +
    $"  Process      : {ProcessName}.exe\n" +
    $"  Per pair     : {TestMs / 1000}s pinned, then killed → next pair\n" +
    $"  Log file     : {logPath}\n";
Console.Write(header);
log.Write(header);

Console.WriteLine();
log.WriteLine();
Log("Ctrl+C to stop.  Cores that print *** CRASH *** are suspect.");
Console.WriteLine();
log.WriteLine();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

int pass = 0;
int pair = 0;

try
{
    while (!cts.IsCancellationRequested)
    {
        if (pair == 0)
            Log($"── Pass {++pass} {new string('─', Math.Max(0, 44 - pass.ToString().Length))}");

        int cpuLo   = pair * 2;
        int cpuHi   = cpuLo + 1;
        nint mask   = (nint)(3L << cpuLo);   // both SMT threads of the physical core

        LogWrite($"  CPUs {cpuLo}+{cpuHi}  │ ");

        // Kill any previous instance and let it fully vanish before the next launch.
        KillByName(ProcessName);
        await SleepAsync(GapMs, cts.Token);
        if (cts.IsCancellationRequested) break;

        Launch(ShellTarget);

        // Poll until XboxPcApp.exe appears (explorer exits immediately; the real app
        // spawns asynchronously from the package activation broker).
        using Process? proc = await PollAsync(ProcessName, LaunchMs, cts.Token);
        if (proc is null)
        {
            string skipped = "process did not appear — skipped";
            Console.WriteLine(skipped);
            log.WriteLine(skipped);
            pair = (pair + 1) % pairCount;
            continue;
        }

        // Pin the process to both logical CPUs of this physical core.
        if (!TryPin(proc, mask, out string pinErr))
        {
            string pinFail = $"affinity failed ({pinErr}) — skipped";
            Console.WriteLine(pinFail);
            log.WriteLine(pinFail);
            KillByName(ProcessName);
            await SleepAsync(GapMs, cts.Token);
            pair = (pair + 1) % pairCount;
            continue;
        }

        string pinned = $"PID {proc.Id}  │ pinned → CPUs {cpuLo}+{cpuHi}  │ ";
        Console.Write(pinned);
        log.Write(pinned);

        (bool crashed, string status) = await WatchAsync(proc, TestMs, cts.Token);

        if (crashed)
        {
            string crashLine = $"*** CRASH {status}  →  CPUs {cpuLo}+{cpuHi} (physical core {pair}) IS SUSPECT ***";
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(crashLine);
            Console.ResetColor();
            log.WriteLine(crashLine);
        }
        else if (status == "cancelled")
        {
            Console.WriteLine("cancelled");
            log.WriteLine("cancelled");
            break;
        }
        else
        {
            string ok = $"healthy ({status}) → killed";
            Console.WriteLine(ok);
            log.WriteLine(ok);
            KillByName(ProcessName);
        }

        await SleepAsync(GapMs, cts.Token);
        pair = (pair + 1) % pairCount;
    }
}
catch (OperationCanceledException) { }
finally
{
    KillByName(ProcessName);
    Log("Stopped.");
    Log($"Full log saved to: {logPath}");
}

// ─── helpers ─────────────────────────────────────────────────────────────────

// Activate the Xbox app via explorer shell — the only supported launch path for
// packaged Store apps that don't expose a plain .exe entry point.
static void Launch(string target)
    => Process.Start(new ProcessStartInfo
    {
        FileName        = "explorer.exe",
        Arguments       = target,
        UseShellExecute = false,
    });

static void KillByName(string name)
{
    foreach (var p in Process.GetProcessesByName(name))
    {
        try   { p.Kill(entireProcessTree: true); p.WaitForExit(2_000); }
        catch { }
        finally { p.Dispose(); }
    }
}

// Repeatedly scans the process list until `name` appears or `maxMs` elapses.
// Returns the live Process on success (caller owns and must Dispose), or null.
static async Task<Process?> PollAsync(string name, int maxMs, CancellationToken ct)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < maxMs && !ct.IsCancellationRequested)
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            if (!p.HasExited) return p;
            p.Dispose();
        }
        await Task.Delay(250, ct).ConfigureAwait(false);
    }
    return null;
}

static bool TryPin(Process proc, nint mask, out string err)
{
    try
    {
        proc.ProcessorAffinity = mask;
        err = "";
        return true;
    }
    catch (Exception ex)
    {
        err = ex.Message;
        return false;
    }
}

// Waits up to `maxMs` for the process to exit.
// Returns (crashed: true, NT status code string) if it exits with an error status,
// (false, "survived Ns") if the timeout expires while the process is still alive, or
// (false, "exited cleanly") if it exits with code 0.
static async Task<(bool crashed, string status)> WatchAsync(
    Process proc, int maxMs, CancellationToken ct)
{
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
    linked.CancelAfter(maxMs);

    try
    {
        await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        if (ct.IsCancellationRequested) return (false, "cancelled");
        // Timeout fired — process is still running — that's a healthy result.
        return (false, $"survived {maxMs / 1_000}s");
    }
    catch (Exception ex)
    {
        return (false, $"watch error: {ex.Message}");
    }

    // Process exited within the window — check whether it was an NT error status.
    uint code = unchecked((uint)proc.ExitCode);
    bool crashed = (code & 0x80000000) != 0;
    string tag = code switch
    {
        0           => "exited cleanly",
        0xC0000005  => $"0x{code:X8} [ACCESS_VIOLATION]",
        0xC000001D  => $"0x{code:X8} [ILLEGAL_INSTRUCTION]",
        0xC0000094  => $"0x{code:X8} [INTEGER_DIVIDE_BY_ZERO]",
        0xC0000096  => $"0x{code:X8} [PRIVILEGED_INSTRUCTION]",
        0xC00000FD  => $"0x{code:X8} [STACK_OVERFLOW]",
        0xC0000409  => $"0x{code:X8} [STACK_BUFFER_OVERRUN]",
        _           => $"0x{code:X8}",
    };
    return (crashed, tag);
}

// Task.Delay wrapper that swallows OperationCanceledException so callers can
// await it unconditionally and check ct.IsCancellationRequested afterwards.
static async Task SleepAsync(int ms, CancellationToken ct)
{
    try   { await Task.Delay(ms, ct).ConfigureAwait(false); }
    catch (OperationCanceledException) { }
}
