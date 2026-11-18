using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;

namespace Blocker.Platform.Windows.Processes;

/// <summary>QueryDosDeviceW wrapper feeding <see cref="NtPathConverter"/>.</summary>
[SupportedOSPlatform("windows10.0.19041")]
internal static class DosDevices
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorInsufficientBuffer = 122;

    /// <summary>First NT target of a DOS device name such as <c>C:</c>, or null when the device does not exist.</summary>
    public static string? Query(string deviceName)
    {
        for (var capacity = 1024; capacity <= 65536; capacity *= 2)
        {
            var buffer = new char[capacity];
            var length = PInvoke.QueryDosDevice(deviceName, buffer);
            if (length > 0)
            {
                var end = Array.IndexOf(buffer, '\0');
                return new string(buffer, 0, end < 0 ? (int)length : end);
            }

            var error = Marshal.GetLastWin32Error();
            if (error == ErrorFileNotFound)
            {
                return null;
            }

            if (error != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(error);
            }
        }

        throw new Win32Exception(ErrorInsufficientBuffer);
    }

    /// <summary>Converter backed by the real device table.</summary>
    public static NtPathConverter CreateConverter() => new(Query);
}
