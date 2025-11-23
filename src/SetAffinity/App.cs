using Microsoft.Extensions.Hosting;
using System.Diagnostics;

namespace SetAffinity;

internal class App : BackgroundService
{
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
                        if (process.StartTime > programStartTime)
                        {
                            process.ProcessorAffinity = (nint)4294966527;
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
