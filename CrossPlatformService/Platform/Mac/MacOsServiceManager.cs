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

//// <summary>
//// macOS (launchd) service management implementation (initial version).
//// Service registration via launchctl commands and generated plist file.
//// </summary>
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

        var plistPath = GetPlistPath(serviceName, systemLevel: PrivilegeHelper.IsElevated());

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

        // İzinler: launchd genelde root owned ve 644 bekler system daemons için
        try
        {
            // chmod 644
            _ = await ProcessRunner.RunAsync("chmod", new[] { "644", plistPath }, cancellationToken: cancellationToken);
        }
        catch { /* ignore */ }

        // load
        var load = await RunLaunchCtlAsync(new[] { "load", plistPath }, cancellationToken);
        if (!load.Success)
        {
            try { File.Delete(plistPath); } catch { /* ignore */ }
            throw new InvalidOperationException($"Plist load failed: {load}");
        }

        if (autoStart)
        {
            // RunAtLoad zaten true; yine de start denemesi
            _ = await RunLaunchCtlAsync(new[] { "start", serviceName }, cancellationToken);
        }
    }

    public async Task RemoveServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        var plistPathSystem = GetPlistPath(serviceName, systemLevel: true);
        var plistPathUser = GetPlistPath(serviceName, systemLevel: false);

        var plistPath = File.Exists(plistPathSystem) ? plistPathSystem :
                        File.Exists(plistPathUser) ? plistPathUser : null;

        if (plistPath == null)
            return;

        // stop (hata olsa yok say)
        _ = await RunLaunchCtlAsync(new[] { "stop", serviceName }, cancellationToken);
        // unload
        _ = await RunLaunchCtlAsync(new[] { "unload", plistPath }, cancellationToken);

        try
        {
            File.Delete(plistPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Plist could not be deleted: {plistPath} - {ex.Message}", ex);
        }
    }

    public async Task StartServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        var result = await RunLaunchCtlAsync(new[] { "start", serviceName }, cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"Service could not be started: {result}");
    }

    public async Task StopServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        var result = await RunLaunchCtlAsync(new[] { "stop", serviceName }, cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"Service could not be stopped: {result}");
    }

    public Task PauseServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("Pause is not supported on macOS launchd."));

    public Task ResumeServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("Resume is not supported on macOS launchd."));

    public async Task<ServiceStatus> GetServiceStatusAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        // launchctl list serviceName
        var result = await RunLaunchCtlAsync(new[] { "list", serviceName }, cancellationToken);
        if (!result.Success)
        {
            var all = (result.StdOut + " " + result.StdErr).ToLowerInvariant();
            if (all.Contains("could not find") || all.Contains("no such process"))
                return ServiceStatus.NotFound;
            return ServiceStatus.Error;
        }

        // Örnek çıktı (yeni sürümlerde plist-like):
        // {
        //   "Label" = "myservice";
        //   "LastExitStatus" = 0;
        //   "PID" = 1234;
        // }
        var txt = result.StdOut;
        if (txt.Contains("PID", StringComparison.OrdinalIgnoreCase))
        {
            // PID satırı
            // "PID" = 1234;
            var lines = txt.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var l in lines)
            {
                var line = l.Trim();
                if (line.StartsWith("\"PID\"", StringComparison.OrdinalIgnoreCase))
                {
                    if (line.Contains('='))
                    {
                        var parts = line.Split('=', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            var pidPart = parts[1].Trim().TrimEnd(';').Trim();
                            if (int.TryParse(pidPart, out var pid) && pid > 0)
                                return ServiceStatus.Running;
                        }
                    }
                }
            }
        }

        // Eğer PID yoksa exit status kontrolü
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
        // EnvironmentVariables launchd dict formatında
        var envBuilder = new StringBuilder();
        if (environmentVariables != null)
        {
            foreach (var kv in environmentVariables)
            {
                envBuilder.AppendLine($"        <key>{XmlEscape(kv.Key)}</key><string>{XmlEscape(kv.Value)}</string>");
            }
        }

        var envSection = environmentVariables != null && environmentVariables.Count > 0
            ? $@"    <key>EnvironmentVariables</key>
    <dict>
{envBuilder.ToString().TrimEnd()}
    </dict>
"
            : string.Empty;

        // Standart log yolu (opsiyonel). İleride yapılandırılabilir hale getirilebilir.
        var stdOut = $"/var/log/{label}.out.log";
        var stdErr = $"/var/log/{label}.err.log";

        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key>
    <string>{XmlEscape(label)}</string>
    <key>ProgramArguments</key>
    <array>
        <string>{XmlEscape(executablePath)}</string>
    </array>
    <key>WorkingDirectory</key>
    <string>{XmlEscape(workingDirectory)}</string>
    <key>RunAtLoad</key>
    <{(runAtLoad ? "true" : "false")}/>
    <key>KeepAlive</key>
    <true/>
    {envSection}<key>StandardOutPath</key>
    <string>{XmlEscape(stdOut)}</string>
    <key>StandardErrorPath</key>
    <string>{XmlEscape(stdErr)}</string>
    <key>ProcessType</key>
    <string>Background</string>
    <!-- Description (informational) -->
    <key>Comment</key>
    <string>{XmlEscape(description)}</string>
</dict>
</plist>
";
    }

    private static string XmlEscape(string value) =>
        value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");

    private static Task<ProcessRunner.ProcessResult> RunLaunchCtlAsync(IEnumerable<string> args, CancellationToken ct)
        => ProcessRunner.RunAsync("launchctl", args, cancellationToken: ct);
}
