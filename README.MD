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

Run with `--mode <name>` (default is `rotate`, which cycles through every mode for 30 s each):

| Mode            | What it stresses                                                                                          |
|-----------------|-----------------------------------------------------------------------------------------------------------|
| `pointerchase`  | NaN-box-style ALU chain feeding an immediate load — silent bit-flip becomes `0xC0000005`. **Best bet** for reproducing `hermes.dll` / Unity crashes. |
| `dispatch`      | Indirect-call hot loop through a 256-entry function-pointer table — same shape as Hermes opcode dispatch and Unity vtable calls. |
| `bursty`        | ~10 ms full-bore INT+FP burst, then `Sleep(2)` — hammers the Vmin/P-state transition that steady-state stressors miss. |
| `verify`        | Four shadow LCG chains computed in parallel; logs silent miscalculations *without* crashing. Useful when the faulty core corrupts results without producing an AV. |
| `int`           | 8× 64-bit LCG + rotate-XOR — IA-core integer multiplier and rotate units.                                |
| `branch`        | 16-arm `switch` on unpredictable input — front-end, BTB, ROB recovery.                                   |
| `call`          | Recursion to depth 48 — overflows the Return Stack Buffer.                                               |
| `mixed`         | INT chains + scalar FP with `CVTSI2SD`/`CVTTSD2SI` — int↔FP bypass network.                              |
| `divide`        | 8× 64-bit `IDIV` chains — variable-latency microcode division path.                                      |
| `threads`       | 4 threads pinned to one logical CPU — XSAVE/XRSTOR storms.                                               |
| `fma`           | Widest SIMD FMA the CPU supports (AVX-512 / AVX2+FMA / `Vector<T>`).                                     |

License
----
MIT
