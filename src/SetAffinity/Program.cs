using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SetAffinity;

internal class Program
{
    static async Task Main(string[] args)
    {
        try
        {
            IHostBuilder builder = Host.CreateDefaultBuilder(args)
                .UseWindowsService()
                .ConfigureLogging(e =>
                {
#if !DEBUG
                    e.ClearProviders();
#endif
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddSingleton(args);
                    services.AddHostedService<App>();
                });
            builder.Build().Run();
        }
        catch (Exception)
        {
            // ignore
        }
    }
}
