using System.IO.Abstractions;
using System.Text.Json;
using Blocker.Core.Security;
using Blocker.Sdk;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.PluginRuntime;

/// <summary>An installed plugin as listed by <see cref="PluginStore"/>.</summary>
/// <param name="Id">Plugin id (also the directory name).</param>
/// <param name="Enabled">Whether the host should load it.</param>
public sealed record StoredPlugin(PluginId Id, bool Enabled);

/// <summary>
/// Installed plugins on disk: <c>{root}/{pluginId}/package.zip</c> and <c>state.json</c> (written by the service), and
/// <c>{dataRoot}/{pluginId}/storage.json</c> (written by the plugin host). Ids are validated with <see cref="PluginId"/>
/// before they touch a path, so no id can escape the roots. All writes are atomic.
/// </summary>
/// <remarks>
/// The service owns <c>root</c>. The host only reads it, and needs write access to <c>dataRoot</c> (grant the low-integrity
/// label there when the host runs restricted).
/// </remarks>
/// <param name="fileSystem">File system.</param>
/// <param name="root">Directory holding one subdirectory per installed plugin.</param>
/// <param name="dataRoot">Directory holding one data subdirectory per plugin.</param>
public sealed class PluginStore(IFileSystem fileSystem, string root, string dataRoot)
{
    /// <summary>File name of the installed ZIP.</summary>
    public const string PackageFileName = "package.zip";

    /// <summary>File name of the enabled flag.</summary>
    public const string StateFileName = "state.json";

    /// <summary>File name of the plugin's key/value storage.</summary>
    public const string StorageFileName = "storage.json";

    /// <summary>Lists installed plugins (those with a package) in ordinal id order. A missing or unreadable state file means disabled.</summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The installed plugins.</returns>
    public async ValueTask<IReadOnlyList<StoredPlugin>> ListAsync(CancellationToken cancellationToken)
    {
        if (!fileSystem.Directory.Exists(root))
        {
            return [];
        }

        List<StoredPlugin> plugins = [];
        foreach (string directory in fileSystem.Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PluginId.TryCreate(fileSystem.Path.GetFileName(directory), out PluginId id) || !fileSystem.File.Exists(PackagePath(id)))
            {
                continue;
            }

            plugins.Add(new StoredPlugin(id, await ReadEnabledAsync(id, cancellationToken).ConfigureAwait(false)));
        }

        return [.. plugins.OrderBy(p => p.Id.Value, StringComparer.Ordinal)];
    }

    /// <summary>Installs or replaces a plugin package and sets its enabled flag.</summary>
    /// <param name="id">Plugin id.</param>
    /// <param name="package">The ZIP bytes (already evaluated by the install pipeline).</param>
    /// <param name="enabled">Initial enabled flag.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when both files are durable.</returns>
    public async ValueTask InstallAsync(PluginId id, ReadOnlyMemory<byte> package, bool enabled, CancellationToken cancellationToken)
    {
        RequireValid(id);
        await AtomicFile.WriteAsync(fileSystem, PackagePath(id), package, cancellationToken).ConfigureAwait(false);
        await WriteStateAsync(id, enabled, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Changes the enabled flag of an installed plugin.</summary>
    /// <param name="id">Plugin id.</param>
    /// <param name="enabled">New flag.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="false"/> when the plugin is not installed.</returns>
    public async ValueTask<bool> SetEnabledAsync(PluginId id, bool enabled, CancellationToken cancellationToken)
    {
        RequireValid(id);
        if (!fileSystem.File.Exists(PackagePath(id)))
        {
            return false;
        }

        await WriteStateAsync(id, enabled, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Deletes a plugin and its data.</summary>
    /// <param name="id">Plugin id.</param>
    /// <returns><see langword="true"/> when something was removed.</returns>
    public bool Remove(PluginId id)
    {
        RequireValid(id);
        bool removed = false;
        foreach (string directory in new[] { PluginDirectory(id), DataDirectory(id) })
        {
            if (fileSystem.Directory.Exists(directory))
            {
                fileSystem.Directory.Delete(directory, recursive: true);
                removed = true;
            }
        }

        return removed;
    }

    /// <summary>Reads the installed ZIP.</summary>
    /// <param name="id">Plugin id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes, or <see langword="null"/> when the plugin is not installed.</returns>
    public async ValueTask<byte[]?> ReadPackageAsync(PluginId id, CancellationToken cancellationToken)
    {
        RequireValid(id);
        string path = PackagePath(id);
        return fileSystem.File.Exists(path) ? await fileSystem.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>Returns the directory of a plugin's package.</summary>
    /// <param name="id">Plugin id.</param>
    /// <returns>The path.</returns>
    public string PluginDirectory(PluginId id)
    {
        RequireValid(id);
        return fileSystem.Path.Combine(root, id.Value);
    }

    /// <summary>Returns the directory of a plugin's data.</summary>
    /// <param name="id">Plugin id.</param>
    /// <returns>The path.</returns>
    public string DataDirectory(PluginId id)
    {
        RequireValid(id);
        return fileSystem.Path.Combine(dataRoot, id.Value);
    }

    /// <summary>Returns the path of a plugin's <c>storage.json</c>.</summary>
    /// <param name="id">Plugin id.</param>
    /// <returns>The path.</returns>
    public string StoragePath(PluginId id) => fileSystem.Path.Combine(DataDirectory(id), StorageFileName);

    /// <summary>Opens the plugin's key/value storage (the file is read on first use).</summary>
    /// <param name="id">Plugin id.</param>
    /// <param name="logger">Logger for damaged files.</param>
    /// <param name="quotaBytes">Quota in bytes.</param>
    /// <returns>The storage; dispose it when the plugin is unloaded.</returns>
    public FileBackedPluginStorage OpenStorage(PluginId id, ILogger logger, long quotaBytes = FileBackedPluginStorage.DefaultQuotaBytes) =>
        new(fileSystem, StoragePath(id), logger, quotaBytes);

    private static void RequireValid(PluginId id)
    {
        if (id.Value is null)
        {
            throw new ArgumentException("The plugin id is not initialized.", nameof(id));
        }
    }

    private string PackagePath(PluginId id) => fileSystem.Path.Combine(PluginDirectory(id), PackageFileName);

    private string StatePath(PluginId id) => fileSystem.Path.Combine(PluginDirectory(id), StateFileName);

    private ValueTask WriteStateAsync(PluginId id, bool enabled, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(
            fileSystem,
            StatePath(id),
            JsonSerializer.SerializeToUtf8Bytes(new PluginState(enabled), PluginRuntimeJsonContext.Default.PluginState),
            cancellationToken);

    private async ValueTask<bool> ReadEnabledAsync(PluginId id, CancellationToken cancellationToken)
    {
        string path = StatePath(id);
        if (!fileSystem.File.Exists(path))
        {
            return false;
        }

        try
        {
            byte[] bytes = await fileSystem.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize(bytes, PluginRuntimeJsonContext.Default.PluginState)?.Enabled ?? false;
        }
        catch (JsonException)
        {
            // Damaged state: fail safe by not loading the plugin until an administrator enables it again.
            return false;
        }
    }
}
