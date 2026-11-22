using System.IO.Abstractions;

namespace Blocker.Core.Security;

/// <summary>Crash-safe replace: write a temp sibling, flush to disk, then move over the target.</summary>
internal static class AtomicFile
{
    private const int BufferSize = 4096;
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromMinutes(1);

    public static async ValueTask WriteAsync(IFileSystem fileSystem, string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        string temp = PrepareTemp(fileSystem, path);
        try
        {
            await using (FileSystemStream stream = Create(fileSystem, temp))
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            fileSystem.File.Move(temp, path, overwrite: true);
            DeleteStaleTemps(fileSystem, path);
        }
        catch
        {
            DeleteIfExists(fileSystem, temp);
            throw;
        }
    }

    public static void Write(IFileSystem fileSystem, string path, ReadOnlySpan<byte> data)
    {
        string temp = PrepareTemp(fileSystem, path);
        try
        {
            using (FileSystemStream stream = Create(fileSystem, temp))
            {
                stream.Write(data);
                stream.Flush(true);
            }

            fileSystem.File.Move(temp, path, overwrite: true);
            DeleteStaleTemps(fileSystem, path);
        }
        catch
        {
            DeleteIfExists(fileSystem, temp);
            throw;
        }
    }

    private static string PrepareTemp(IFileSystem fileSystem, string path)
    {
        string? directory = fileSystem.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            fileSystem.Directory.CreateDirectory(directory);
        }

        return $"{path}.{Guid.NewGuid():N}.tmp";
    }

    /// <summary>Crash leftovers of earlier writes; the age threshold keeps the temp file of a concurrent writer safe.</summary>
    private static void DeleteStaleTemps(IFileSystem fileSystem, string path)
    {
        if (fileSystem.Path.GetDirectoryName(path) is not { Length: > 0 } directory)
        {
            return;
        }

        try
        {
            DateTime threshold = DateTime.UtcNow - StaleTempAge;
            foreach (string stale in fileSystem.Directory.GetFiles(directory, $"{fileSystem.Path.GetFileName(path)}.*.tmp"))
            {
                if (fileSystem.File.GetLastWriteTimeUtc(stale) < threshold)
                {
                    fileSystem.File.Delete(stale);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temp file must never fail a write that already succeeded.
            return;
        }
    }

    private static FileSystemStream Create(IFileSystem fileSystem, string temp) =>
        fileSystem.FileStream.New(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.WriteThrough);

    private static void DeleteIfExists(IFileSystem fileSystem, string path)
    {
        if (fileSystem.File.Exists(path))
        {
            fileSystem.File.Delete(path);
        }
    }
}
