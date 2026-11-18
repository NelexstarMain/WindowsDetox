using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Blocker.Setup;

/// <summary>Install, upgrade and uninstall steps. Only our own executables are started; never a shell.</summary>
internal sealed partial class SetupEngine(IProgress<string> progress)
{
    public const string ProductName = "Windows Detox";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WindowsDetox";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AgentValueName = "WindowsDetoxAgent";
    private static readonly string[] OwnProcesses = ["Blocker.UI", "Blocker.Stub", "Blocker.PluginHost"];

    // Exit codes of Blocker.Service.exe one-shot commands (src/Blocker.Service/ServiceCommands.cs).
    private const int ExitSessionActive = 3;
    private const int ExitIncomplete = 4;

    public static string InstallDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsDetox");

    private static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindowsDetox");

    private static string ShortcutPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ProductName + ".lnk");

    private static string ServiceExe => Path.Combine(InstallDirectory, "Blocker.Service.exe");

    private static string UiExe => Path.Combine(InstallDirectory, "Blocker.UI.exe");

    public static string Version { get; } = typeof(SetupEngine).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>Gets whether this executable carries the application payload (the downloadable installer).</summary>
    public static bool HasPayload => typeof(SetupEngine).Assembly.GetManifestResourceInfo("payload.zip") is not null;

    public static bool IsInstalled => File.Exists(ServiceExe);

    public async Task InstallAsync(CancellationToken ct)
    {
        if (IsInstalled)
        {
            progress.Report("Removing the previous version...");
            int code = await RunServiceCommandAsync("--uninstall", ct).ConfigureAwait(false);
            if (code == ExitSessionActive)
            {
                throw new SetupException("A detox session is running. Update after it ends.");
            }

            if (code == ExitIncomplete)
            {
                throw new SetupException("The previous version could not undo all of its changes. Restart Windows and try again.");
            }
        }

        StopOwnProcesses();
        progress.Report("Copying files...");
        await Task.Run(ExtractPayload, ct).ConfigureAwait(false);

        progress.Report("Registering services...");
        int install = await RunServiceCommandAsync("--install", ct).ConfigureAwait(false);
        if (install != 0)
        {
            throw new SetupException($"Registering the services failed (code {install}). Details: {Path.Combine(DataDirectory, "logs")}");
        }

        progress.Report("Adding shortcuts...");
        using (RegistryKey run = OpenMachineKey(RunKeyPath))
        {
            run.SetValue(AgentValueName, $"\"{UiExe}\" --agent");
        }

        Shortcut.Create(ShortcutPath, UiExe, InstallDirectory, "Distraction blocker with detox sessions");
        WriteUninstallEntry();
        progress.Report("Done.");
    }

    /// <summary>Uninstalls. Returns <see langword="true"/> when some files are left for removal at the next restart.</summary>
    public async Task<bool> UninstallAsync(CancellationToken ct)
    {
        if (IsInstalled)
        {
            progress.Report("Undoing all changes and removing services...");
            int code = await RunServiceCommandAsync("--uninstall", ct).ConfigureAwait(false);
            if (code == ExitSessionActive)
            {
                throw new SetupException("A detox session is running. Uninstall after it ends.");
            }

            if (code == ExitIncomplete)
            {
                throw new SetupException("Some changes could not be undone. Restart Windows and run the uninstaller again.");
            }
        }

        StopOwnProcesses();
        progress.Report("Removing shortcuts...");
        using (RegistryKey run = OpenMachineKey(RunKeyPath))
        {
            run.DeleteValue(AgentValueName, throwOnMissingValue: false);
        }

        File.Delete(ShortcutPath);
        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);

        progress.Report("Removing files...");
        bool pending = await Task.Run(() => DeleteTree(InstallDirectory) | DeleteTree(DataDirectory), ct).ConfigureAwait(false);
        progress.Report("Done.");
        return pending;
    }

    private static async Task<int> RunServiceCommandAsync(string argument, CancellationToken ct)
    {
        var info = new ProcessStartInfo(ServiceExe, argument)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = InstallDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process process = Process.Start(info) ?? throw new SetupException("Blocker.Service.exe did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return process.ExitCode;
    }

    private void StopOwnProcesses()
    {
        foreach (Process process in OwnProcesses.SelectMany(Process.GetProcessesByName))
        {
            using (process)
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (path is not null && path.StartsWith(InstallDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    progress.Report($"Could not close {process.ProcessName}: {ex.Message}");
                }
            }
        }
    }

    private static void ExtractPayload()
    {
        using Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
            ?? throw new SetupException("This installer is damaged: the application files are missing.");
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        string root = Path.GetFullPath(InstallDirectory) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new SetupException("This installer is damaged: invalid file path.");
            }

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void WriteUninstallEntry()
    {
        long sizeKb = new DirectoryInfo(InstallDirectory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
        using RegistryKey key = OpenMachineKey(UninstallKeyPath);
        key.SetValue("DisplayName", ProductName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "WindowsDetox");
        key.SetValue("DisplayIcon", UiExe);
        key.SetValue("InstallLocation", InstallDirectory);
        key.SetValue("UninstallString", $"\"{Path.Combine(InstallDirectory, "Blocker.Setup.exe")}\" --uninstall");
        key.SetValue("EstimatedSize", (int)Math.Min(sizeKb, int.MaxValue), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static RegistryKey OpenMachineKey(string path) =>
        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).CreateSubKey(path, writable: true);

    /// <summary>Deletes a directory tree; files in use (including this uninstaller) are scheduled for removal at restart.</summary>
    private static bool DeleteTree(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        bool pending = false;
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                pending |= MoveFileEx(file, null, MoveFileDelayUntilReboot);
            }
        }

        foreach (string dir in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(directory))
        {
            try
            {
                Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                pending |= MoveFileEx(dir, null, MoveFileDelayUntilReboot);
            }
        }

        return pending;
    }

    private const int MoveFileDelayUntilReboot = 0x4;

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existingFileName, string? newFileName, int flags);
}

/// <summary>A setup failure with a message meant for the user.</summary>
internal sealed class SetupException(string message) : Exception(message);
