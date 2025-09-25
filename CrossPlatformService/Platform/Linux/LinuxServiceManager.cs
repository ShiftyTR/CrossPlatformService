using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CrossPlatformService.Services;
using CrossPlatformService.Utilities;

namespace CrossPlatformService.Platform.Linux;

//// <summary>
//// Linux (systemd) service management implementation (initial version).
//// Requires root privileges (writing unit file and executing systemctl).
//// </summary>
internal sealed class LinuxServiceManager : IServiceManager
{
    public bool IsSupportedPlatform => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    private const string SystemdUnitDirectory = "/etc/systemd/system";

    public async Task InstallServiceAsync(
        string serviceName,
        string executablePath,
        string? description = null,
        IDictionary<string, string>? environmentVariables = null,
        IEnumerable<string>? serviceArguments = null,
        bool autoStart = true,
        CancellationToken cancellationToken = default)
    {
        PrivilegeHelper.EnsureElevated("Linux systemd service installation");

        if (string.IsNullOrWhiteSpace(serviceName))
            throw new ArgumentException("Service name cannot be empty.", nameof(serviceName));
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("Executable path cannot be empty.", nameof(executablePath));

        var unitPath = GetUnitFilePath(serviceName);

        if (File.Exists(unitPath))
            throw new InvalidOperationException($"Systemd unit already exists for '{serviceName}': {unitPath}");

        // Eğer binary /root altında ise SELinux context (admin_home_t) nedeniyle systemd tarafından EXEC reddi (203/EXEC Permission denied) yaşanabilir.
        // Güvenli, canonical bir konuma kopyala: /usr/local/lib/<serviceName>/<binary>
        // (self-contained tek binary varsayımı; kopyalama başarısız olursa orijinal yolu kullanır.)
        try
        {
            var fullExec = Path.GetFullPath(executablePath);
            if (fullExec.StartsWith("/root/", StringComparison.Ordinal))
            {
                var targetDir = Path.Combine("/usr/local/lib", serviceName);
                Directory.CreateDirectory(targetDir);
                var targetPath = Path.Combine(targetDir, Path.GetFileName(fullExec));
                File.Copy(fullExec, targetPath, overwrite: true);

                // chmod 755
                try { _ = ProcessRunner.RunAsync("chmod", new[] { "755", targetPath }, cancellationToken: cancellationToken); } catch { /* ignore */ }

                // restorecon (SELinux) – başarısız olabilir, önemli değil
                try { _ = ProcessRunner.RunAsync("restorecon", new[] { targetPath }, cancellationToken: cancellationToken); } catch { /* ignore */ }

                executablePath = targetPath;
            }
        }
        catch
        {
            // Sessiz geç; orijinal executablePath kullanılacak
        }

        // Working directory (directory containing the executable) – relocation sonrası tekrar al
        var workDir = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? "/";

        var unitContent = BuildUnitFileContent(
            serviceName,
            executablePath,
            workDir,
            description,
            environmentVariables,
            serviceArguments,
            autoStart);

        try
        {
            await File.WriteAllTextAsync(unitPath, unitContent, Encoding.UTF8, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unit file could not be written: {unitPath} - {ex.Message}", ex);
        }

        // systemctl daemon-reload
        await RunSystemctlAsync(new[] { "daemon-reload" }, cancellationToken);

        if (autoStart)
        {
            // enable unit
            var enable = await RunSystemctlAsync(new[] { "enable", serviceName }, cancellationToken);
            if (!enable.Success)
                throw new InvalidOperationException($"Service enable failed: {enable}");

            // start unit
            var start = await RunSystemctlAsync(new[] { "start", serviceName }, cancellationToken);
            if (!start.Success)
                throw new InvalidOperationException($"Service start failed: {start}");
        }
    }

    public async Task RemoveServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        PrivilegeHelper.EnsureElevated("Linux systemd service removal");

        var unitPath = GetUnitFilePath(serviceName);
        if (!File.Exists(unitPath))
            return;

        // Attempt stop (ignore errors if already stopped or absent)
        _ = await RunSystemctlAsync(new[] { "stop", serviceName }, cancellationToken);
        // disable unit
        _ = await RunSystemctlAsync(new[] { "disable", serviceName }, cancellationToken);

        try
        {
            File.Delete(unitPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unit file could not be deleted: {unitPath} - {ex.Message}", ex);
        }

        // systemctl daemon-reload
        await RunSystemctlAsync(new[] { "daemon-reload" }, cancellationToken);
    }

    public async Task StartServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        PrivilegeHelper.EnsureElevated("Linux systemd service start");
        var result = await RunSystemctlAsync(new[] { "start", serviceName }, cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"Service could not be started: {result}");
    }

