using System.Diagnostics;

namespace SetAffinity;

internal class Program
{
    static async Task Main(string[] args)
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
