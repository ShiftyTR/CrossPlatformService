using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CrossPlatformService.Services;
using CrossPlatformService.Utilities;

namespace CrossPlatformService.Platform.Mac;
internal sealed class MacOsServiceManager : IServiceManager
{
    public bool IsSupportedPlatform => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    private const string SystemDaemonsDirectory = "/Library/LaunchDaemons";
    private static readonly string UserAgentsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "LaunchAgents");

    public async Task InstallServiceAsync(
        string serviceName,
        string executablePath,
        string? description = null,
        IDictionary<string, string>? environmentVariables = null,
        bool autoStart = true,
        CancellationToken cancellationToken = default)
    {
        PrivilegeHelper.EnsureElevated("macOS launchd service installation (system level default).");

        if (string.IsNullOrWhiteSpace(serviceName))
            throw new ArgumentException("Service name cannot be empty.", nameof(serviceName));
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("Executable path cannot be empty.", nameof(executablePath));

        var plistPath = GetPlistPath(serviceName, systemLevel: true);
        if (File.Exists(plistPath))
            throw new InvalidOperationException($"Plist already exists for '{serviceName}': {plistPath}");

        var workingDir = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? "/";

        var plistContent = BuildPlistContent(
            label: serviceName,
            executablePath: executablePath,
            workingDirectory: workingDir,
            description: description ?? serviceName,
            environmentVariables: environmentVariables,
            runAtLoad: autoStart);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plistPath)!);
            await File.WriteAllTextAsync(plistPath, plistContent, Encoding.UTF8, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Plist file could not be written: {plistPath} - {ex.Message}", ex);
        }

        // launchd plist izinleri: root:wheel + 644 (aksi durumda load reddedebilir)
        try
        {
            _ = await ProcessRunner.RunAsync("chown", new[] { "root:wheel", plistPath }, cancellationToken: cancellationToken);
            _ = await ProcessRunner.RunAsync("chmod", new[] { "644", plistPath }, cancellationToken: cancellationToken);
        }
        catch { /* ignore */ }

        // Modern akış: bootstrap + enable + kickstart
        // (fallback olarak load/start da denenir)
        var bootstrap = await RunLaunchCtlAsync(new[] { "bootstrap", "system", plistPath }, cancellationToken);
        if (!bootstrap.Success && !bootstrap.StdErr.Contains("Service is already loaded", StringComparison.OrdinalIgnoreCase))
        {
            // Eski yöntemle dener
            var load = await RunLaunchCtlAsync(new[] { "load", plistPath }, cancellationToken);
            if (!load.Success)
            {
                try { File.Delete(plistPath); } catch { /* ignore */ }
                throw new InvalidOperationException($"Plist bootstrap/load failed: {bootstrap.StdErr} {load.StdErr}".Trim());
            }
        }

        // enable (idempotent)
        _ = await RunLaunchCtlAsync(new[] { "enable", $"system/{serviceName}" }, cancellationToken);

        if (autoStart)
        {
            // kickstart: -k (crash sayacı reset), -p (on-demand)
            var ks = await RunLaunchCtlAsync(new[] { "kickstart", "-k", $"system/{serviceName}" }, cancellationToken);
            if (!ks.Success)
            {
                // fallback
                _ = await RunLaunchCtlAsync(new[] { "start", serviceName }, cancellationToken);
            }
        }
    }

    public async Task RemoveServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        var plistPath = GetPlistPath(serviceName, systemLevel: true);
        if (!File.Exists(plistPath))
        {
            // User-level?
            plistPath = GetPlistPath(serviceName, systemLevel: false);
            if (!File.Exists(plistPath)) return;
        }

        // stop (ignore errors)
        _ = await RunLaunchCtlAsync(new[] { "kill", "SIGTERM", $"system/{serviceName}" }, cancellationToken);
        _ = await RunLaunchCtlAsync(new[] { "stop", serviceName }, cancellationToken);

        // bootout (modern unload)
        _ = await RunLaunchCtlAsync(new[] { "bootout", "system", plistPath }, cancellationToken);
        _ = await RunLaunchCtlAsync(new[] { "unload", plistPath }, cancellationToken);

        try { File.Delete(plistPath); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Plist could not be deleted: {plistPath} - {ex.Message}", ex);
        }
    }

    public async Task StartServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        var ks = await RunLaunchCtlAsync(new[] { "kickstart", "-k", $"system/{serviceName}" }, cancellationToken);
        if (!ks.Success)
        {
            var res = await RunLaunchCtlAsync(new[] { "start", serviceName }, cancellationToken);
            if (!res.Success)
                throw new InvalidOperationException($"Service could not be started: {ks.StdErr} {res.StdErr}".Trim());
        }
    }

    public async Task StopServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        // kill SIGTERM + stop
        var k = await RunLaunchCtlAsync(new[] { "kill", "SIGTERM", $"system/{serviceName}" }, cancellationToken);
        var s = await RunLaunchCtlAsync(new[] { "stop", serviceName }, cancellationToken);
        if (!k.Success && !s.Success)
            throw new InvalidOperationException($"Service could not be stopped: {k.StdErr} {s.StdErr}".Trim());
    }

    public Task PauseServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("Pause is not supported on macOS launchd."));

    public Task ResumeServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("Resume is not supported on macOS launchd."));

    public async Task<ServiceStatus> GetServiceStatusAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        // Önce print ile domainli dene
        var pr = await RunLaunchCtlAsync(new[] { "print", $"system/{serviceName}" }, cancellationToken);
        if (pr.Success)
        {
            var t = pr.StdOut;
            if (t.Contains("state = running", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("pid =", StringComparison.OrdinalIgnoreCase))
                return ServiceStatus.Running;

            if (t.Contains("last exit code = 0", StringComparison.OrdinalIgnoreCase))
                return ServiceStatus.Stopped;

            return ServiceStatus.Error;
        }

        // Eski fallback: list
        var ls = await RunLaunchCtlAsync(new[] { "list", serviceName }, cancellationToken);
        if (!ls.Success)
        {
            var all = (ls.StdOut + " " + ls.StdErr).ToLowerInvariant();
            if (all.Contains("could not find") || all.Contains("no such process"))
                return ServiceStatus.NotFound;
            return ServiceStatus.Error;
        }

        var txt = ls.StdOut;
        if (txt.Contains("PID", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in txt.Split('\n'))
            {
                var l = line.Trim();
                if (l.StartsWith("\"PID\"", StringComparison.OrdinalIgnoreCase) && l.Contains('='))
                {
                    var parts = l.Split('=', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[1].Trim().TrimEnd(';'), out var pid) && pid > 0)
                        return ServiceStatus.Running;
                }
            }
        }

        if (txt.Contains("LastExitStatus", StringComparison.OrdinalIgnoreCase))
        {
            if (txt.Contains("LastExitStatus\" = 0", StringComparison.OrdinalIgnoreCase))
                return ServiceStatus.Stopped;
            return ServiceStatus.Error;
        }

        return ServiceStatus.Unknown;
    }

    private static string GetPlistPath(string serviceName, bool systemLevel)
        => systemLevel
            ? Path.Combine(SystemDaemonsDirectory, $"{serviceName}.plist")
            : Path.Combine(UserAgentsDirectory, $"{serviceName}.plist");

    private static string BuildPlistContent(
        string label,
        string executablePath,
        string workingDirectory,
        string description,
        IDictionary<string, string>? environmentVariables,
        bool runAtLoad)
    {
        // EnvironmentVariables (launchd dict)
        var envBuilder = new StringBuilder();
        if (environmentVariables != null)
        {
            foreach (var kv in environmentVariables)
                envBuilder.AppendLine($"        <key>{XmlEscape(kv.Key)}</key><string>{XmlEscape(kv.Value)}</string>");
        }

        var envSection = (environmentVariables != null && environmentVariables.Count > 0)
            ? $@"    <key>EnvironmentVariables</key>
    <dict>
{envBuilder.ToString().TrimEnd()}
    </dict>
"
            : string.Empty;

        var stdOut = $"/var/log/{label}.out.log";
        var stdErr = $"/var/log/{label}.err.log";

        // ÖNEMLİ: --service-run eklendi, KeepAlive dict'e çevrildi
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key>
    <string>{XmlEscape(label)}</string>

    <key>ProgramArguments</key>
    <array>
        <string>{XmlEscape(executablePath)}</string>
        <string>--service-run</string>
    </array>

    <key>WorkingDirectory</key>
    <string>{XmlEscape(workingDirectory)}</string>

    <key>RunAtLoad</key>
    <{(runAtLoad ? "true" : "false")}/>

    <key>KeepAlive</key>
    <dict>
        <key>SuccessfulExit</key>
        <false/>
    </dict>

{envSection}    <key>StandardOutPath</key>
    <string>{XmlEscape(stdOut)}</string>
    <key>StandardErrorPath</key>
    <string>{XmlEscape(stdErr)}</string>

    <key>ProcessType</key>
    <string>Background</string>

    <key>Comment</key>
    <string>{XmlEscape(description)}</string>
</dict>
</plist>
";
    }

    private static string XmlEscape(string value) =>
        value.Replace("&", "&amp;")
             .Replace("<", "&lt;")
             .Replace(">", "&gt;")
             .Replace("\"", "&quot;")
             .Replace("'", "&apos;");

    private static Task<ProcessRunner.ProcessResult> RunLaunchCtlAsync(IEnumerable<string> args, CancellationToken ct)
        => ProcessRunner.RunAsync("launchctl", args, cancellationToken: ct);
}
