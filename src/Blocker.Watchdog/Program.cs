using System.IO.Abstractions;
using Blocker.Core.Security;
using Blocker.Core.Supervision;
using Blocker.Platform.Windows.Processes;
using Blocker.Platform.Windows.Supervision;
using Blocker.Watchdog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (!WatchdogOptions.TryParse(args, out WatchdogOptions? options, out string? error))
{
    await Console.Error.WriteLineAsync($"Blocker.Watchdog: {error}").ConfigureAwait(false);
    return 2;
}

// Arguments are ours (--console, --partner, --data), not host configuration.
HostApplicationBuilder builder = Host.CreateApplicationBuilder();
if (!options.Console)
{
    builder.Services.AddWindowsService(service => service.ServiceName = options.ServiceName);
}

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IFileSystem, FileSystem>();
builder.Services.AddSingleton<IServiceControl, ScmServiceControl>();
builder.Services.AddSingleton<IProcessWaiter, ProcessWaiter>();
builder.Services.AddSingleton(sp => new SupervisionStatusFile(
    new SecureFileStore(
        sp.GetRequiredService<IFileSystem>(),
        new DpapiSecretProtector(),
        new HmacKeyStore(sp.GetRequiredService<IFileSystem>(), new DpapiSecretProtector(), options.KeyPath)),
    options.StatusPath,
    sp.GetRequiredService<ILogger<SupervisionStatusFile>>()));
builder.Services.AddSingleton(sp => new MutualSupervisor(
    options.PartnerServiceName,
    sp.GetRequiredService<IServiceControl>(),
    sp.GetRequiredService<IProcessWaiter>(),
    sp.GetRequiredService<SupervisionStatusFile>().ShouldSuperviseAsync,
    TimeProvider.System,
    sp.GetRequiredService<ILogger<MutualSupervisor>>()));
builder.Services.AddHostedService<WatchdogWorker>();

using IHost host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
return 0;
