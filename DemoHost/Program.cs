using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using CrossPlatformService.Auto;
using CrossPlatformService.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using DemoHost;

internal static class Program
{
    // Default service name (based on assembly name)
    private static readonly string DefaultServiceName =
        Assembly.GetEntryAssembly()?.GetName().Name ?? "DemoHostService";

    // Behavior when user runs with no arguments:
    // 1. Service not installed + elevated => auto install & start (then exit)
    // 2. Service not installed + not elevated => run in foreground (test) mode
    // 3. Service installed => if interactive inform & exit; if service context run worker
    static async Task<int> Main(string[] args)
    {
        string serviceName = DefaultServiceName;
        string? description = "Demo cross-platform service";

        // Parse simple args:
        // Commands: install | remove | start | stop | status
        // Optional name override: name=CustomName
        // If no command provided -> fall back to AutoServiceRunner auto behavior.
        string? command = null;
        var workerArgList = new List<string>();

        foreach (var a in args)
        {
            if (a.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
            {
                var n = a.Substring("name=".Length).Trim();
                if (!string.IsNullOrWhiteSpace(n))
                    serviceName = n;
                continue;
            }

            if (command == null)
            {
                switch (a.ToLowerInvariant())
                {
                    case "install":
                    case "remove":
                    case "uninstall":
                    case "start":
                    case "stop":
                    case "status":
                        command = a.ToLowerInvariant();
                        continue;
                }
            }

            // Unrecognized token => treat as worker argument
            workerArgList.Add(a);
        }

        if (command != null)
        {
            var manager = ServiceManagerFactory.Create();
            try
            {
                switch (command)
                {
                    case "install":
                        // workerArgList burada 'install' haricinde kalan tüm token'ları içerir.
                        // Bunları kalıcı servis argümanları olarak iletiriz.
                        await manager.InstallServiceAsync(serviceName,
                            executablePath: Environment.ProcessPath
                                ?? throw new InvalidOperationException("ProcessPath not available."),
                            description: description,
                            environmentVariables: null,
                            serviceArguments: workerArgList.Count > 0 ? workerArgList : null,
                            autoStart: true,
                            cancellationToken: CancellationToken.None);
                        Console.WriteLine($"[CLI] Installed service '{serviceName}' with args: {string.Join(' ', workerArgList)}");
                        return 0;

                    case "uninstall":
                    case "remove":
                        await manager.RemoveServiceAsync(serviceName, CancellationToken.None);
                        Console.WriteLine($"[CLI] Removed service '{serviceName}'.");
                        return 0;

                    case "start":
                        await manager.StartServiceAsync(serviceName, CancellationToken.None);
                        Console.WriteLine($"[CLI] Started service '{serviceName}'.");
                        return 0;

                    case "stop":
                        await manager.StopServiceAsync(serviceName, CancellationToken.None);
                        Console.WriteLine($"[CLI] Stopped service '{serviceName}'.");
                        return 0;

                    case "status":
                        var st = await manager.GetServiceStatusAsync(serviceName, CancellationToken.None);
                        Console.WriteLine($"[CLI] Status of '{serviceName}': {st}");
                        return 0;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[CLI] Command failed: " + ex.Message);
                return 2;
            }
        }

        // Auto mode (original behavior)
        return await AutoServiceRunner.RunAsync(
            serviceName,
            description,
            configureHost: builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton(new WorkerArguments(workerArgList.ToArray()));
                    services.AddHostedService<Worker>();
                });
            },
            cancellationToken: CancellationToken.None);
    }
}
