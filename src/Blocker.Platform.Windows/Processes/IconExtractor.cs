using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Blocker.Platform.Windows.Processes;

/// <summary>Extracts the first large icon of an executable as PNG (GDI: ExtractIconExW, GetIconInfo, GetDIBits).</summary>
[SupportedOSPlatform("windows10.0.19041")]
internal static class IconExtractor
{
    private const uint ExtractFailed = uint.MaxValue;

    /// <summary>PNG of the large (normally 32 px) icon, or null when the file has no extractable icon.</summary>
    public static unsafe byte[]? TryExtractPng(string path)
    {
        var icons = new HICON[1];
        var count = PInvoke.ExtractIconEx(path, 0, icons, default);
        using var icon = new IconHandle(icons[0]);
        if (count == 0 || count == ExtractFailed || icon.IsInvalid)
        {
            return null;
        }

        if (!PInvoke.GetIconInfo(icon, out var info))
        {
            return null;
        }

        using var color = new BitmapHandle(info.hbmColor);
        using var mask = new BitmapHandle(info.hbmMask);
        if (color.IsInvalid)
        {
            return null; // monochrome icon: not worth supporting
        }

        var bitmap = default(BITMAP);
        if (PInvoke.GetObject(color, MemoryMarshal.AsBytes(new Span<BITMAP>(ref bitmap))) == 0 || bitmap.bmWidth <= 0 || bitmap.bmHeight <= 0)
        {
            return null;
        }

        using var screen = new ScreenDc();
        var pixels = ReadPixels(screen, color, bitmap.bmWidth, bitmap.bmHeight);
        if (pixels is null)
        {
            return null;
        }

        // Icons without an alpha channel carry transparency in the AND mask (white = transparent).
        if (!HasAlpha(pixels) && !mask.IsInvalid && ReadPixels(screen, mask, bitmap.bmWidth, bitmap.bmHeight) is { } maskPixels)
        {
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i + 3] = maskPixels[i] == 0 ? (byte)255 : (byte)0;
            }
        }
        else if (!HasAlpha(pixels))
        {
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
        }

        return PngEncoder.EncodeBgra(bitmap.bmWidth, bitmap.bmHeight, pixels);
    }

    private static bool HasAlpha(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static unsafe byte[]? ReadPixels(SafeHandle screen, SafeHandle bitmap, int width, int height)
    {
        var header = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
            },
        };

        var pixels = new byte[checked(width * height * 4)];
        fixed (byte* bits = pixels)
        {
            var lines = PInvoke.GetDIBits(new HDC(screen.DangerousGetHandle()), bitmap, 0, (uint)height, bits, &header, DIB_USAGE.DIB_RGB_COLORS);
            return lines == height ? pixels : null;
        }
    }

    private sealed unsafe class IconHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public IconHandle(HICON icon)
            : base(ownsHandle: true) => SetHandle((IntPtr)icon.Value);

        protected override bool ReleaseHandle() => PInvoke.DestroyIcon(new HICON(handle));
    }

    private sealed unsafe class BitmapHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public BitmapHandle(HBITMAP bitmap)
            : base(ownsHandle: true) => SetHandle((IntPtr)bitmap.Value);

        protected override bool ReleaseHandle() => PInvoke.DeleteObject(new HGDIOBJ(handle));
    }

    private sealed unsafe class ScreenDc : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ScreenDc()
            : base(ownsHandle: true)
        {
            var dc = PInvoke.GetDC(default);
            if (dc.IsNull)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            SetHandle((IntPtr)dc.Value);
        }

        protected override bool ReleaseHandle() => PInvoke.ReleaseDC(default, new HDC(handle)) != 0;
    }
}
