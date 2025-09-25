using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CrossPlatformService.Services;

public enum ServiceStatus
{
    Unknown = 0,
    NotFound,
    Installing,
    Stopped,
    Running,
    Paused,
    Error
}

public interface IServiceManager
{
    bool IsSupportedPlatform { get; }

    Task InstallServiceAsync(
        string serviceName,
        string executablePath,
        string? description = null,
        IDictionary<string, string>? environmentVariables = null,
        bool autoStart = true,
        CancellationToken cancellationToken = default);

    Task RemoveServiceAsync(string serviceName, CancellationToken cancellationToken = default);

    Task StartServiceAsync(string serviceName, CancellationToken cancellationToken = default);

    Task StopServiceAsync(string serviceName, CancellationToken cancellationToken = default);

    Task PauseServiceAsync(string serviceName, CancellationToken cancellationToken = default);

    Task ResumeServiceAsync(string serviceName, CancellationToken cancellationToken = default);

    Task<ServiceStatus> GetServiceStatusAsync(string serviceName, CancellationToken cancellationToken = default);
}

public static class ServiceManagerFactory
{
    public static IServiceManager Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new Platform.Windows.WindowsServiceManager();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new Platform.Linux.LinuxServiceManager();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new Platform.Mac.MacOsServiceManager();

        throw new PlatformNotSupportedException("Unsupported platform.");
    }
}
