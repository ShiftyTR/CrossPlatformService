using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CrossPlatformService.Utilities;

/// <summary>
/// Dış komut / sistem aracını (sc, systemctl, launchctl vb.) çalıştırmak için yardımcı sınıf.
/// Standart çıktı ve hata çıktısını toplar.
/// </summary>
internal static class ProcessRunner
{
    public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
    {
        public bool Success => ExitCode == 0;
        public override string ToString() => $"ExitCode={ExitCode}\nSTDOUT:\n{StdOut}\nSTDERR:\n{StdErr}";
    }

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string>? arguments = null,
        string? workingDirectory = null,
        IDictionary<string, string>? environment = null,
        int timeoutMilliseconds = 60_000,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;

        if (arguments != null)
        {
            foreach (var arg in arguments)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        if (environment != null)
        {
            foreach (var kvp in environment)
            {
                startInfo.Environment[kvp.Key] = kvp.Value;
            }
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stderr.AppendLine(e.Data);
        };
        process.Exited += (_, _) => tcs.TrySetResult(true);

        if (!process.Start())
            throw new InvalidOperationException($"Süreç başlatılamadı: {fileName}");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutMilliseconds > 0)
            linkedCts.CancelAfter(timeoutMilliseconds);

        Task waitTask = tcs.Task;
        try
        {
            await waitTask.WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }

            throw new TimeoutException($"Komut zaman aşımına uğradı veya iptal edildi: {fileName}");
        }

        return new ProcessResult(process.ExitCode, stdout.ToString().TrimEnd(), stderr.ToString().TrimEnd());
    }
}
