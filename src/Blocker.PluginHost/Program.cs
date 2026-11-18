using System.IO.Abstractions;
using Blocker.Core.Events;
using Blocker.Core.Ipc;
using Blocker.Core.Logging;
using Blocker.Core.PluginRuntime;
using Blocker.PluginHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (!PluginHostArguments.TryParse(args, out PluginHostArguments? options, out string? error))
{
    await Console.Error.WriteLineAsync($"Blocker.PluginHost: {error}").ConfigureAwait(false);
    return ExitCodes.BadArguments;
}

// The host's own command line is not host configuration, so no arguments are handed to the builder.
HostApplicationBuilder builder = Host.CreateApplicationBuilder();

// The default providers do not suit a low-integrity process without a console: the Event Log provider cannot write there and makes
// every warning throw. Log to the plugin data directory instead, the one place this host may write ("host-logs" is never a plugin id).
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new RollingFileLoggerProvider(new FileSystem(), Path.Combine(options.DataDirectory, "host-logs"), TimeProvider.System));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IFileSystem, FileSystem>();
builder.Services.AddSingleton(new VerifiedTimeProvider(TimeProvider.System));
builder.Services.AddSingleton(sp => new PluginStore(sp.GetRequiredService<IFileSystem>(), options.PluginsDirectory, options.DataDirectory));
builder.Services.AddSingleton(sp => new HostEventBus(sp.GetRequiredService<VerifiedTimeProvider>(), sp.GetRequiredService<ILogger<HostEventBus>>()));
builder.Services.AddSingleton<PluginHostRuntime>(sp => new PluginHostRuntime(
    sp.GetRequiredService<PluginStore>(),
    sp.GetRequiredService<HostEventBus>(),
    sp.GetRequiredService<VerifiedTimeProvider>(),
    sp.GetRequiredService<ILogger<PluginHostRuntime>>()));
builder.Services.AddSingleton(sp => new IpcClient(options.PipeName, sp.GetRequiredService<ILogger<IpcClient>>()));
builder.Services.AddSingleton(sp => new PluginHostEndpoint(
    sp.GetRequiredService<IpcClient>(), sp.GetRequiredService<PluginHostRuntime>(), sp.GetRequiredService<ILogger<PluginHostEndpoint>>(), options.Token));
builder.Services.AddHostedService<PluginHostWorker>();

try
{
    using IHost host = builder.Build();
    await host.RunAsync().ConfigureAwait(false);
    return Environment.ExitCode;
}
catch (Exception ex)
{
    // The host runs at low integrity and has no console: leave the reason where the service (and a person) can read it.
    TryWriteCrashLog(options.DataDirectory, ex);
    return ExitCodes.Crashed;
}

static void TryWriteCrashLog(string directory, Exception exception)
{
    try
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin-host-crash.log"), $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{exception}");
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Blocker.PluginHost: could not write the crash log: {ex.Message}");
    }
}

namespace Blocker.PluginHost
{
    /// <summary>Process exit codes of <c>Blocker.PluginHost</c>.</summary>
    internal static class ExitCodes
    {
        public const int BadArguments = 2;
        public const int Refused = 3;
        public const int ConnectionFailed = 4;
        public const int Crashed = 5;
    }
}
