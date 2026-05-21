# Overview
Runs as a Windows service that automatically forces CPU affinity on most processes to avoid faulty CPU cores.

# Because Intel
https://www.google.com/search?q=intel+processor+oxidation%2Fcorrosion

# Download
[Latest Release](../../releases/latest)

# Installation
Make sure you have .net 10 runtime installed:  
https://dotnet.microsoft.com/en-us/download/dotnet/10.0  
  
It is recommended to use it as a Windows service, though it can also be run as a console app (best run as admin in that case to give access to more processes). To set it up as a Windows service (run cmd as admin):  
`sc create SetAffinity start= auto binPath= "<full_path_to_exe> <cpumask>"`  
`sc start SetAffinity`  

The spaces after `start=` and `binPath=` are important.  

The `cpumask` is a binary representation of logical CPUs (threads). For example, let’s say we have a 4-core CPU with hyper-threading. In Task Manager’s process affinity, we’ll see `CPU0` through `CPU7`. These correspond to the bits in the `cpumask` parameter.  
So for instance if the second core is faulty (i.e. `CPU2` and `CPU3`), the mask would be: 11001111.  

To "uninstall" (remove the Windows service registration):  
`sc stop SetAffinity`  
`sc delete SetAffinity`  

# Details
Some processes are excluded on purpose. `explorer.exe` and a few other application launchers would transfer their CPU affinity to their child processes, and some apps don't like that. For the same reason, the affinity change doesn't happen right away, but after about 20 seconds. This solves 99% of compatibility issues.  

This software is best run as a Windows service for two reasons: it's fire-and-forget this way, and it runs as the `Local System` user, which is higher-ish than a regular admin (giving it access to more processes). Higher-ish still doesn’t mean all-powerful, as it still does not grant access to kernel-level software (drivers, antivirus, anticheat, etc.).

# CpuCrashTest
The repo also ships a companion tool, `CpuCrashTest`, which does the *opposite* of `SetAffinity`: it pins one worker process per logical CPU and stresses each core in isolation, so you can identify which core(s) are faulty before adding them to the mask.

Run with `--mode <name>` (default is `rotate`, which alternates `deepidle` and `idxcall` every 30 s):

