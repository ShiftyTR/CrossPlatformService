# CrossPlatformService

Cross-platform .NET 8 library and sample host that lets a console application install / start / stop / remove itself as an OS service:

- Windows: Windows Service (via `sc.exe`)
- Linux: systemd unit (`/etc/systemd/system/*.service`)
- macOS: launchd daemon / agent (generated `.plist`, `launchctl` load/start)

## Features

| Capability | Windows | Linux (systemd) | macOS (launchd) |
|------------|---------|-----------------|-----------------|
| Install (create) | ✅ | ✅ | ✅ |
| Remove (delete) | ✅ | ✅ | ✅ |
| Start | ✅ | ✅ | ✅ |
| Stop | ✅ | ✅ | ✅ |
| Status Query | ✅ | ✅ | ✅ |
| Pause / Resume | ✅ (sc pause / continue) | ❌ | ❌ |
| Auto Install Helper (first run) | ✅ | ✅ | ✅ |

## Core Concepts

### IServiceManager
Abstraction with platform-specific implementations:
- `WindowsServiceManager` (uses `sc create`, `sc start`, etc.)
- `LinuxServiceManager` (writes unit file, `systemctl daemon-reload`, enable/start)
- `MacOsServiceManager` (writes plist, `launchctl load/start`)

Factory: `ServiceManagerFactory.Create()` picks the correct implementation at runtime.

### AutoServiceRunner
High-level orchestrator that:
1. Detects if running interactively or as a service.
2. Installs service (if elevated and not installed) or runs foreground test mode.
3. Starts the Generic Host with user-supplied service registrations.

### ServiceManagerExtensions
Fluent helpers: `InstallSelfAsync`, `StartAsync`, `StopAsync`, `RemoveAsync`, `StatusAsync`, plus `InstallCurrentApplicationAsync()`.

## Sample Host (DemoHost)

`DemoHost/Program.cs` demonstrates:
- Command-line control:
  ```
  DemoHost install
  DemoHost start
  DemoHost stop
  DemoHost status
  DemoHost remove   # alias: uninstall
  DemoHost name=MyCustomService install
  ```
- Argument passthrough to the worker: any unrecognized tokens become `WorkerArguments`.

Foreground (no service install):
```
DemoHost foo bar --level=3
```

After installation (Windows elevated / Linux sudo / macOS sudo for system daemon):
```
DemoHost status
DemoHost stop
DemoHost start
DemoHost remove
```

## Passing Arguments to the Worker

`WorkerArguments` (record) is registered as a singleton:
```csharp
services.AddSingleton(new WorkerArguments(workerArgList.ToArray()));
services.AddHostedService<Worker>();
```

In `Worker`:
```csharp
public sealed record WorkerArguments(string[] Args);

public Worker(ILogger<Worker> logger, WorkerArguments args) { ... }

protected override async Task ExecuteAsync(CancellationToken ct)
{
    _logger.LogInformation("Args: {Args}", string.Join(' ', _args.Args));
    ...
}
```

For another app (e.g. Localtonet integration):
```csharp
await AutoServiceRunner.RunAsync(
    serviceName,
    description,
    hostBuilder => hostBuilder.ConfigureServices((ctx, services) =>
    {
        services.AddSingleton(new WorkerArguments(tkenArgs));
        services.AddHostedService<Worker>();
    }),
    cancellationToken: CancellationToken.None);
```

## Platform Notes

### Windows
- Requires elevated console (Administrator) to install/remove.
- Error 1053 typically means a blocking operation during service start. Ensure long-running startup work runs in background tasks after the host starts.

### Linux (systemd)
- Requires `sudo` for install/remove (writes unit file to `/etc/systemd/system`).
- Unit auto-start handled via `enable`.
- Pause/Resume not supported.

### macOS (launchd)
- System-level install requires `sudo` (LaunchDaemons). User-level could be adapted to `~/Library/LaunchAgents`.
- Creates plist, sets permissions, `launchctl load` + optional start.
- Pause/Resume not supported.

## Typical Installation Flow

Windows:
```
dotnet publish -c Release -r win-x64 --self-contained false
cd bin/Release/net8.0/win-x64
DemoHost install
DemoHost status
```

Linux:
```
dotnet publish -c Release -r linux-x64 --self-contained false
sudo ./DemoHost install
./DemoHost status
```

macOS:
```
dotnet publish -c Release -r osx-x64 --self-contained false
sudo ./DemoHost install
./DemoHost status
```

Remove:
```
# Any platform (elevated where required)
DemoHost remove
```

## Programmatic Usage (Library Only)

```csharp
using CrossPlatformService.Services;

var manager = ServiceManagerFactory.Create();
await manager.InstallServiceAsync(
    serviceName: "MyService",
    executablePath: Environment.ProcessPath!,
    description: "My cross-platform service",
    environmentVariables: null,
    autoStart: true);

var status = await manager.GetServiceStatusAsync("MyService");
Console.WriteLine(status);
```

## Handling Blocking External SDK Calls

If an external SDK (e.g. tunnel client) has a blocking start method:
```csharp
protected override async Task ExecuteAsync(CancellationToken ct)
{
    _ = Task.Run(() => blockingClient.Start(token), ct); // Offload
    while (!ct.IsCancellationRequested)
    {
        _logger.LogInformation("Heartbeat {Time}", DateTimeOffset.Now);
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
    }
}
```
Never block inside `ExecuteAsync` before the first awaited call, or the service may time out.

## Environment Variables / Future Enhancements
- Linux & macOS support injecting environment variables (unit/plist).
- Windows currently leaves a placeholder (would require wrapper or registry ImagePath manipulation).
- Possible future: persistent argument/config store, structured logging, health endpoints.

## Limitations
- Pause/Resume only functional on Windows.
- No built-in secure secret storage (use OS or external vault).
- macOS user-agent mode not fully exposed (system daemon default).
- No retry/backoff policy baked in.

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Windows Error 1053 | Blocking startup | Offload blocking code, ensure async flow |
| Service installs but not auto-starting | Missing elevation or start failure | Check logs / event viewer |
| macOS plist load fails | Permission / malformed plist | Ensure root, validate XML |
| Linux status Unknown | Unit not enabled or name mismatch | `systemctl status <name>` manually |

Check Windows Event Viewer (Application log) or Linux `journalctl -u <service>` / macOS `log show --predicate 'process == "<name>"'`.

## Logging
- Uses standard `Microsoft.Extensions.Logging` abstractions.
- Redirect systemd / launchd stdout/stderr (macOS plist already sets log paths; Linux sample comments show how to append logs).

## Security
- Always validate / sanitize tokens or secrets passed as arguments.
- Prefer environment variables or configuration providers for sensitive values.

## Repository Layout

```
CrossPlatformService/
  Auto/AutoServiceRunner.cs
  Platform/Windows/* (Windows service implementation)
  Platform/Linux/*   (systemd implementation)
  Platform/Mac/*     (launchd implementation)
  Services/*         (IServiceManager + extensions)
  Utilities/*        (Privilege + Process runner)
DemoHost/
  Program.cs         (CLI + auto + argument passthrough)
  Worker.cs          (Sample background job)
```

## License
MIT

## Quick Start (Minimal)

```bash
git clone https://github.com/ShiftyTR/CrossPlatformService.git
cd CrossPlatformService
dotnet build
cd DemoHost/bin/Debug/net8.0
# Test foreground
./DemoHost arg1 arg2
# Install (platform specific elevation)
./DemoHost install
./DemoHost status
```

## Contributing
Open issues or PRs for:
- Environment variable injection on Windows
- Extended diagnostics
- Service restart policies (custom)
