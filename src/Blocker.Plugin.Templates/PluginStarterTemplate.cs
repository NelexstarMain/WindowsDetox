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

    /// <summary>Gets the names of the files contained in the starter ZIP, in ordinal order. They all live at the ZIP root.</summary>
    public static IReadOnlyList<string> FileNames { get; } =
    [
        .. Source.GetManifestResourceNames()
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(name => name[Prefix.Length..])
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>Writes the starter template as a ZIP archive with every file at the archive root.</summary>
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
            await using Stream resource = Source.GetManifestResourceStream(Prefix + fileName)
                ?? throw new InvalidOperationException($"Embedded resource '{Prefix}{fileName}' is missing.");
            await resource.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
        }
    }
}