| Mode            | What it stresses                                                                                          |
|-----------------|-----------------------------------------------------------------------------------------------------------|
| `external`      | Spawns a *real* user-supplied binary (e.g. `node.exe` running a JS workload, or your actual problem app) pinned to each candidate core, watches whether it crashes with `0xC0000005` (or other silicon-fault status codes) within `--timeout` seconds.  Bypasses the impossible task of guessing what microarchitectural state synthetic loops are missing — uses the actual failure symptom directly.  **Recommended as the primary diagnostic.**  See "External-app mode" below. |
| `deepidle`      | Long `Thread.Sleep(100 ms)` (allows the logical CPU to drop to C6 — Windows' default DemotePromote heuristic demotes after ~70 ms idle), then a 2 ms `idxcall`-style burst on wake-up.  Targets the Raptor Lake **Vmin-shift transition** directly: degraded RPL silicon often doesn't fail under sustained load — it fails in the first ~µs after wake-up, when the voltage applied is too low for the degraded transistors.  Real apps (XboxPcApp, browsers, anything UI-driven) do exactly this every frame at 60 FPS, while sustained stress loops never exercise the transition. |
| `idxcall`       | Scale-4 indexed load inside a depth-128 NoInlining recursive function — one load on the way down, one on the way back up.  Models the exact shape of real `hermes.dll` crashes: the bytecode interpreter recurses ~200 frames deep (genuine C++ CALL/RET pairs, not threaded dispatch) and the AV is a `mov r32, [base + idx*4 + disp]` deep inside that chain.  RSB exhausts around depth 16-24, so every RET on the way up is a mispredict recovered via the indirect predictor while the load/store unit does the indexed chases. |
| `rotate`        | Default.  Alternates `deepidle` and `idxcall` every 30 s. |

> Earlier modes (`fma`, `int`, `branch`, `call`, `mixed`, `divide`, `threads`, `pointerchase`, `dispatch`, `bursty`, `verify`, `hermes`, `atomic`, `memcpy`, `bmi`, `idxchase`) were removed.  None of them reproduced any AV in overnight runs on cores independently confirmed as faulty via `external` mode — the failure mode does not appear to be raw-stress-driven.

## Recommended order for Hermes / Unity AV reproduction

If your faulty CPU produces Access Violations (`0xC0000005`) in `hermes.dll` (React Native), `UnityPlayer.dll`, or specific apps like XboxPcApp.exe:

1. **`external`** — the most reliable diagnostic in practice.  If you already know an app that crashes on the faulty core, use that as the test; otherwise a tight Node.js property-access loop is the closest proxy to the hermes.dll workload.  See below.
2. **`deepidle`** — best synthetic candidate.  Most likely to reproduce if the failure is the classic Raptor Lake Vmin-shift on wake-up from C-state idle (the canonical RPL failure mode).
3. **`idxcall`** — replicates the literal faulting instruction *and* the deep recursive CALL/RET chain observed in real Hermes crashes.  Useful as a control comparison vs `deepidle`: if `deepidle` reproduces but `idxcall` doesn't, that pins the trigger to the C-state transition rather than the instruction itself.

## External-app mode

If synthetic stress modes fail to reproduce, `--mode external` lets a real binary be the test.  This is often the only reliable way to identify a faulty core that exhibits "crashes only in specific apps" behaviour — typical of late-stage Raptor Lake Vmin degradation.

```
CpuCrashTest.exe --mode external --exec <path-to-binary> --args "<args-to-binary>" [--cores N[,N...]] [--timeout SECONDS]
```

For each core under test, spawns the binary, immediately pins it to that single logical CPU via `SetProcessAffinityMask`, and waits up to `--timeout` seconds (default 30) for it to exit.  Any exit with a Windows status code with the high bit set (`0xC0000000`+) is reported as a crash — `0xC0000005` Access Violation, `0xC000001D` Illegal Instruction, and a handful of other silicon-failure signatures are recognised by name.  All cores are tested in parallel (each pinned process can't contend with the others).

Examples:

```
:: Test cores 4 and 6 by running a tight Node.js property-access loop for 30 s each.
:: This is the workload pattern closest to the hermes.dll crash signature.
CpuCrashTest.exe --mode external --exec node.exe ^
  --args "-e \"while(true){var x={};for(var i=0;i<1000;i++)x['p'+i]=i;}\"" ^
  --cores 4,6 --timeout 30

:: Sweep every core with a longer-lived run of your actual problem app.
CpuCrashTest.exe --mode external --exec "C:\Path\To\XboxPcApp.exe" --timeout 60
```

The cleanest workload is one that hammers the suspect instruction pattern and runs longer than the timeout (so the only way the process exits is by crashing or being killed).  A "clean exit (code 0)" result means the workload finished before the timeout and gives no evidence either way — use longer-running args.

## Diagnostic tips

- The worker prints `Affinity OK: running on logical CPU N as requested` on startup.  If you instead see `*** AFFINITY MISMATCH ***`, Task Manager's `CPU N` numbering does not match what the kernel sees (typical causes: processor groups on >64-LP systems, affinity-blocking security software).  Test results won't be meaningful until that's resolved.
- Get an actual crash dump and look at the faulting instruction (Event Viewer → Windows Logs → Application → `.NET Runtime` / `Application Error` event, then load the `.dmp` in WinDbg).  The exact opcode bytes and the SIB scale tell you which instruction pattern to target — that's how `idxchase` was designed.
- Run `--mode verify` for an hour.  If it never logs a silent mismatch, the worker may not actually be pinned to the faulty core (P-core / E-core / HT-sibling numbering can differ from Task Manager's `CPU0..CPUn`).

License
----
MIT
