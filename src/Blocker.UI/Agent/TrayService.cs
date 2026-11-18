using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Blocker.UI.Native;
using Blocker.UI.Services;
using Blocker.UI.ViewModels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Blocker.UI.Agent;

/// <summary>
/// The agent's notification-area icon: "Open Blocker", the live remaining time as tooltip and, while no session runs, "Exit".
/// A message-only window receives the shell callbacks; all presentation rules live in <see cref="TrayModel"/>. The icon is removed
/// (<c>NIM_DELETE</c>) when the host stops.
/// </summary>
internal sealed partial class TrayService : IHostedService, IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = 0x8001; // WM_APP + 1
    private const int IconSize = 32;

    private readonly IServiceConnection _connection;
    private readonly IUiDispatcher _dispatcher;
    private readonly IMainWindowLauncher _launcher;
    private readonly SessionTimeline _timeline;
    private readonly ILogger<TrayService> _logger;
    private readonly uint _taskbarCreated = TrayNative.TaskbarCreatedMessage();
    private HwndSource? _source;
    private DispatcherTimer? _timer;
    private nint _icon;
    private string _tooltip = string.Empty;

    /// <summary>Creates the tray service.</summary>
    /// <param name="connection">Service connection; its snapshots feed the tooltip and menu.</param>
    /// <param name="dispatcher">UI-thread marshalling.</param>
    /// <param name="launcher">Opens the main window.</param>
    /// <param name="time">Time source for the local countdown.</param>
    /// <param name="logger">Logger.</param>
    public TrayService(IServiceConnection connection, IUiDispatcher dispatcher, IMainWindowLauncher launcher, TimeProvider time, ILogger<TrayService> logger)
    {
        _connection = connection;
        _dispatcher = dispatcher;
        _launcher = launcher;
        _timeline = new SessionTimeline(time);
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _dispatcher.Post(Create);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _dispatcher.Post(Destroy);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => Destroy();

    private void Create()
    {
        if (_source is not null)
        {
            return;
        }

        _source = new HwndSource(new HwndSourceParameters("WindowsDetox.Tray") { ParentWindow = TrayNative.MessageOnlyParent, WindowStyle = 0 });
        _source.AddHook(WndProc);
        _icon = TrayNative.CreateIconFromPixels(IconSize, IconSize, RenderIcon());
        _timeline.Update(_connection.Session);
        _tooltip = TrayModel.Tooltip(_timeline, _connection.IsConnected);
        if (!TrayNative.Add(_source.Handle, IconId, CallbackMessage, _icon, _tooltip))
        {
            LogAddFailed(_logger);
        }

        _connection.SessionChanged += OnSessionChanged;
        _connection.ConnectionChanged += OnConnectionChanged;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshTooltip();
        _timer.Start();
    }

    private void Destroy()
    {
        if (_source is null)
        {
            return;
        }

        _timer?.Stop();
        _timer = null;
        _connection.SessionChanged -= OnSessionChanged;
        _connection.ConnectionChanged -= OnConnectionChanged;
        TrayNative.Remove(_source.Handle, IconId);
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
        TrayNative.DestroyIconHandle(_icon);
        _icon = 0;
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        _timeline.Update(_connection.Session);
        RefreshTooltip();
    }

    private void OnConnectionChanged(object? sender, EventArgs e) => RefreshTooltip();

    private void RefreshTooltip()
    {
        string tooltip = TrayModel.Tooltip(_timeline, _connection.IsConnected);
        if (_source is null || tooltip == _tooltip)
        {
            return;
        }

        _tooltip = tooltip;
        TrayNative.SetTooltip(_source.Handle, IconId, tooltip);
    }

    private nint WndProc(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == CallbackMessage)
        {
            int notification = (int)(lParam & 0xFFFF);
            switch (notification)
            {
                case TrayNative.NinSelect or TrayNative.NinKeySelect:
                    _launcher.OpenOrActivate();
                    break;
                case TrayNative.WmContextMenu:
                    ShowMenu(window, (short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                    break;
            }

            handled = true;
        }
        else if (message == _taskbarCreated && _source is not null)
        {
            // Explorer restarted: its notification area is empty again.
            _ = TrayNative.Add(window, IconId, CallbackMessage, _icon, _tooltip);
        }

        return 0;
    }

    private void ShowMenu(nint window, int x, int y)
    {
        IReadOnlyList<TrayMenuItem> items = TrayModel.Menu(_connection.Session);
        uint chosen = TrayNative.ShowMenu(window, x, y, [.. items.Select(i => ((uint)i.Command + 1, i.Text))]);
        if (chosen == 0)
        {
            return;
        }

        if ((TrayCommand)(chosen - 1) == TrayCommand.Open)
        {
            _launcher.OpenOrActivate();
        }
        else if (!_connection.Session.IsSessionRunning)
        {
            // Re-checked here: a session may have started while the menu was open.
            Application.Current.Shutdown();
        }
    }

    // A ring in the accent colour, drawn once at startup; the pixels are premultiplied BGRA as the icon API expects.
    private static byte[] RenderIcon()
    {
        Color accent = Application.Current.TryFindResource("Blocker.Accent.Color") is Color found ? found : Color.FromRgb(0x3D, 0xD9, 0xC1);
        SolidColorBrush brush = new(accent);
        DrawingVisual visual = new();
        using (DrawingContext context = visual.RenderOpen())
        {
            Point center = new(IconSize / 2d, IconSize / 2d);
            context.DrawEllipse(null, new Pen(brush, 4), center, 11, 11);
            context.DrawEllipse(brush, null, center, 4, 4);
        }

        RenderTargetBitmap bitmap = new(IconSize, IconSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        byte[] pixels = new byte[IconSize * IconSize * 4];
        bitmap.CopyPixels(pixels, IconSize * 4, 0);
        return pixels;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The shell did not accept the tray icon.")]
    private static partial void LogAddFailed(ILogger logger);
}
