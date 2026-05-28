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
The repo also ships a companion tool, `CpuCrashTest`, which identifies faulty CPU cores by using the Xbox PC app — a real app known to crash within seconds on degraded Raptor Lake silicon — as the test workload.

It sweeps every logical CPU in order: launches the Xbox app, pins it to one core, waits 20 seconds watching for a crash, kills it, and moves to the next core.  After the last core it loops back to 0 and repeats until Ctrl+C.

```
CpuCrashTest.exe
```

No arguments needed.  Sample output:

```
── Pass 1 ────────────────────────────────────────
  CPU  0  │ PID 9182  │ pinned → CPU 0  │ healthy (survived 20s) → killed
  CPU  1  │ PID 9240  │ pinned → CPU 1  │ healthy (survived 20s) → killed
  ...
  CPU  6  │ PID 9318  │ pinned → CPU 6  │ *** CRASH 0xC0000005 [ACCESS_VIOLATION]  →  CPU 6 IS SUSPECT ***
  CPU  7  │ PID 9401  │ pinned → CPU 7  │ healthy (survived 20s) → killed
  ...
```

Once you know the faulty logical CPU number(s), add their bits to the `SetAffinity` service mask to exclude them from normal process scheduling.

### How it works

The Xbox PC app is a Microsoft Store package and cannot be launched with a plain `Process.Start`.  The tool activates it through the Windows shell:

```
explorer.exe shell:appsFolder\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App
```

Explorer performs the package activation and exits immediately; `XboxPcApp.exe` appears a moment later.  The tool polls for the process (up to 5 s), sets `ProcessorAffinity` to a single-bit mask for the target core, then calls `WaitForExitAsync` with a 20-second timeout.

- **Crash** — the process exits with an NT error status (high bit set, e.g. `0xC0000005`): the core is flagged as suspect.
- **Survived** — the 7-second timeout fires while the process is still alive: the core is healthy for this workload, the app is killed, and the next core is tested.
- **Affinity failed** — `SetProcessAffinityMask` was rejected (rare; can happen if a security product intercepts it): the core is skipped with a warning.

# KeepEmBusy
After `CpuCrashTest` finds faulty logical CPUs and `SetAffinity` excludes them from normal scheduling, run `KeepEmBusy` to pin a 100% CPU burn worker on each bad core. That keeps the scheduler from placing other work on those cores.

```
KeepEmBusy.exe 8 9 10 11
```

The parent process supervises one hidden child per core (`KeepEmBusy.exe child <n>`). If a child exits (crash or kill), the parent restarts it after a short backoff. Press Ctrl+C to stop the parent and all children.

`SetAffinity` blacklists command lines containing `KeepEmBusy.exe child` so burn workers keep their single-core pin; the parent (`KeepEmBusy.exe 8 9 …`) is not blacklisted and may receive the good-core mask like any other app.

License
----
MIT
