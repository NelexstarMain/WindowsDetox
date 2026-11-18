using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Blocker.UI.Native;

/// <summary>Win32 calls of the tray icon (<c>Shell_NotifyIcon</c>, popup menu, icon creation, window activation).</summary>
internal static unsafe partial class TrayNative
{
    /// <summary>Parent handle of message-only windows (<c>HWND_MESSAGE</c>).</summary>
    public static readonly nint MessageOnlyParent = -3;

    /// <summary>Selecting the icon with the mouse (<c>NIN_SELECT</c>, version 4 notification).</summary>
    public const int NinSelect = 0x400;

    /// <summary>Selecting the icon with the keyboard (<c>NIN_KEYSELECT</c>).</summary>
    public const int NinKeySelect = 0x401;

    /// <summary>Context menu request (<c>WM_CONTEXTMENU</c>).</summary>
    public const int WmContextMenu = 0x007B;

    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4;
    private const uint NotifyIconVersion4 = 4;
    private const uint MfString = 0, TpmReturnCmd = 0x100, TpmRightButton = 2, WmNull = 0;
    private const int SwRestore = 9;

    /// <summary>Registers (or re-registers after an Explorer restart) the icon and switches it to version 4 callbacks.</summary>
    /// <param name="window">Window that receives <paramref name="callbackMessage"/>.</param>
    /// <param name="id">Icon id.</param>
    /// <param name="callbackMessage">Message sent for mouse and keyboard events.</param>
    /// <param name="icon">Icon handle.</param>
    /// <param name="tooltip">Tooltip text.</param>
    /// <returns><see langword="true"/> when the shell accepted the icon.</returns>
    public static bool Add(nint window, uint id, uint callbackMessage, nint icon, string tooltip)
    {
        NotifyIconData data = Create(window, id, NifMessage | NifIcon | NifTip, tooltip);
        data.CallbackMessage = callbackMessage;
        data.Icon = icon;
        if (!ShellNotifyIcon(NimAdd, ref data))
        {
            return false;
        }

        data.TimeoutOrVersion = NotifyIconVersion4;
        return ShellNotifyIcon(NimSetVersion, ref data);
    }

    /// <summary>Changes the tooltip.</summary>
    /// <param name="window">Owner window.</param>
    /// <param name="id">Icon id.</param>
    /// <param name="tooltip">Tooltip text (at most 127 characters).</param>
    public static void SetTooltip(nint window, uint id, string tooltip)
    {
        NotifyIconData data = Create(window, id, NifTip, tooltip);
        _ = ShellNotifyIcon(NimModify, ref data);
    }

    /// <summary>Removes the icon from the notification area (<c>NIM_DELETE</c>).</summary>
    /// <param name="window">Owner window.</param>
    /// <param name="id">Icon id.</param>
    public static void Remove(nint window, uint id)
    {
        NotifyIconData data = Create(window, id, 0, string.Empty);
        _ = ShellNotifyIcon(NimDelete, ref data);
    }

    /// <summary>Shows a popup menu at a screen position and waits for the choice.</summary>
    /// <param name="owner">Window that owns the menu.</param>
    /// <param name="x">Screen x.</param>
    /// <param name="y">Screen y.</param>
    /// <param name="items">Command id (non-zero) and text per entry.</param>
    /// <returns>The chosen command id, or zero when the menu was dismissed.</returns>
    public static uint ShowMenu(nint owner, int x, int y, IReadOnlyList<(uint Id, string Text)> items)
    {
        nint menu = CreatePopupMenu();
        if (menu == 0)
        {
            return 0;
        }

        try
        {
            foreach ((uint id, string text) in items)
            {
                _ = AppendMenuW(menu, MfString, id, text);
            }

            // Without foreground ownership the menu would not close when the user clicks elsewhere.
            _ = SetForegroundWindow(owner);
            uint chosen = TrackPopupMenu(menu, TpmReturnCmd | TpmRightButton, x, y, 0, owner, 0);
            _ = PostMessageW(owner, WmNull, 0, 0);
            return chosen;
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    /// <summary>Creates an icon from premultiplied BGRA pixels.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="bgra">Pixels, row after row, four bytes each.</param>
    /// <returns>Icon handle for <see cref="DestroyIconHandle"/>, or zero on failure.</returns>
    public static nint CreateIconFromPixels(int width, int height, ReadOnlySpan<byte> bgra)
    {
        byte[] mask = new byte[(width + 15) / 16 * 2 * height];
        nint color;
        fixed (byte* pixels = bgra)
        {
            color = CreateBitmap(width, height, 1, 32, pixels);
        }

        nint monochrome;
        fixed (byte* bits = mask)
        {
            monochrome = CreateBitmap(width, height, 1, 1, bits);
        }

        try
        {
            IconInfo info = new() { IsIcon = 1, Mask = monochrome, Color = color };
            return color != 0 && monochrome != 0 ? CreateIconIndirect(ref info) : 0;
        }
        finally
        {
            _ = DeleteObject(color);
            _ = DeleteObject(monochrome);
        }
    }

    /// <summary>Destroys an icon made by <see cref="CreateIconFromPixels"/>.</summary>
    /// <param name="icon">Icon handle.</param>
    public static void DestroyIconHandle(nint icon)
    {
        if (icon != 0)
        {
            _ = DestroyIcon(icon);
        }
    }

    /// <summary>Gets the message id the shell broadcasts when Explorer restarts and the icon must be added again.</summary>
    /// <returns>The registered message id.</returns>
    public static uint TaskbarCreatedMessage() => RegisterWindowMessageW("TaskbarCreated");

    /// <summary>Restores (when minimized) and brings a window to the foreground.</summary>
    /// <param name="window">Window handle.</param>
    public static void Activate(nint window)
    {
        if (IsIconic(window))
        {
            _ = ShowWindow(window, SwRestore);
        }

        _ = SetForegroundWindow(window);
    }

    private static NotifyIconData Create(nint window, uint id, uint flags, string tooltip)
    {
        NotifyIconData data = new() { Size = (uint)Unsafe.SizeOf<NotifyIconData>(), Window = window, Id = id, Flags = flags };
        ReadOnlySpan<char> text = tooltip.AsSpan(0, Math.Min(tooltip.Length, 127));
        text.CopyTo(new Span<char>(data.Tip, 128));
        return data;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nuint id, string text);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateIconIndirect(ref IconInfo info);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte* bits);

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint TimeoutOrVersion;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint Mask;
        public nint Color;
    }
}
