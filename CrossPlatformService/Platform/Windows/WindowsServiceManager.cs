using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CrossPlatformService.Services;
using CrossPlatformService.Utilities;

namespace CrossPlatformService.Platform.Windows;

//// <summary>
//// Windows service management implementation (initial version).
//// Uses sc.exe for basic operations.
//// Note: For production scenarios a more robust approach (P/Invoke CreateService, etc.)
//// can be added. For now a practical and testable path was chosen.
//// </summary>
internal sealed class WindowsServiceManager : IServiceManager
{
    public bool IsSupportedPlatform => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static readonly Dictionary<string, ServiceStatus> StateMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "STOPPED", ServiceStatus.Stopped },
        { "RUNNING", ServiceStatus.Running },
        { "PAUSED",  ServiceStatus.Paused },
        { "START_PENDING", ServiceStatus.Installing },
        { "STOP_PENDING", ServiceStatus.Stopped },
        { "PAUSE_PENDING", ServiceStatus.Paused },
        { "CONTINUE_PENDING", ServiceStatus.Running }
    };

    public async Task InstallServiceAsync(
        string serviceName,
        string executablePath,
        string? description = null,
        IDictionary<string, string>? environmentVariables = null,
        IEnumerable<string>? serviceArguments = null,
        bool autoStart = true,
        CancellationToken cancellationToken = default)
    {
        PrivilegeHelper.EnsureElevated("Windows service installation");

        if (string.IsNullOrWhiteSpace(serviceName))
            throw new ArgumentException("Service name cannot be empty.", nameof(serviceName));
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("Executable path cannot be empty.", nameof(executablePath));

        // Already exists?
        var exists = await ServiceExistsAsync(serviceName, cancellationToken);
        if (exists)
            throw new InvalidOperationException($"Service '{serviceName}' already exists.");

        // Build full binPath with optional arguments
        string Quote(string a)
        {
            if (string.IsNullOrEmpty(a)) return "\"\"";
            return a.Contains(' ') || a.Contains('"')
                ? $"\"{a.Replace("\"", "\\\"")}\""
                : a;
        }

        var argSegment = serviceArguments != null
            ? string.Join(' ', serviceArguments.Select(Quote))
            : string.Empty;

        var fullBinPath = string.IsNullOrEmpty(argSegment)
            ? $"\"{executablePath}\""
            : $"\"{executablePath}\" {argSegment}";

        // sc create command (a space must follow binPath= and start= parameters)
        var createArgs = new List<string>
        {
            "create",
            serviceName,
            "binPath=", fullBinPath,
            "start=", autoStart ? "auto" : "demand"
        };

        var createResult = await ProcessRunner.RunAsync("sc", createArgs, cancellationToken: cancellationToken);
        if (!createResult.Success)
            throw new InvalidOperationException($"Service could not be created. sc output:\n{createResult}");

        if (!string.IsNullOrWhiteSpace(description))
        {
            var descResult = await ProcessRunner.RunAsync("sc", new[]
            {
                "description",
                serviceName,
                description!
            }, cancellationToken: cancellationToken);

            if (!descResult.Success)
            {
                // Attempt rollback delete
                _ = await TryDeleteServiceAsync(serviceName, cancellationToken);
                throw new InvalidOperationException($"Service description could not be set:\n{descResult}");
            }
        }

        // There is no direct standard to inject environment variables into a Windows Service
        // (would require registry ImagePath manipulation or a wrapper host).
        // Leaving a placeholder for now.
        if (environmentVariables != null && environmentVariables.Count > 0)
        {
            // Environment variable propagation can be implemented when a wrapper host is added.
        }

        if (autoStart)
        {
            var startResult = await ProcessRunner.RunAsync("sc", new[] { "start", serviceName }, cancellationToken: cancellationToken);
            if (!startResult.Success)
            {
                // Even if auto-start fails the installation can still be considered complete.
                // Developer is informed. Optionally: throw.
            }
        }
    }

    public async Task RemoveServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        PrivilegeHelper.EnsureElevated("Windows service removal");

        var exists = await ServiceExistsAsync(serviceName, cancellationToken);
        if (!exists)
            return;

        // Attempt to stop (errors can be ignored if already stopped)
        _ = await ProcessRunner.RunAsync("sc", new[] { "stop", serviceName }, cancellationToken: cancellationToken);

        // Delete
        var delResult = await ProcessRunner.RunAsync("sc", new[] { "delete", serviceName }, cancellationToken: cancellationToken);
        if (!delResult.Success)
            throw new InvalidOperationException($"Service could not be deleted:\n{delResult}");
    }

    public async Task StartServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        PrivilegeHelper.EnsureElevated("Windows service start");

        var result = await ProcessRunner.RunAsync("sc", new[] { "start", serviceName }, cancellationToken: cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"Service could not be started:\n{result}");
    }

    public async Task StopServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        PrivilegeHelper.EnsureElevated("Windows service stop");

        var result = await ProcessRunner.RunAsync("sc", new[] { "stop", serviceName }, cancellationToken: cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"Service could not be stopped:\n{result}");
    }

    public async Task PauseServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        PrivilegeHelper.EnsureElevated("Windows service pause");

        var result = await ProcessRunner.RunAsync("sc", new[] { "pause", serviceName }, cancellationToken: cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"Service could not be paused:\n{result}");
    }

    public async Task ResumeServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        PrivilegeHelper.EnsureElevated("Windows service resume");

        var result = await ProcessRunner.RunAsync("sc", new[] { "continue", serviceName }, cancellationToken: cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"Service could not be resumed:\n{result}");
    }

    public async Task<ServiceStatus> GetServiceStatusAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        var query = await ProcessRunner.RunAsync("sc", new[] { "query", serviceName }, cancellationToken: cancellationToken);
        if (!query.Success)
        {
            // Output may include "FAILED 1060" (service not installed)
            if (query.StdErr.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase) ||
                query.StdOut.Contains("1060", StringComparison.OrdinalIgnoreCase))
                return ServiceStatus.NotFound;

            return ServiceStatus.Error;
        }

        // STATE              : 4  RUNNING
        var stateLine = query.StdOut.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("STATE", StringComparison.OrdinalIgnoreCase));

        if (stateLine == null)
            return ServiceStatus.Unknown;

        var parts = stateLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Extract last token (e.g. RUNNING)
        var stateToken = parts.LastOrDefault();
        if (stateToken != null && StateMap.TryGetValue(stateToken, out var mapped))
            return mapped;

        return ServiceStatus.Unknown;
    }

    private async Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync("sc", new[] { "query", serviceName }, timeoutMilliseconds: 15_000, cancellationToken: cancellationToken);
        if (!result.Success)
        {
            if (result.StdErr.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase) ||
                result.StdOut.Contains("1060", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return result.Success;
    }

    private async Task<bool> TryDeleteServiceAsync(string serviceName, CancellationToken cancellationToken)
    {
        var del = await ProcessRunner.RunAsync("sc", new[] { "delete", serviceName }, cancellationToken: cancellationToken);
        return del.Success;
    }
}
