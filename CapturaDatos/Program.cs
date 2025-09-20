using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using CapturaDatos;

IHost host = Host.CreateDefaultBuilder(args)
    //.UseWindowsService()
    .ConfigureAppConfiguration((hostingContext, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
    })
    .ConfigureServices((hostContext, services) =>
    {
        services.AddSingleton<RotatingLogger>();
        services.AddSingleton<ParametrosServicio>();
        services.AddSingleton<MonitorConexionSQL>();
        services.AddHostedService<ServicioPrincipal>();
    })
    .Build();

await host.RunAsync();
