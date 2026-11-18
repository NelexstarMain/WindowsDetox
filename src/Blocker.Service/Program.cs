using System.IO.Abstractions;
using Blocker.Core.Logging;
using Blocker.Core.ServiceHost;
using Blocker.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (!ServiceArguments.TryParse(args, out ServiceArguments? arguments, out string? error))
{
    await Console.Error.WriteLineAsync($"Blocker.Service: {error}").ConfigureAwait(false);
    return ExitCodes.BadArguments;
}

BlockerPaths paths = BlockerPaths.ForCurrentInstallation();
var fileSystem = new FileSystem();
bool devMode = DevModeGate.IsEnabled(arguments.DevMode, paths, fileSystem);
bool interactive = arguments.Console || arguments.Command != ServiceCommandKind.Run;

// The command line is ours (--console, --dev-mode, ...), not host configuration, so no arguments are handed to the builder.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
builder.Logging.ClearProviders();
if (interactive)
{
    builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
}
else
{
    builder.Services.AddWindowsService(service => service.ServiceName = BlockerServiceNames.Service);
}

builder.Logging.SetMinimumLevel(devMode ? LogLevel.Debug : LogLevel.Information);
builder.Logging.AddProvider(new RollingFileLoggerProvider(fileSystem, paths.LogsDirectory, TimeProvider.System));
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));

if (arguments.Command != ServiceCommandKind.Run)
{
    using IHost commandHost = builder.Build();
    return await ServiceCommands.RunAsync(
        arguments.Command,
        paths,
        arguments.DevMode,
        commandHost.Services.GetRequiredService<ILoggerFactory>(),
        CancellationToken.None).ConfigureAwait(false);
}

builder.Services.AddSingleton<IHostedService>(sp => new BlockerWorker(paths, devMode, sp.GetRequiredService<ILoggerFactory>()));
using IHost host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
return ExitCodes.Success;
