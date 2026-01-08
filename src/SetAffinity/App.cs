using Microsoft.Extensions.Hosting;
using System.Diagnostics;

namespace SetAffinity;

internal class App : BackgroundService
{
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
}
