using System.IO.Compression;
using System.Reflection;

namespace Blocker.Plugin.Templates;

/// <summary>
/// The plugin starter template (<c>templates/PluginStarter</c>) embedded in this assembly. The UI offers it as the
/// "Download starter" ZIP; users edit it, usually with an AI agent, and upload it again.
/// </summary>
public static class PluginStarterTemplate
{
    private const string Prefix = "PluginStarter/";

    private static readonly Assembly Source = typeof(PluginStarterTemplate).Assembly;

    // Resource names keep the platform's directory separator (RecursiveDir uses '\\' on Windows), so map normalized names back.
    private static readonly Dictionary<string, string> ResourceByFileName = Source.GetManifestResourceNames()
        .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
        .ToDictionary(name => name[Prefix.Length..].Replace('\\', '/'), name => name, StringComparer.Ordinal);

    /// <summary>
    /// Gets the relative paths (forward slashes) of the files contained in the starter ZIP, in ordinal order. Plugin files
    /// live at the ZIP root; AI agent skills live under <c>.claude/</c>, which the plugin installer ignores.
    /// </summary>
    public static IReadOnlyList<string> FileNames { get; } = [.. ResourceByFileName.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Writes the starter template as a ZIP archive with every file at its relative path.</summary>
    /// <param name="destination">Writable stream that receives the archive. It is left open.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the archive has been written and flushed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    public static async Task WriteZipAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        foreach (string fileName in FileNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZipArchiveEntry entry = archive.CreateEntry(fileName, CompressionLevel.Optimal);
            await using Stream entryStream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using Stream resource = Source.GetManifestResourceStream(ResourceByFileName[fileName])
                ?? throw new InvalidOperationException($"Embedded resource '{Prefix}{fileName}' is missing.");
            await resource.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
        }
    }
}