    public async Task StopServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        PrivilegeHelper.EnsureElevated("Linux systemd service stop");
        var result = await RunSystemctlAsync(new[] { "stop", serviceName }, cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"Service could not be stopped: {result}");
    }

    public Task PauseServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("Pause operation is not supported on Linux (systemd)."));

    public Task ResumeServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("Resume operation is not supported on Linux (systemd)."));

    public async Task<ServiceStatus> GetServiceStatusAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        // systemctl is-active returns (exit 0) for: active
        // inactive (3), failed (3), activating, deactivating, unknown
        var isActive = await RunSystemctlAsync(new[] { "is-active", serviceName }, cancellationToken);
        if (!isActive.Success)
        {
            var txt = (isActive.StdOut + " " + isActive.StdErr).Trim();
            if (string.IsNullOrWhiteSpace(txt))
                return ServiceStatus.NotFound;

            if (txt.Contains("inactive", StringComparison.OrdinalIgnoreCase))
                return ServiceStatus.Stopped;
            if (txt.Contains("failed", StringComparison.OrdinalIgnoreCase))
                return ServiceStatus.Error;
            if (txt.Contains("activating", StringComparison.OrdinalIgnoreCase))
                return ServiceStatus.Installing;
            if (txt.Contains("deactivating", StringComparison.OrdinalIgnoreCase))
                return ServiceStatus.Stopped;
            if (txt.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
                txt.Contains("not-found", StringComparison.OrdinalIgnoreCase))
                return ServiceStatus.NotFound;

            return ServiceStatus.Unknown;
        }

        // active
        return ServiceStatus.Running;
    }

    private static string GetUnitFilePath(string serviceName)
        => Path.Combine(SystemdUnitDirectory, $"{serviceName}.service");

    private static string BuildUnitFileContent(
        string serviceName,
        string executablePath,
        string workingDirectory,
        string? description,
        IDictionary<string, string>? environmentVariables,
        IEnumerable<string>? serviceArguments,
        bool autoStart)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Unit]");
        sb.AppendLine($"Description={Escape(description ?? serviceName)}");
        sb.AppendLine("After=network.target");
        sb.AppendLine();
        sb.AppendLine("[Service]");
        sb.AppendLine("Type=simple");

        var execBuilder = new StringBuilder();
        execBuilder.Append(QuoteIfNeeded(executablePath));
        if (serviceArguments != null)
        {
            foreach (var arg in serviceArguments)
            {
                if (string.IsNullOrWhiteSpace(arg)) continue;
                execBuilder.Append(' ').Append(Escape(arg));
            }
        }
        sb.AppendLine($"ExecStart={execBuilder}");

        sb.AppendLine($"WorkingDirectory={Escape(workingDirectory)}");
        sb.AppendLine("Restart=on-failure");
        sb.AppendLine("RestartSec=5");
        if (environmentVariables != null)
        {
            foreach (var kv in environmentVariables)
            {
                sb.AppendLine($"Environment=\"{Escape(kv.Key)}={Escape(kv.Value)}\"");
            }
        }
        sb.AppendLine();
        sb.AppendLine("[Install]");
        sb.AppendLine("WantedBy=multi-user.target");

        return sb.ToString();
    }

    private static string Escape(string value)
        => value
            .Replace("\"", "\\\"");

    private static string QuoteIfNeeded(string path)
        => path.Contains(' ') ? $"\"{path}\"" : path;

    private static Task<ProcessRunner.ProcessResult> RunSystemctlAsync(IEnumerable<string> args, CancellationToken ct)
        => ProcessRunner.RunAsync("systemctl", args, cancellationToken: ct);
}
