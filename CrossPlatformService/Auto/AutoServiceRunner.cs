using System;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Diagnostics;
using CrossPlatformService.Services;
using CrossPlatformService.Utilities;
using Microsoft.Extensions.Hosting;

namespace CrossPlatformService.Auto;

/// <summary>
/// Single-command behavior:
/// - If service not installed and elevated: install, start, then exit
/// - If service not installed and NOT elevated: run in foreground (console) test mode
/// - If service is installed:
///     * If launched interactively (user console): inform and exit
///     * If running under a service context (Windows Service / systemd / launchd) run the normal host loop
/// </summary>
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

        // NEW: servis bağlamı algısı + manuel zorlama
        bool forceServiceRun = HasArg("--service-run");
        bool isWindowsSvc = IsWindowsServiceProcess();
        bool isLaunchdSvc = IsLaunchdServiceProcess();
        bool isSystemdSvc = IsSystemdServiceProcess();
        bool serviceProcess = forceServiceRun || isWindowsSvc || isLaunchdSvc || isSystemdSvc;

        // ÖNEMLİ: launchd/systemd altındayken asla "interactive" sayma
        bool interactive = !serviceProcess && IsInteractive();

        if (serviceProcess)
        {
            // launchd / systemd / Windows Service / --service-run → doğrudan worker loop
            return await RunForegroundAsync(serviceName, description, configureHost, cancellationToken);
        }

        if (status == ServiceStatus.NotFound)
        {
            if (PrivilegeHelper.IsElevated())
            {
                Console.WriteLine($"[AutoService] Service '{serviceName}' not found. Starting installation...");
                try
                {
                    await manager.InstallServiceAsync(
                        serviceName,
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
                    if (!interactive) return 2;

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
            if (interactive)
            {
                // Kullanıcı konsolundan çalıştırıldıysa uyar ve çık
                Console.WriteLine($"[AutoService] Service '{serviceName}' already installed. Use system service control (start/stop).");
                return 0;
            }

            // Servis bağlamı ise host'u çalıştır
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
        builder.UseContentRoot(AppContext.BaseDirectory);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            builder.UseWindowsService();
        }
        // Linux/macOS: ek lifetime zorunlu değil; SIGTERM ile kapanır.

        builder.ConfigureServices(_ => { /* hosted services dışarıdan eklenecek */ });
        configureHost?.Invoke(builder);

        using var host = builder.Build();
        Console.WriteLine($"[AutoService] '{serviceName}' run loop starting (foreground/service context).");
        await host.RunAsync(ct);
        return 0;
    }

    // --- helpers ---

    private static bool HasArg(string arg) =>
        Environment.GetCommandLineArgs().Any(a => string.Equals(a, arg, StringComparison.OrdinalIgnoreCase));

    private static bool IsWindowsServiceProcess()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        try
        {
            using var proc = Process.GetCurrentProcess();
            return proc.SessionId == 0; // Windows Service → Session 0
        }
        catch { return false; }
    }

    // macOS launchd bağlamı tespiti
    private static bool IsLaunchdServiceProcess()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return false;

        // launchd tipik env değişkenleri
        string? xpc = Environment.GetEnvironmentVariable("XPC_SERVICE_NAME");
        string? label = Environment.GetEnvironmentVariable("LAUNCH_JOBKEY_LABEL");
        if (!string.IsNullOrEmpty(xpc) || !string.IsNullOrEmpty(label))
            return true;

        // TTY yoksa büyük olasılıkla servis bağlamıdır (savunmacı yaklaşım)
        try
        {
            bool hasTty = !(Console.IsInputRedirected && Console.IsOutputRedirected && Console.IsErrorRedirected);
            if (!hasTty) return true;
        }
        catch { /* ignore */ }

        return false;
    }

    // Linux systemd bağlamı tespiti (hafif sezgisel, yeterli)
    private static bool IsSystemdServiceProcess()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return false;

        // systemd tipik env: INVOCATION_ID, NOTIFY_SOCKET vb.
        string? inv = Environment.GetEnvironmentVariable("INVOCATION_ID");
        string? ns = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        if (!string.IsNullOrEmpty(inv) || !string.IsNullOrEmpty(ns))
            return true;

        // TTY yoksa servis olma ihtimali yüksek
        try
        {
            bool hasTty = !(Console.IsInputRedirected && Console.IsOutputRedirected && Console.IsErrorRedirected);
            if (!hasTty) return true;
        }
        catch { /* ignore */ }

        return false;
    }

    private static bool IsInteractive()
    {
        try { return Environment.UserInteractive; }
        catch { return true; } // güvenli varsayım
    }

    private static async Task<ServiceStatus> SafeGetStatusAsync(
        IServiceManager manager,
        string serviceName,
        CancellationToken ct)
    {
        try { return await manager.GetServiceStatusAsync(serviceName, ct); }
        catch { return ServiceStatus.Unknown; }
    }
}
