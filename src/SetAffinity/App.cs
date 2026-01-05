using Microsoft.Extensions.Hosting;
using System.Diagnostics;

namespace SetAffinity;

internal class App : BackgroundService
{
    private readonly IList<string> _clearedProcesses =
    [
        "svchost.exe",
        "NvBroadcast.Container.exe",
        "nvcontainer.exe",
        "NVDisplay.Container.exe",
    ];
    private readonly string[] _args;

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
                            if (_args[0] == "all" || process.StartTime > programStartTime || _clearedProcesses.Contains(process.MainModule?.ModuleName ?? string.Empty))
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
}
