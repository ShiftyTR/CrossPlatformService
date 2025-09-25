using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DemoHost;

public sealed record WorkerArguments(string[] Args);

internal sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;

    private readonly WorkerArguments _args;

    public Worker(ILogger<Worker> logger, WorkerArguments args)
    {
        _logger = logger;
        _args = args;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker started (PID={Pid}) Args=[{Args}]", Environment.ProcessId, string.Join(' ', _args.Args));

        var iteration = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            iteration++;
            _logger.LogInformation("Worker iteration {Iter} - Time: {Time}", iteration, DateTimeOffset.Now);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Worker stopping...");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("StopAsync invoked, performing graceful shutdown...");
        await base.StopAsync(cancellationToken);
    }
}
