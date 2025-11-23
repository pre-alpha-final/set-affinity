using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SetAffinity;

internal class Program
{
    static async Task Main(string[] args)
    {
        IHostBuilder builder = Host.CreateDefaultBuilder(args)
            .UseWindowsService() // 👈 enables Windows Service mode
            .ConfigureServices((hostContext, services) =>
            {
                services.AddHostedService<App>(); // your background service
            });
        builder.Build().Run();
    }
}
