using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace CrossPlatformService.Services;

//// <summary>
//// Convenience extension methods and factory shortcuts over IServiceManager.
//// A console application can call these to register itself as a service.
//// </summary>
public static class ServiceManagerExtensions
{
    /// <summary>
    /// Installs the currently running executable (Environment.ProcessPath) as a service.
    /// </summary>
    public static Task InstallCurrentApplicationAsync(
        this IServiceManager manager,
        string serviceName,
        string? description = null,
        bool autoStart = true,
        IEnumerable<string>? serviceArguments = null,
        System.Collections.Generic.IDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
        => manager.InstallServiceAsync(
            serviceName,
            GetCurrentExecutablePath(),
            description,
            environmentVariables,
            serviceArguments,
            autoStart,
            cancellationToken);

    /// <summary>
    /// Factory + InstallCurrentApplication combo (single-line usage).
    /// </summary>
    public static Task InstallSelfAsync(
        string serviceName,
        string? description = null,
        bool autoStart = true,
        IEnumerable<string>? serviceArguments = null,
        System.Collections.Generic.IDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
        => ServiceManagerFactory.Create().InstallCurrentApplicationAsync(
            serviceName,
            description,
            autoStart,
            serviceArguments,
            environmentVariables,
            cancellationToken);

    /// <summary>
    /// Quick access: start the service.
    /// </summary>
    public static Task StartAsync(string serviceName, CancellationToken ct = default)
        => ServiceManagerFactory.Create().StartServiceAsync(serviceName, ct);

    /// <summary>
    /// Quick access: stop the service.
    /// </summary>
    public static Task StopAsync(string serviceName, CancellationToken ct = default)
        => ServiceManagerFactory.Create().StopServiceAsync(serviceName, ct);

    /// <summary>
    /// Quick access: remove the service.
    /// </summary>
    public static Task RemoveAsync(string serviceName, CancellationToken ct = default)
        => ServiceManagerFactory.Create().RemoveServiceAsync(serviceName, ct);

    /// <summary>
    /// Quick access: query status.
    /// </summary>
    public static Task<ServiceStatus> StatusAsync(string serviceName, CancellationToken ct = default)
        => ServiceManagerFactory.Create().GetServiceStatusAsync(serviceName, ct);

    private static string GetCurrentExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Executable path of the running process could not be determined.");
        return path;
    }
}
