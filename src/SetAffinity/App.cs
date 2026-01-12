using Microsoft.Extensions.Hosting;
using System.CommandLine;
using System.Diagnostics;
using System.Management;

namespace SetAffinity;

internal class App : BackgroundService
{
    private readonly List<string> _appLaunchersBlacklist =
    [
        "explorer.exe",
        "svchost.exe -k netsvcs -p -s Schedule",
        "taskhostw.exe",
        "svchost.exe -k DcomLaunch -p",
    ];
    private string _mode;
    private nint _cpuMask;

    public App(string[] args)
    {
        HandleArgs(args);
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

                            if ((_mode == "a") || process.StartTime > programStartTime)
                            {
                                process.ProcessorAffinity = _cpuMask;
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

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void HandleArgs(string[] args)
    {
        var mode = new Option<string>("mode", "-m", "--mode")
        {
            Description = "'a' for all (default), 'n' for newly appearing apps only (compatibility mode)"
        };
        var cpuMask = new Argument<string>("cpu mask")
        {
            Description = "binary mask for logical CPUs"
        };
        var rootCommand = new RootCommand("Set cpu affinity for apps")
        {
            mode,
            cpuMask,
        };
        var parseResult = rootCommand.Parse(args);
        parseResult.Invoke();
        if (parseResult.Errors.Count > 0)
        {
            throw new ArgumentException("Invalid arguments");
        }

        _mode = parseResult.GetValue(mode) ?? "a";
        _cpuMask = (nint)Convert.ToInt64(new string(parseResult.GetValue(cpuMask)!.Reverse().ToArray()), 2);
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
