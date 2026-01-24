# Overview
Runs as a Windows service that automatically forces CPU affinity on most processes to avoid faulty CPU cores.

# Because Intel
https://www.google.com/search?q=intel+processor+oxidation%2Fcorrosion

# Ok, Intel CPU, fine, but where exactly?
Turns out you can actually check which core is the faulty one:  
https://www.youtube.com/watch?v=dU5qLJqTSAc&t=1099s

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

License
----
MIT
