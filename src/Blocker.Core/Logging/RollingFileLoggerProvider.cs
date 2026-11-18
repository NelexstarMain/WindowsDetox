using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.Logging;

/// <summary>Limits of a <see cref="RollingFileLoggerProvider"/>.</summary>
public sealed record RollingFileLoggerOptions
{
    /// <summary>Gets the lowest level that is written.</summary>
    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;

    /// <summary>Gets the size after which the current file is closed and a numbered one starts.</summary>
    public long MaxFileBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>Gets how many days of log files are kept.</summary>
    public int RetainDays { get; init; } = 14;

    /// <summary>Gets the number of lines that may wait for the writer; further lines are dropped.</summary>
    public int QueueCapacity { get; init; } = 4096;
}

/// <summary>
/// Plain-text logger that writes <c>service-yyyyMMdd.log</c> files (UTC days), starts <c>service-yyyyMMdd.N.log</c> when a file gets too large and
/// deletes files older than the retention. Lines are queued and written by one background task, so logging never blocks the caller on disk I/O;
/// when the queue is full the line is dropped, and a failing disk only increments <see cref="FailedWrites"/>.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider, IAsyncDisposable
{
    private const string Prefix = "service-";
    private const string Extension = ".log";
    private const string DayFormat = "yyyyMMdd";

    private readonly IFileSystem _fileSystem;
    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly RollingFileLoggerOptions _options;
    private readonly Channel<Entry> _queue;
    private readonly Task _writer;
    private string? _currentDay;
    private long _failedWrites;
    private int _disposed;

    /// <summary>Creates the provider and starts its writer.</summary>
    /// <param name="fileSystem">File system.</param>
    /// <param name="directory">Log directory (created on demand).</param>
    /// <param name="time">Time source for timestamps and file names.</param>
    /// <param name="options">Limits; defaults when <see langword="null"/>.</param>
    public RollingFileLoggerProvider(IFileSystem fileSystem, string directory, TimeProvider time, RollingFileLoggerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(time);
        _fileSystem = fileSystem;
        _directory = directory;
        _time = time;
        _options = options ?? new RollingFileLoggerOptions();
        _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Gets the number of lines that could not be written because the disk failed.</summary>
    public long FailedWrites => Interlocked.Read(ref _failedWrites);

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>Stops accepting lines and waits until the queued ones are written.</summary>
    /// <returns>A task that completes when the writer ended.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ = _queue.Writer.TryComplete();
        }

        await _writer.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private static string FileName(string day, int sequence) =>
        sequence == 0 ? $"{Prefix}{day}{Extension}" : $"{Prefix}{day}.{sequence.ToString(CultureInfo.InvariantCulture)}{Extension}";

    private void Enqueue(LogLevel level, string category, string message, Exception? exception)
    {
        DateTime at = _time.GetUtcNow().UtcDateTime;
        StringBuilder line = new();
        line.Append(at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] ").Append(category).Append(": ").Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        _ = _queue.Writer.TryWrite(new Entry(at, line.ToString()));
    }

    private async Task WriteLoopAsync()
    {
        await foreach (Entry entry in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Append(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = Interlocked.Increment(ref _failedWrites);
            }
        }
    }

    private void Append(Entry entry)
    {
        string day = entry.At.ToString(DayFormat, CultureInfo.InvariantCulture);
        _fileSystem.Directory.CreateDirectory(_directory);
        if (day != _currentDay)
        {
            DeleteOldFiles(day);
            _currentDay = day;
        }

        string path = _fileSystem.Path.Combine(_directory, FileName(day, 0));
        if (_fileSystem.File.Exists(path) && _fileSystem.FileInfo.New(path).Length >= _options.MaxFileBytes)
        {
            int sequence = 1;
            while (_fileSystem.File.Exists(_fileSystem.Path.Combine(_directory, FileName(day, sequence))))
            {
                sequence++;
            }

            _fileSystem.File.Move(path, _fileSystem.Path.Combine(_directory, FileName(day, sequence)));
        }

        _fileSystem.File.AppendAllText(path, entry.Line + Environment.NewLine, Encoding.UTF8);
    }

    private void DeleteOldFiles(string today)
    {
        DateTime cutoff = DateTime.ParseExact(today, DayFormat, CultureInfo.InvariantCulture).AddDays(-_options.RetainDays);
        foreach (string file in _fileSystem.Directory.EnumerateFiles(_directory, $"{Prefix}*{Extension}"))
        {
            string name = _fileSystem.Path.GetFileName(file);
            if (name.Length >= Prefix.Length + DayFormat.Length
                && DateTime.TryParseExact(name.AsSpan(Prefix.Length, DayFormat.Length), DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime fileDay)
                && fileDay < cutoff)
            {
                _fileSystem.File.Delete(file);
            }
        }
    }

    private readonly record struct Entry(DateTime At, string Line);

    private sealed class FileLogger(RollingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner._options.MinimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.Enqueue(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
