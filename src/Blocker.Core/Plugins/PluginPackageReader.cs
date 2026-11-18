using System.Collections.Frozen;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;

namespace Blocker.Core.Plugins;

/// <summary>A validated plugin ZIP: manifest plus the text of every file. Nothing is ever written to disk.</summary>
/// <param name="Manifest">Parsed <c>plugin.json</c>.</param>
/// <param name="Sources">C# sources by normalized path (forward slashes), in ordinal order.</param>
/// <param name="Files">All other files (<c>.json</c>, <c>.md</c>, including <c>plugin.json</c>) by normalized path.</param>
public sealed record PluginPackage(
    PluginManifest Manifest,
    IReadOnlyDictionary<string, string> Sources,
    IReadOnlyDictionary<string, string> Files);

/// <summary>Outcome of <see cref="PluginPackageReader.ReadAsync"/>.</summary>
/// <param name="Package">The package; <see langword="null"/> when <paramref name="Errors"/> is not empty.</param>
/// <param name="Errors">Human-readable errors.</param>
/// <param name="FailedStage"><see cref="PluginInstallStage.Package"/> or <see cref="PluginInstallStage.Manifest"/> on failure; <see langword="null"/> on success.</param>
public sealed record PluginPackageReadResult(PluginPackage? Package, IReadOnlyList<string> Errors, PluginInstallStage? FailedStage)
{
    /// <summary>Gets whether the package is valid.</summary>
    public bool Succeeded => Package is not null;
}

/// <summary>
/// Validates a plugin ZIP and reads it into memory. The archive is untrusted: entry names are validated, sizes are
/// enforced by counting bytes actually decompressed (header sizes are not trusted), and nothing is extracted to disk.
/// </summary>
public static class PluginPackageReader
{
    /// <summary>Maximum size of the ZIP file.</summary>
    public const long MaxCompressedBytes = 5 * 1024 * 1024;

    /// <summary>Maximum total size of all entries after decompression.</summary>
    public const long MaxUnpackedBytes = 2 * 1024 * 1024;

    /// <summary>Maximum number of entries (files and directories).</summary>
    public const int MaxEntries = 50;

    /// <summary>Maximum compression ratio of a single entry.</summary>
    public const int MaxCompressionRatio = 100;

    /// <summary>Maximum number of path segments of an entry name.</summary>
    public const int MaxDepth = 3;

    private const int MaxNameLength = 200;
    private const int MaxIgnoredEntries = 200;
    private const string IgnoredRoot = ".claude";
    private const string ManifestName = "plugin.json";

    private static readonly FrozenSet<string> AllowedExtensions = new[] { ".cs", ".json", ".md" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Reads and validates a plugin ZIP.</summary>
    /// <param name="zip">Readable stream containing the ZIP. It is not disposed.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The package, or every validation error found in the first failing phase.</returns>
    public static async Task<PluginPackageReadResult> ReadAsync(Stream zip, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zip);
        using var buffer = new MemoryStream();
        if (await CopyAsync(zip, buffer, MaxCompressedBytes, cancellationToken).ConfigureAwait(false) > MaxCompressedBytes)
        {
            return Fail($"The ZIP file is larger than {MaxCompressedBytes / 1024 / 1024} MB.");
        }

        buffer.Position = 0;
        try
        {
            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
            return await ReadArchiveAsync(archive, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            return Fail($"The file is not a valid ZIP archive: {PluginText.Printable(ex.Message, 200)}");
        }
    }

    private static async Task<PluginPackageReadResult> ReadArchiveAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > MaxEntries + MaxIgnoredEntries)
        {
            return Fail($"The ZIP contains {archive.Entries.Count} entries; at most {MaxEntries} are allowed.");
        }

        List<string> errors = [];
        int counted = 0;
        List<(ZipArchiveEntry Entry, string Name)> files = [];
        Dictionary<string, string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (IsIgnored(name))
            {
                continue;
            }

