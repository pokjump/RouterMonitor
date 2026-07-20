using Microsoft.Extensions.Logging;
using RouterMonitor.Core;
using RouterMonitor.Core.Providers.AdbVV5822;

var dumpRequested = args.Contains("--dump");

using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddConsole()
    .SetMinimumLevel(LogLevel.Information));

var logger = loggerFactory.CreateLogger<Program>();

var options = new AdbVV5822Options
{
    BaseAddress = new Uri(Environment.GetEnvironmentVariable("ROUTER_BASEURL") ?? "http://192.168.1.1"),
    Username = Environment.GetEnvironmentVariable("ROUTER_USERNAME") ?? "admin",
    Password = Environment.GetEnvironmentVariable("ROUTER_PASSWORD") ?? "admin_netia",
    RawDumpDirectory = dumpRequested ? Path.Combine(AppContext.BaseDirectory, "dumps") : null,
};

IRouterProvider provider = new AdbVV5822Provider(options, loggerFactory.CreateLogger<AdbVV5822Provider>());

try
{
    await provider.LoginAsync();

    var overview = await provider.GetOverviewAsync();
    PrintOverview(overview);

    var devices = await provider.GetDevicesAsync();
    PrintDevices(devices);
}
catch (Exception ex)
{
    logger.LogError(ex, "Nie udało się pobrać danych z routera.");
    return 1;
}
finally
{
    await provider.LogoutAsync();
    (provider as IDisposable)?.Dispose();
}

return 0;

static void PrintOverview(RouterMonitor.Core.Models.RouterOverview overview)
{
    Console.WriteLine();
    Console.WriteLine("=== Przegląd routera ===");
    Console.WriteLine($"Model:      {overview.Model ?? "?"}");
    Console.WriteLine($"Firmware:   {overview.Firmware ?? "?"}");
    Console.WriteLine($"MAC:        {overview.Mac ?? "?"}");
    Console.WriteLine($"Uptime:     {overview.Uptime ?? "?"}");
    Console.WriteLine($"WAN IP:     {overview.WanIp ?? "?"}");
    Console.WriteLine($"Brama:      {overview.Gateway ?? "?"}");
    Console.WriteLine($"DNS:        {overview.Dns ?? "?"}");
    Console.WriteLine();
    Console.WriteLine($"-- Wszystkie sekcje ({overview.Sections.Count}) --");
    foreach (var section in overview.Sections)
    {
        Console.WriteLine($"[{section.Title}]");
        foreach (var (label, value) in section.Fields)
            Console.WriteLine($"    {label}: {value}");
    }
}

static void PrintDevices(IReadOnlyList<RouterMonitor.Core.Models.NetworkDevice> devices)
{
    Console.WriteLine();
    Console.WriteLine($"=== Urządzenia w sieci ({devices.Count}) ===");
    foreach (var device in devices)
    {
        Console.WriteLine($"- {device.Name} | IP: {device.IpAddress ?? "?"} | MAC: {device.MacAddress ?? "?"} | Połączenie: {device.ConnectionType ?? "?"}");
    }
}
