using System.Text;
using System.Text.Json;
using Blocker.Contracts;
using Blocker.Core.Ipc;
using Blocker.Core.PluginRuntime;
using Blocker.Core.Plugins;
using Blocker.Sdk;
using Blocker.Sdk.Extensions;

namespace Blocker.Core.ServiceHost;

// Plugin management and the calls that are forwarded to the plugin host.
internal sealed partial class ServiceIpcHandlers
{
    private async ValueTask<ListPluginsResponse> ListPluginsAsync(IpcRequestContext context, EmptyDto request, CancellationToken cancellationToken)
    {
        IReadOnlyList<StoredPlugin> stored = await deps.Plugins.ListAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> faulted = [.. deps.PluginHost.Plugins.Where(static p => p.State == HostPluginState.Faulted).Select(static p => p.PluginId)];
        List<PluginInfoDto> plugins = new(stored.Count);
        foreach (StoredPlugin plugin in stored)
        {
            PluginInfoDto info = await GetInfoAsync(plugin.Id, cancellationToken).ConfigureAwait(false);
            plugins.Add(info with { Enabled = plugin.Enabled, Faulted = plugin.Enabled && faulted.Contains(plugin.Id.Value) });
        }

        return new ListPluginsResponse(plugins);
    }

    private async ValueTask<InstallPluginResponse> InstallPluginAsync(IpcRequestContext context, InstallPluginRequest request, CancellationToken cancellationToken)
    {
        RequireAdministrator(context);
        RequireNoSession();
        byte[] package;
        try
        {
            package = Convert.FromBase64String(Require(request.PackageBase64));
        }
        catch (FormatException)
        {
            return new InstallPluginResponse(false, null, ["The package is not valid base64."], nameof(PluginInstallStageDto.Package));
        }

        if (package.Length > MaxPackageBytes)
        {
            return new InstallPluginResponse(false, null, ["Plugin packages must be smaller than 3 MB."], nameof(PluginInstallStageDto.Package));
        }

        return await InstallPackageAsync(package, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Installs a plugin shipped with the application (from the protected install directory) through the same pipeline as a
    /// user install: evaluation in the restricted host, lock votes, no session. Callers check the session state first.
    /// </summary>
    /// <param name="package">ZIP package.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The install result.</returns>
    internal async ValueTask<InstallPluginResponse> InstallBundledAsync(byte[] package, CancellationToken cancellationToken)
    {
        RequireNoSession();
        return await InstallPackageAsync(package, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<InstallPluginResponse> InstallPackageAsync(byte[] package, CancellationToken cancellationToken)
    {
        await _pluginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The evaluation compiles and runs the plugin's tests in a separate, restricted host; it never touches the installed plugins.
            PluginInstallReportDto report = await deps.PluginHost.EvaluatePackageAsync(package, cancellationToken).ConfigureAwait(false);
            if (!report.Succeeded)
            {
                return new InstallPluginResponse(false, report.PluginId, report.Errors, report.Stage.ToString(), report.Tests);
            }

            if (!PluginId.TryCreate(report.PluginId, out PluginId id))
            {
                return new InstallPluginResponse(false, null, ["The plugin id is invalid."], nameof(PluginInstallStageDto.Manifest), report.Tests);
            }

            // Replacing a plugin is a removal as far as a lock-policy plugin is concerned.
            bool replacing = (await deps.Plugins.ListAsync(cancellationToken).ConfigureAwait(false)).Any(p => p.Id == id);
            if (await FindVetoAsync(replacing ? LockAction.RemovePlugin : LockAction.ChangeProtectionSettings, cancellationToken).ConfigureAwait(false) is { } veto)
            {
                return new InstallPluginResponse(false, id.Value, [veto], null, report.Tests);
            }

            RequireNoSession(); // the evaluation takes a while: a session may have started meanwhile
            await deps.Plugins.InstallAsync(id, package, enabled: true, cancellationToken).ConfigureAwait(false);
            Forget(id);
            _ = await deps.PluginHost.ReloadAsync(cancellationToken).ConfigureAwait(false);
            LogPluginInstalled(logger, id.Value);
            return new InstallPluginResponse(true, id.Value, [], null, report.Tests);
        }
        finally
        {
            _ = _pluginGate.Release();
        }
    }

    private async ValueTask<OperationResponse> RemovePluginAsync(IpcRequestContext context, RemovePluginRequest request, CancellationToken cancellationToken)
    {
        RequireAdministrator(context);
        RequireNoSession();
        if (!PluginId.TryCreate(request.PluginId, out PluginId id))
        {
            return Failed("The plugin id is invalid.");
        }

        await _pluginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!(await deps.Plugins.ListAsync(cancellationToken).ConfigureAwait(false)).Any(p => p.Id == id))
            {
                return Failed("The plugin is not installed.");
            }

            if (await FindVetoAsync(LockAction.RemovePlugin, cancellationToken).ConfigureAwait(false) is { } veto)
            {
                return Failed(veto);
            }

            // Unload first so the host releases the plugin's files, then delete.
            _ = await deps.Plugins.SetEnabledAsync(id, false, cancellationToken).ConfigureAwait(false);
            _ = await deps.PluginHost.ReloadAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _ = deps.Plugins.Remove(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Failed($"The plugin was disabled but its files could not be deleted: {ex.Message}");
            }

            Forget(id);
            LogPluginRemoved(logger, id.Value);
            return new OperationResponse(true, []);
        }
        finally
        {
            _ = _pluginGate.Release();
        }
    }

    private async ValueTask<OperationResponse> SetPluginEnabledAsync(IpcRequestContext context, SetPluginEnabledRequest request, CancellationToken cancellationToken)
    {
        RequireAdministrator(context);
        RequireNoSession();
        if (!PluginId.TryCreate(request.PluginId, out PluginId id))
        {
            return Failed("The plugin id is invalid.");
        }

        await _pluginGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!request.Enabled && await FindVetoAsync(LockAction.DisablePlugin, cancellationToken).ConfigureAwait(false) is { } veto)
            {
                return Failed(veto);
            }

            if (!await deps.Plugins.SetEnabledAsync(id, request.Enabled, cancellationToken).ConfigureAwait(false))
            {
                return Failed("The plugin is not installed.");
            }

            _ = await deps.PluginHost.ReloadAsync(cancellationToken).ConfigureAwait(false);
            return new OperationResponse(true, []);
        }
        finally
        {
            _ = _pluginGate.Release();
        }
    }

    private async ValueTask<DownloadTemplateResponse> DownloadTemplateAsync(IpcRequestContext context, DownloadTemplateRequest request, CancellationToken cancellationToken)
    {
        if (request.Template is { Length: > 0 } name && !name.Equals("starter", StringComparison.OrdinalIgnoreCase) && !name.Equals("PluginStarter", StringComparison.OrdinalIgnoreCase))
        {
            throw new IpcException(IpcErrorCode.ValidationFailed, "Unknown template.");
        }

        byte[] zip = await deps.PluginTemplate(cancellationToken).ConfigureAwait(false);
        return new DownloadTemplateResponse(TemplateFileName, Convert.ToBase64String(zip));
    }

    private ValueTask<GetContributionsResponse> GetContributionsAsync(IpcRequestContext context, GetContributionsRequest request, CancellationToken cancellationToken) =>
        deps.PluginHost.RenderSlotAsync(request.Slot, cancellationToken);

    private async ValueTask<EmptyDto> SendActionAsync(IpcRequestContext context, SendActionRequest request, CancellationToken cancellationToken)
    {
        OperationResponse response = await deps.PluginHost.SendUiActionAsync(Require(request), cancellationToken).ConfigureAwait(false);
        return response.Success
            ? new EmptyDto()
            : throw new IpcException(IpcErrorCode.ValidationFailed, response.Errors.FirstOrDefault() ?? "The action was not accepted.");
    }

    private async ValueTask<JsonElement> InvokePluginAsync(IpcRequestContext context, JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!IpcMethods.TryParsePluginMethod(context.Method, out string? pluginId, out string? method))
        {
            throw new IpcException(IpcErrorCode.NotFound, "Unknown method.");
        }

        string payload = parameters.GetRawText();
        if (Encoding.UTF8.GetByteCount(payload) > MaxPluginPayloadBytes)
        {
            throw new IpcException(IpcErrorCode.PayloadTooLarge, "Message too large.");
        }

        string response = await deps.PluginHost.InvokePluginMethodAsync(pluginId, method, payload, cancellationToken).ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(response);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new IpcException(IpcErrorCode.Internal, "The plugin returned an invalid response.");
        }
    }

    /// <summary>Returns the user-visible text of the first veto of <paramref name="action"/>, or <see langword="null"/> when nobody objects.</summary>
    private async ValueTask<string?> FindVetoAsync(LockAction action, CancellationToken cancellationToken)
    {
        EvaluateLockResponse votes = await deps.PluginHost.EvaluateLockAsync(action, cancellationToken).ConfigureAwait(false);
        if (votes.Vetoes.Count == 0)
        {
            return null;
        }

        LockVetoDto first = votes.Vetoes[0];
        string reason = Printable(first.Reason, 200);
        return $"Vetoed by {first.PluginId}: {reason}";
    }

    private async ValueTask ThrowOnVetoAsync(LockAction action, CancellationToken cancellationToken)
    {
        if (await FindVetoAsync(action, cancellationToken).ConfigureAwait(false) is { } veto)
        {
            throw new IpcException(IpcErrorCode.ValidationFailed, veto);
        }
    }

    /// <summary>Finds an installed, enabled plugin and what its manifest grants; <see langword="null"/> for anything else.</summary>
    /// <param name="pluginId">Plugin id as stated by the plugin host.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The grant.</returns>
    internal async ValueTask<PluginGrant?> ResolvePluginAsync(string pluginId, CancellationToken cancellationToken)
    {
        if (!PluginId.TryCreate(pluginId, out PluginId id)
            || !(await deps.Plugins.ListAsync(cancellationToken).ConfigureAwait(false)).Any(p => p.Id == id && p.Enabled))
        {
            return null;
        }

        PluginInfoDto info = await GetInfoAsync(id, cancellationToken).ConfigureAwait(false);
        return new PluginGrant(info.Name, info.Capabilities.ToHashSet(StringComparer.Ordinal));
    }

    private async ValueTask<PluginInfoDto> GetInfoAsync(PluginId id, CancellationToken cancellationToken)
    {
        lock (_manifestGate)
        {
            if (_infoCache.TryGetValue(id.Value, out PluginInfoDto? cached) && cached is not null)
            {
                return cached;
            }
        }

        PluginInfoDto info = await ReadInfoAsync(id, cancellationToken).ConfigureAwait(false);
        lock (_manifestGate)
        {
            _infoCache[id.Value] = info;
        }

        return info;
    }

    private async ValueTask<PluginInfoDto> ReadInfoAsync(PluginId id, CancellationToken cancellationToken)
    {
        byte[]? bytes = await deps.Plugins.ReadPackageAsync(id, cancellationToken).ConfigureAwait(false);
        if (bytes is not null)
        {
            using var stream = new MemoryStream(bytes);
            PluginPackageReadResult read = await PluginPackageReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            if (read.Package?.Manifest is { } manifest)
            {
                return new PluginInfoDto(
                    id.Value,
                    manifest.Name,
                    manifest.Version.ToString(),
                    manifest.Author,
                    false,
                    [.. manifest.Capabilities.Select(PluginCapabilities.ToManifestName)]);
            }
        }

        LogManifestUnreadable(logger, id.Value);
        return new PluginInfoDto(id.Value, id.Value, "unknown", "unknown", false, []);
    }

    private void Forget(PluginId id)
    {
        lock (_manifestGate)
        {
            _ = _infoCache.Remove(id.Value);
        }
    }
}