            counted++;
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue;
            }

            string label = PluginText.Printable(entry.FullName, MaxNameLength);
            string? problem = NameProblem(name) ?? ExtensionProblem(name);
            if (problem is not null)
            {
                errors.Add($"Entry '{label}': {problem}");
            }
            else if (!seen.TryAdd(name, label))
            {
                errors.Add($"Entry '{label}' duplicates '{seen[name]}' (names are compared case-insensitively).");
            }
            else
            {
                files.Add((entry, name));
            }
        }

        if (counted > MaxEntries)
        {
            return Fail($"The ZIP contains {counted} entries; at most {MaxEntries} are allowed.");
        }

        if (errors.Count == 0 && !files.Any(f => string.Equals(f.Name, ManifestName, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"{ManifestName} is missing at the ZIP root.");
        }

        if (errors.Count > 0)
        {
            return new PluginPackageReadResult(null, errors, PluginInstallStage.Package);
        }

        return await ReadContentsAsync(files, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PluginPackageReadResult> ReadContentsAsync(List<(ZipArchiveEntry Entry, string Name)> files, CancellationToken cancellationToken)
    {
        SortedDictionary<string, string> sources = new(StringComparer.Ordinal);
        SortedDictionary<string, string> others = new(StringComparer.Ordinal);
        long total = 0;
        foreach ((ZipArchiveEntry entry, string name) in files)
        {
            long remaining = MaxUnpackedBytes - total;
            long ratioCap = Math.Max(entry.CompressedLength, 1) * MaxCompressionRatio;
            long cap = Math.Min(remaining, ratioCap);
            using var content = new MemoryStream();
            Stream stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                long read;
                try
                {
                    read = await CopyAsync(stream, content, cap, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException ex)
                {
                    return Fail($"Entry '{PluginText.Printable(name, MaxNameLength)}' is corrupt: {PluginText.Printable(ex.Message, 200)}");
                }

                if (read > cap)
                {
                    return Fail(ratioCap < remaining
                        ? $"Entry '{PluginText.Printable(name, MaxNameLength)}' has a compression ratio above {MaxCompressionRatio}:1 (possible ZIP bomb)."
                        : $"The ZIP unpacks to more than {MaxUnpackedBytes / 1024 / 1024} MB.");
                }
            }

            total += content.Length;
            string? text = Decode(content.GetBuffer().AsSpan(0, (int)content.Length));
            if (text is null)
            {
                return Fail($"Entry '{PluginText.Printable(name, MaxNameLength)}' is not valid UTF-8 text.");
            }

            (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? sources : others)[name] = text;
        }

        string manifestKey = others.Keys.First(k => string.Equals(k, ManifestName, StringComparison.OrdinalIgnoreCase));
        PluginManifestParseResult parsed = PluginManifestParser.Parse(others[manifestKey]);
        return parsed.Manifest is { } manifest
            ? new PluginPackageReadResult(
                new PluginPackage(
                    manifest,
                    sources.ToImmutableSortedDictionary(StringComparer.Ordinal),
                    others.ToImmutableSortedDictionary(StringComparer.Ordinal)),
                [],
                null)
            : new PluginPackageReadResult(null, parsed.Errors, PluginInstallStage.Manifest);
    }

    /// <summary>
    /// Authoring tooling (<c>.claude/</c> at the ZIP root, e.g. AI agent skills shipped in the starter template) is never
    /// read, compiled or counted. The name must still be safe; only the depth and extension rules are waived.
    /// </summary>
    private static bool IsIgnored(string name) =>
        (name.StartsWith(IgnoredRoot + "/", StringComparison.OrdinalIgnoreCase)) && NameProblem(name.TrimEnd('/'), checkDepth: false) is null;

    private static string? NameProblem(string name, bool checkDepth = true)
    {
        if (name.Length > MaxNameLength)
        {
            return $"the name is longer than {MaxNameLength} characters.";
        }

        if (name.Any(char.IsControl))
        {
            return "the name contains control characters.";
        }

        if (name.Contains(':'))
        {
            return "the name contains ':' (drive letters and alternate data streams are not allowed).";
        }

        if (name.StartsWith('/'))
        {
            return "absolute paths are not allowed.";
        }

        string[] segments = name.Split('/');
        if (segments.Any(s => s is "." or ".."))
        {
            return "'.' and '..' path segments are not allowed.";
        }

        if (segments.Any(s => s.Length == 0 || s.EndsWith('.') || s.EndsWith(' ')))
        {
            return "path segments must not be empty or end with '.' or a space.";
        }

        return checkDepth && segments.Length > MaxDepth ? $"the path is nested deeper than {MaxDepth} levels." : null;
    }

    private static string? ExtensionProblem(string name)
    {
        string extension = Path.GetExtension(name);
        return AllowedExtensions.Contains(extension)
            ? null
            : $"file type '{(extension.Length == 0 ? "(none)" : PluginText.Printable(extension))}' is not allowed (only .cs, .json and .md).";
    }

    private static string? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Utf8Bom))
        {
            bytes = bytes[3..];
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>Copies at most <paramref name="limit"/> + 1 bytes; a result above <paramref name="limit"/> means the limit was exceeded.</summary>
    private static async Task<long> CopyAsync(Stream source, Stream destination, long limit, CancellationToken cancellationToken)
    {
        byte[] chunk = new byte[16 * 1024];
        long total = 0;
        while (total <= limit)
        {
            int read = await source.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit + 1 - total)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
        }

        return total;
    }

    private static PluginPackageReadResult Fail(string error) => new(null, [error], PluginInstallStage.Package);
}
