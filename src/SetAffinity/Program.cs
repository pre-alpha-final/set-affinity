using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SetAffinity;

internal class Program
{
    static async Task Main(string[] args)
    {
        IHostBuilder builder = Host.CreateDefaultBuilder(args)
            .UseWindowsService()
            .ConfigureServices((hostContext, services) =>
            {
                services.AddSingleton(args);
                services.AddHostedService<App>();
            });
        builder.Build().Run();
    }
}
