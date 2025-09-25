using System;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CrossPlatformService.Platform.Windows;

//// <summary>
//// Bridge between Windows Service Control Manager and .NET Generic Host (IHost).
//// Maps ServiceBase lifecycle events (Start / Stop / Pause / Continue) to the hosted application.
//// </summary>
internal sealed class WindowsGenericHostService : ServiceBase
{
    private readonly Func<IHost> _hostFactory;
    private IHost? _host;
    private CancellationTokenSource? _cts;
    private bool _paused;
    private readonly object _lock = new();

    public WindowsGenericHostService(Func<IHost> hostFactory, string serviceName)
    {
        _hostFactory = hostFactory ?? throw new ArgumentNullException(nameof(hostFactory));
        ServiceName = serviceName;
        CanStop = true;
        CanShutdown = true;
        CanPauseAndContinue = true; // Pause/Continue support (optional – depends on app logic)
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        _cts = new CancellationTokenSource();
        // Create and run the host on a background task
        Task.Run(async () =>
        {
            try
            {
                _host = _hostFactory();
                await _host.StartAsync(_cts.Token).ConfigureAwait(false);

                // Wait for host shutdown (until Stop is called)
                await _host.WaitForShutdownAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    var logger = _host?.Services.GetService(typeof(ILogger<WindowsGenericHostService>)) as ILogger;
                    logger?.LogCritical(ex, "WindowsGenericHostService startup failure");
                }
                catch { /* ignore */ }
                // On failure attempt to stop the service
                try { Stop(); } catch { /* ignore */ }
            }
        });
    }

    protected override void OnStop()
    {
        lock (_lock)
        {
            try
            {
                _cts?.Cancel();
                if (_host != null)
                {
                    _host.StopAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                    _host.Dispose();
                    _host = null;
                }
            }
            catch { /* ignore */ }
        }
    }

    protected override void OnPause()
    {
        _paused = true;
        // Application can inspect this paused flag if it wants (e.g. shared state)
        base.OnPause();
    }

    protected override void OnContinue()
    {
        _paused = false;
        base.OnContinue();
    }

    protected override void OnShutdown()
    {
        // System is shutting down
        OnStop();
        base.OnShutdown();
    }

    public bool IsPaused => _paused;
}
