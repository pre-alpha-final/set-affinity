using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using System.Management;

namespace SetAffinity;

internal class App : BackgroundService
{
    private readonly string[] _args;
    private readonly List<string> _appLaunchersBlacklist =
    [
        "explorer.exe",
        "svchost.exe -k netsvcs -p -s Schedule",
        "taskhostw.exe",
        //"winlogon.exe",
        //"userinit.exe",
        //"wininit.exe",
        //"services.exe",
        //"lsass.exe",
        //"taskmgr.exe",
    ];

    public App(string[] args)
    {
        _args = args;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var programStartTime = DateTime.Now;
        while (true)
        {
            var processes = Process.GetProcesses();
            _ = Task.Run(async () =>
            {
                await Task.Delay(20000);
                foreach (var process in processes)
                {
                    try
                    {
                        using (process)
                        {
                            var commandLine = GetCommandLine(process);
                            if (commandLine != null && _appLaunchersBlacklist.Any(e => commandLine.Contains(e, StringComparison.OrdinalIgnoreCase)))
                            {
                                continue;
                            }

                            if ((_args.Length > 0 && _args[0] == "all") || process.StartTime > programStartTime)
                            {
                                process.ProcessorAffinity = (nint)4294966527;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        // ignore
                    }
                }
            });
            await Task.Delay(5000);
        }
    }

    private static string? GetCommandLine(Process process)
    {
        try
        {
            using ManagementObjectSearcher managementObjectSearcher = new($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");
            foreach (var managementObject in managementObjectSearcher.Get())
            {
                return managementObject["CommandLine"]?.ToString();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}
