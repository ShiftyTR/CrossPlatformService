using System;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Diagnostics;
using CrossPlatformService.Services;
using CrossPlatformService.Utilities;
using Microsoft.Extensions.Hosting;

namespace CrossPlatformService.Auto;

//// <summary>
//// Single-command behavior:
//// - If service not installed and elevated: install, start, then exit
//// - If service not installed and NOT elevated: run in foreground (console) test mode
//// - If service is installed:
////     * If launched interactively (user console): inform and exit
////     * If running under a service context (Windows Service / systemd / launchd) run the normal host loop
//// </summary>
public static class AutoServiceRunner
{
    public static async Task<int> RunAsync(
        string serviceName,
        string? description,
        Action<IHostBuilder>? configureHost = null,
        CancellationToken cancellationToken = default)
    {
        var manager = ServiceManagerFactory.Create();
        var status = await SafeGetStatusAsync(manager, serviceName, cancellationToken);

        bool serviceProcess = IsWindowsServiceProcess();
        bool interactive = !serviceProcess && IsInteractive();

        // If already in a Windows Service context (Session 0) run host directly.
        if (serviceProcess)
        {
            return await RunForegroundAsync(serviceName, description, configureHost, cancellationToken);
        }

        if (status == ServiceStatus.NotFound)
        {
            if (PrivilegeHelper.IsElevated())
            {
                    Console.WriteLine($"[AutoService] Service '{serviceName}' not found. Starting installation...");
                try
                {
                    await manager.InstallServiceAsync(serviceName,
                        executablePath: Environment.ProcessPath
                            ?? throw new InvalidOperationException("ProcessPath could not be read."),
                        description: description,
                        environmentVariables: null,
                        autoStart: true,
                        cancellationToken: cancellationToken);

                    Console.WriteLine("[AutoService] Installation completed and service started (autoStart).");
                    Console.WriteLine("[AutoService] You may close this window. Service is running in the background.");
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[AutoService] Service installation failed: " + ex.Message);
                    Console.ResetColor();
                    if (!interactive)
                        return 2;
                    Console.WriteLine("[AutoService] Falling back to foreground console mode...");
                    return await RunForegroundAsync(serviceName, description, configureHost, cancellationToken);
                }
            }
            else
            {
                Console.WriteLine($"[AutoService] Service '{serviceName}' is not installed and elevation is missing.");
                Console.WriteLine("[AutoService] Run with admin/root to install. Running in foreground for now.");
                return await RunForegroundAsync(serviceName, description, configureHost, cancellationToken);
            }
        }
        else
        {
            // Servis mevcut
            if (interactive)
            {
                // Kullanıcı manuel çalıştırdı; servis zaten kurulu. Bilgilendir ve çık.
                Console.WriteLine($"[AutoService] Service '{serviceName}' already installed. Use system service control (start/stop).");
                return 0;
            }

            // Servis context'i (WindowsService/systemd/launchd) içerisinde: host'u çalıştır.
            return await RunForegroundAsync(serviceName, description, configureHost, cancellationToken);
        }
    }

    private static async Task<int> RunForegroundAsync(
        string serviceName,
        string? description,
        Action<IHostBuilder>? configureHost,
        CancellationToken ct)
    {
        var builder = Host.CreateDefaultBuilder();

        // Windows / Linux entegrasyonu koşullu.
        builder.UseContentRoot(AppContext.BaseDirectory);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            builder.UseWindowsService();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // systemd entegrasyonu opsiyonel. UseSystemd() çağrısı kaldırıldı (paket tüm platformlarda referanslı değil).
        }

        builder.ConfigureServices(services =>
        {
            // Tüketen uygulama (DemoHost) kendi hosted servislerini ekleyecek (Worker vs.)
            // Burada generic ekleme yapılmadı.
        });

        configureHost?.Invoke(builder);

        // Windows service context için ekstra ServiceBase sarmalayıcı kaldırıldı.
        // UseWindowsService() host lifetime sinyallerini (Start/Stop) yönetir.

        using var host = builder.Build();
        Console.WriteLine($"[AutoService] '{serviceName}' run loop starting (foreground/service context).");
        await host.RunAsync(ct);
        return 0;
    }

    private static bool IsWindowsServiceProcess()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;
        try
        {
            using var proc = Process.GetCurrentProcess();
            // Windows servisleri Session 0'da koşar.
            if (proc.SessionId == 0)
                return true;
        }
        catch { /* ignore */ }
        return false;
    }

    private static bool IsInteractive()
    {
        try
        {
            return Environment.UserInteractive;
        }
        catch
        {
            return true; // Varsayılan güvenli varsayım
        }
    }

    private static async Task<ServiceStatus> SafeGetStatusAsync(
        IServiceManager manager,
        string serviceName,
        CancellationToken ct)
    {
        try
        {
            return await manager.GetServiceStatusAsync(serviceName, ct);
        }
        catch
        {
            return ServiceStatus.Unknown;
        }
    }
}
