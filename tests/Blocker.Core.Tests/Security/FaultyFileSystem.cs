using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Win32.SafeHandles;

namespace Blocker.Core.Tests.Security;

/// <summary>Raised by <see cref="FaultyFileSystem"/> to simulate a crash while a temp file is being written.</summary>
public sealed class SimulatedCrashException : IOException
{
    public SimulatedCrashException() : base("simulated crash") { }
}

/// <summary>Delegates to a <see cref="MockFileSystem"/>; once armed, creating any file stream throws a <see cref="SimulatedCrashException"/>.</summary>
public sealed class FaultyFileSystem(MockFileSystem inner) : IFileSystem
{
    /// <summary>When true, every stream creation fails.</summary>
    public bool FailStreams { get; set; }

    public MockFileSystem Inner { get; } = inner;

    public IFile File => Inner.File;
    public IDirectory Directory => Inner.Directory;
    public IFileInfoFactory FileInfo => Inner.FileInfo;
    public IFileVersionInfoFactory FileVersionInfo => Inner.FileVersionInfo;
    public IFileStreamFactory FileStream => new FaultyStreamFactory(this);
    public IDirectoryInfoFactory DirectoryInfo => Inner.DirectoryInfo;
    public IDriveInfoFactory DriveInfo => Inner.DriveInfo;
    public IPath Path => Inner.Path;
    public IFileSystemWatcherFactory FileSystemWatcher => Inner.FileSystemWatcher;
    public IRandomAccess RandomAccess => Inner.RandomAccess;

    private sealed class FaultyStreamFactory(FaultyFileSystem owner) : IFileStreamFactory
    {
        public IFileSystem FileSystem => owner;

        public FileSystemStream New(string path, FileMode mode) => Guard(() => owner.Inner.FileStream.New(path, mode));
        public FileSystemStream New(string path, FileMode mode, FileAccess access) => Guard(() => owner.Inner.FileStream.New(path, mode, access));
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share) => Guard(() => owner.Inner.FileStream.New(path, mode, access, share));
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize) => Guard(() => owner.Inner.FileStream.New(path, mode, access, share, bufferSize));
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, bool useAsync) => Guard(() => owner.Inner.FileStream.New(path, mode, access, share, bufferSize, useAsync));
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options) => Guard(() => owner.Inner.FileStream.New(path, mode, access, share, bufferSize, options));
        public FileSystemStream New(string path, FileStreamOptions options) => Guard(() => owner.Inner.FileStream.New(path, options));
        public FileSystemStream New(SafeFileHandle handle, FileAccess access) => Guard(() => owner.Inner.FileStream.New(handle, access));
        public FileSystemStream New(SafeFileHandle handle, FileAccess access, int bufferSize) => Guard(() => owner.Inner.FileStream.New(handle, access, bufferSize));
        public FileSystemStream New(SafeFileHandle handle, FileAccess access, int bufferSize, bool isAsync) => Guard(() => owner.Inner.FileStream.New(handle, access, bufferSize, isAsync));
        public FileSystemStream Wrap(FileStream fileStream) => owner.Inner.FileStream.Wrap(fileStream);

        private FileSystemStream Guard(Func<FileSystemStream> create) =>
            owner.FailStreams ? throw new SimulatedCrashException() : create();
    }
}
